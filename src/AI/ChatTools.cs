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

        public static async Task<ToolChatMessage> ProcessWithProgressAsync(ChatToolCall call, string repo, Action<ToolCallProgress> progress, CancellationToken cancellation, Action<ViewModels.AIChatBaseline> onBaseline = null)
        {
            var arguments = call.FunctionArguments.ToString();
            if (arguments.Length > 2000)
                arguments = arguments[..2000] + "…";
            var started = System.Diagnostics.Stopwatch.StartNew();
            progress?.Invoke(new(call.Id, call.FunctionName, arguments, ToolCallState.Running, TimeSpan.Zero));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var result = await ProcessAsync(call, repo, null, null, timeout.Token, onBaseline);
                timeout.Token.ThrowIfCancellationRequested();
                var text = string.Join("\n", result.Content.Select(x => x.Text));
                progress?.Invoke(new(call.Id, call.FunctionName, arguments,
                    text.StartsWith("Tool error:", StringComparison.Ordinal) ? ToolCallState.Failed : ToolCallState.Completed,
                    started.Elapsed, text.Length > 2000 ? text[..2000] + "…" : text));
                return result;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                progress?.Invoke(new(call.Id, call.FunctionName, arguments, ToolCallState.Canceled, started.Elapsed));
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                const string error = "Tool error: Execution timed out after 10 seconds. Narrow the request and try again.";
                progress?.Invoke(new(call.Id, call.FunctionName, arguments, ToolCallState.TimedOut, started.Elapsed, error));
                return new ToolChatMessage(call.Id, error);
            }
            catch (Exception e)
            {
                progress?.Invoke(new(call.Id, call.FunctionName, arguments, ToolCallState.Failed, started.Elapsed, e.Message));
                throw;
            }
        }

        private static ToolChatMessage Error(string id, string text) => new(id, $"Tool error: {text}");

        public static async Task<ToolChatMessage> ProcessAsync(ChatToolCall call, string repo, string amendParent, Action<string> output, CancellationToken cancellation = default, Action<ViewModels.AIChatBaseline> onBaseline = null)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                using var doc = JsonDocument.Parse(call.FunctionArguments);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return Error(call.Id, "Tool arguments must be a JSON object.");

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
                    return await ReadFileAsync(call.Id, doc.RootElement, repo, output, cancellation, onBaseline);

                if (call.FunctionName.Equals(Grep.FunctionName))
                    return await GrepAsync(call.Id, doc.RootElement, repo, output, cancellation);

                if (GitChatTools.All.Any(x => x.FunctionName == call.FunctionName))
                    return await GitChatTools.ProcessAsync(call.Id, call.FunctionName, doc.RootElement, repo, cancellation);

                return Error(call.Id, $"Unsupported tool: {call.FunctionName}");
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // 模型可以修正参数再试；取消仍向上传播，以停止整个工具循环。
                return new ToolChatMessage(call.Id, $"Tool error: {e.Message}");
            }
        }

        private static async Task<ToolChatMessage> ReadFileAsync(string callId, JsonElement args, string repo, Action<string> output, CancellationToken cancellation, Action<ViewModels.AIChatBaseline> onBaseline)
        {
            if (!args.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
                return Error(callId, "path is required");

            var relative = pathEl.GetString();
            output?.Invoke($"ReadFile {relative}");

            string full;
            try
            {
                full = ResolveRepoPath(repo, relative);
            }
            catch (Exception e)
            {
                return Error(callId, e.Message);
            }


            var baseline = await ViewModels.AIChatBaseline.CaptureAsync(repo, relative, cancellation);
            if (baseline.Original == null)
            {
                onBaseline?.Invoke(baseline);
                return Error(callId, "File not found. Its absence was captured for a new-file proposal.");
            }
            // 工具输出和待应用基线使用同一份字节，不能再次读取可能已改变的文件。
            var bytes = baseline.Original;
            if (bytes.AsSpan().IndexOf((byte)0) >= 0)
                return Error(callId, "Binary file skipped.");
            onBaseline?.Invoke(baseline);

            var start = 1;
            if (args.TryGetProperty("start_line", out var startEl) && startEl.TryGetInt32(out var parsedStart) && parsedStart > 1)
                start = parsedStart;
            var max = 400;
            if (args.TryGetProperty("max_lines", out var maxEl) && maxEl.TryGetInt32(out var parsedMax) && parsedMax > 0)
                max = Math.Min(parsedMax, 2000);

            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
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
                return Error(callId, "pattern is required");

            var pattern = patternEl.GetString();
            if (string.IsNullOrEmpty(pattern) || pattern.Length > 200)
                return Error(callId, "pattern is empty or too long.");

            var path = args.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String
                ? pathEl.GetString()
                : string.Empty;
            if (!string.IsNullOrEmpty(path))
            {
                ResolveRepoPath(repo, path);
            }

            output?.Invoke(string.IsNullOrEmpty(path) ? $"Grep {pattern}" : $"Grep {pattern} in {path}");
            var rs = await new Commands.GrepForAI(repo, pattern, path) { CancellationToken = cancellation }.ReadAsync();
            if (!rs.IsSuccess && !string.IsNullOrWhiteSpace(rs.StdErr))
                return Error(callId, rs.StdErr.Trim());
            var text = string.IsNullOrWhiteSpace(rs.StdOut) ? "No matches." : rs.StdOut.Trim();
            return new ToolChatMessage(callId, text.Length > 30000 ? text[..30000] + "\n[truncated]" : text);
        }

        internal static string ResolveRepoPath(string repo, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Path is required.");
            if (path.Split(['/', '\\']).Any(x => x is "." or "..") || path.Contains('\0'))
                throw new InvalidOperationException("Path must not contain traversal segments or NUL.");
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
