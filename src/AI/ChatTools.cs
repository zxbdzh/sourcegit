using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace SourceGit.AI
{
    public static class ChatTools
    {
        public static readonly ChatTool GetDetailChangesInFile = ChatTool.CreateFunctionTool(
            "GetDetailChangesInFile",
            "Get the detailed changes in the specified file in the specified repository.",
            BinaryData.FromBytes(Encoding.UTF8.GetBytes("""
            {
                "type": "object",
                "properties": {
                    "file": {
                        "type": "string",
                        "description": "The path to the file."
                    },
                    "originalFile": {
                        "type": "string",
                        "description": "The path to the original file when it has been renamed or copied."
                    }
                 },
                 "required": ["file"]
            }
            """)), false);

        public static readonly ChatTool ReadFile = ChatTool.CreateFunctionTool(
            "ReadFile",
            "Read a text file from the working copy. Use when the supplied diff is not enough.",
            BinaryData.FromBytes(Encoding.UTF8.GetBytes("""
            {
                "type": "object",
                "properties": {
                    "path": {
                        "type": "string",
                        "description": "Repository-relative path."
                    },
                    "start_line": {
                        "type": "integer",
                        "description": "1-based first line to return."
                    },
                    "max_lines": {
                        "type": "integer",
                        "description": "Maximum number of lines to return."
                    }
                },
                "required": ["path"]
            }
            """)), false);

        public static readonly ChatTool Grep = ChatTool.CreateFunctionTool(
            "Grep",
            "Search the working copy with a regular expression. Returns path:line:text matches.",
            BinaryData.FromBytes(Encoding.UTF8.GetBytes("""
            {
                "type": "object",
                "properties": {
                    "pattern": {
                        "type": "string",
                        "description": "Extended regular expression."
                    },
                    "path": {
                        "type": "string",
                        "description": "Optional repository-relative file, directory, or glob."
                    }
                },
                "required": ["pattern"]
            }
            """)), false);

        public static async Task<ToolChatMessage> ProcessAsync(ChatToolCall call, string repo, string amendParent, Action<string> output, CancellationToken cancellation = default)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                using var doc = JsonDocument.Parse(call.FunctionArguments);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return new ToolChatMessage(call.Id, "Tool arguments must be a JSON object.");

                if (call.FunctionName.Equals(GetDetailChangesInFile.FunctionName))
                {
                    var hasFile = doc.RootElement.TryGetProperty("file", out var filePath);
                    var hasOriginalFile = doc.RootElement.TryGetProperty("originalFile", out var originalFilePath);
                    if (!hasFile)
                        throw new ArgumentException("The file argument is required.");

                    output?.Invoke($"Read changes in file: {filePath.GetString()}");
                    var orgFilePath = hasOriginalFile ? originalFilePath.GetString() : string.Empty;
                    var rs = await new Commands.GetFileChangeForAI(repo, filePath.GetString(), orgFilePath, amendParent)
                    {
                        CancellationToken = cancellation
                    }.ReadAsync();
                    return new ToolChatMessage(call.Id, rs.IsSuccess ? rs.StdOut : rs.StdErr);
                }

                if (call.FunctionName.Equals(ReadFile.FunctionName))
                    return await ReadFileAsync(call.Id, doc.RootElement, repo, output, cancellation);

                if (call.FunctionName.Equals(Grep.FunctionName))
                    return await GrepAsync(call.Id, doc.RootElement, repo, output, cancellation);

                return new ToolChatMessage(call.Id, $"Unsupported tool: {call.FunctionName}");
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // 模型可以修正参数再试；取消仍向上传播，以停止整个工具循环。
                return new ToolChatMessage(call.Id, $"Tool error: {e.Message}");
            }
        }

        private static async Task<ToolChatMessage> ReadFileAsync(string callId, JsonElement args, string repo, Action<string> output, CancellationToken cancellation)
        {
            if (!args.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
                return new ToolChatMessage(callId, "path is required");

            var relative = pathEl.GetString();
            output?.Invoke($"ReadFile {relative}");

            string full;
            try
            {
                full = ResolveRepoPath(repo, relative);
            }
            catch (Exception e)
            {
                return new ToolChatMessage(callId, e.Message);
            }

            if (!File.Exists(full))
                return new ToolChatMessage(callId, "File not found.");

            // 不把大文件一次性装入内存；按行分页，并限制单次返回字符数。
            using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (file.Length > 8 * 1024 * 1024)
                return new ToolChatMessage(callId, "File exceeds the 8 MiB text-file limit. Use Grep to locate relevant lines.");
            var prefix = new byte[4096];
            var prefixLength = await file.ReadAsync(prefix.AsMemory(), cancellation);
            if (prefix.AsSpan(0, prefixLength).IndexOf((byte)0) >= 0)
                return new ToolChatMessage(callId, "Binary file skipped.");
            file.Position = 0;

            var start = 1;
            if (args.TryGetProperty("start_line", out var startEl) && startEl.TryGetInt32(out var parsedStart) && parsedStart > 1)
                start = parsedStart;
            var max = 400;
            if (args.TryGetProperty("max_lines", out var maxEl) && maxEl.TryGetInt32(out var parsedMax) && parsedMax > 0)
                max = Math.Min(parsedMax, 2000);

            using var reader = new StreamReader(file, Encoding.UTF8, true);
            var builder = new StringBuilder().Append("path: ").AppendLine(relative);
            var lineNumber = 0;
            var returned = 0;
            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                lineNumber++;
                if (lineNumber < start)
                    continue;

                builder.Append(lineNumber).Append('|');
                var remaining = Math.Max(0, 80000 - builder.Length);
                builder.AppendLine(line.Length > remaining ? line[..remaining] : line);
                if (++returned == max || builder.Length >= 80000)
                {
                    builder.AppendLine("[Result limit reached; request a later start_line to continue.]");
                    break;
                }
            }

            if (returned == 0)
                builder.Append("File has ").Append(lineNumber).Append(" lines.");
            return new ToolChatMessage(callId, builder.ToString());
        }

        private static async Task<ToolChatMessage> GrepAsync(string callId, JsonElement args, string repo, Action<string> output, CancellationToken cancellation)
        {
            if (!args.TryGetProperty("pattern", out var patternEl) || patternEl.ValueKind != JsonValueKind.String)
                return new ToolChatMessage(callId, "pattern is required");

            var pattern = patternEl.GetString();
            if (string.IsNullOrEmpty(pattern) || pattern.Length > 200)
                return new ToolChatMessage(callId, "pattern is empty or too long.");

            var path = args.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String
                ? pathEl.GetString()
                : string.Empty;
            if (!string.IsNullOrEmpty(path))
            {
                var probe = path.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(probe) || probe.Contains(':') || probe.Split(Path.DirectorySeparatorChar).Contains(".."))
                    return new ToolChatMessage(callId, "Path must be relative to the repository.");
            }

            output?.Invoke(string.IsNullOrEmpty(path) ? $"Grep {pattern}" : $"Grep {pattern} in {path}");
            var rs = await new Commands.GrepForAI(repo, pattern, path) { CancellationToken = cancellation }.ReadAsync();
            var text = string.IsNullOrWhiteSpace(rs.StdOut) ? (string.IsNullOrWhiteSpace(rs.StdErr) ? "No matches." : rs.StdErr.Trim()) : rs.StdOut.Trim();
            return new ToolChatMessage(callId, text.Length > 30000 ? text[..30000] + "\n[truncated]" : text);
        }

        private static string ResolveRepoPath(string repo, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Path is required.");
            if (Path.IsPathRooted(path) || path.Contains(':'))
                throw new InvalidOperationException("Path must be relative to the repository.");

            var root = Path.GetFullPath(repo);
            var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(prefix, comparison))
                throw new InvalidOperationException("Path is outside the repository.");

            var relative = Path.GetRelativePath(root, full);
            var current = root;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
            {
                if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Cannot read .git internals.");
                current = Path.Combine(current, segment);
                // 目录联接或符号链接可能把仓库内路径指向外部，不能只校验字符串前缀。
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symbolic links and junctions cannot be read.");
            }

            return full;
        }
    }
}
