using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace SourceGit.AI
{
    // 工具只描述有限查询参数，不接受 Git 参数、命令或 shell 文本。
    public static class GitChatTools
    {
        public static readonly ChatTool Status = Define("GitStatus", "Read staged, unstaged, untracked and deleted file status. Never modifies the index.", """
            {"type":"object","properties":{}}
            """);
        public static readonly ChatTool Diff = Define("GitDiff", "Read text diff. With no revisions: working tree versus index, or staged versus HEAD. Optional from/to commits compare history. Untracked files are not included; use ReadFile.", """
            {"type":"object","properties":{"path":{"type":"string","description":"Optional literal repository-relative path."},"staged":{"type":"boolean"},"from":{"type":"string","description":"Optional commit/ref."},"to":{"type":"string","description":"Optional commit/ref; requires from."}}}
            """);
        public static readonly ChatTool Log = Define("GitLog", "Read recent commits, optionally for a literal file path.", """
            {"type":"object","properties":{"path":{"type":"string"},"revision":{"type":"string"},"max_count":{"type":"integer","minimum":1,"maximum":50}}}
            """);
        public static readonly ChatTool Blame = Define("GitBlame", "Read commit and author attribution for a bounded line range at a commit (default HEAD), not working-copy lines.", """
            {"type":"object","properties":{"path":{"type":"string"},"revision":{"type":"string"},"start_line":{"type":"integer","minimum":1},"max_lines":{"type":"integer","minimum":1,"maximum":400}},"required":["path"]}
            """);
        public static readonly ChatTool RevisionFile = Define("ReadRevisionFile", "Read a bounded text-file line range at a historical commit. Rejects symlinks, submodules, binary files and repository escapes.", """
            {"type":"object","properties":{"path":{"type":"string"},"revision":{"type":"string"},"start_line":{"type":"integer","minimum":1},"max_lines":{"type":"integer","minimum":1,"maximum":2000}},"required":["path","revision"]}
            """);
        public static readonly IReadOnlyList<ChatTool> All = [Status, Diff, Log, Blame, RevisionFile];

        private static ChatTool Define(string name, string description, string schema) => ChatTool.CreateFunctionTool(name, description, BinaryData.FromString(schema), false);

        public static async Task<ToolChatMessage> ProcessAsync(string id, string name, JsonElement args, string repo, CancellationToken cancellation)
        {
            var path = String(args, "path");
            if (!string.IsNullOrEmpty(path))
            {
                ChatTools.ResolveRepoPath(repo, path);
                path = Path.GetRelativePath(repo, ChatTools.ResolveRepoPath(repo, path)).Replace('\\', '/');
            }
            if (name == Status.FunctionName)
                return new(id, await Run(repo, ["status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=all"], cancellation));
            if (name == Diff.FunctionName)
            {
                var from = String(args, "from");
                var to = String(args, "to");
                if (args.TryGetProperty("staged", out var stagedEl) && stagedEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("staged must be boolean.");
                var staged = stagedEl.ValueKind == JsonValueKind.True;
                if (!string.IsNullOrEmpty(to) && string.IsNullOrEmpty(from))
                    throw new ArgumentException("to requires from.");
                if (staged && (!string.IsNullOrEmpty(from) || !string.IsNullOrEmpty(to)))
                    throw new ArgumentException("staged cannot be combined with revisions.");
                var command = new List<string> { "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--unified=4" };
                if (staged) command.Add("--cached");
                if (!string.IsNullOrEmpty(from)) command.Add(await Commit(repo, from, cancellation));
                if (!string.IsNullOrEmpty(to)) command.Add(await Commit(repo, to, cancellation));
                command.Add("--");
                if (!string.IsNullOrEmpty(path)) command.Add(path);
                return new(id, await Run(repo, command, cancellation));
            }
            var revision = await Commit(repo, String(args, "revision", "HEAD"), cancellation);
            if (name == Log.FunctionName)
            {
                var command = new List<string> { "log", "--no-show-signature", "--no-color", "--no-decorate", "--date=iso-strict", "--format=%H%nAuthor: %an%nDate: %aI%nSubject: %s%n", "-n", Number(args, "max_count", 20, 50).ToString(), revision, "--" };
                if (!string.IsNullOrEmpty(path)) command.Add(path);
                return new(id, await Run(repo, command, cancellation));
            }
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required.");
            var blob = await TextBlob(repo, revision, path, cancellation);
            var start = Number(args, "start_line", 1, int.MaxValue - 2000);
            var max = Number(args, "max_lines", 400, name == Blame.FunctionName ? 400 : 2000);
            if (name == Blame.FunctionName)
            {
                var lines = blob.Length == 0 ? 0 : blob.Count(x => x == '\n') + (blob.EndsWith('\n') ? 0 : 1);
                if (start > lines) return new(id, $"File has {lines} lines at {revision}.");
                max = Math.Min(max, lines - start + 1);
                return new(id, $"Revision: {revision}\nPath: {path}\n" + await Run(repo, ["blame", "--no-textconv", "-l", "-L", $"{start},+{max}", revision, "--", path], cancellation));
            }
            var text = new StringBuilder($"Revision: {revision}\nPath: {path}\n");
            using var reader = new StringReader(blob);
            var lineNumber = 0;
            var returned = 0;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                cancellation.ThrowIfCancellationRequested();
                if (++lineNumber < start) continue;
                if (returned++ == max || text.Length + line.Length > 80000)
                {
                    text.AppendLine("[Result limit reached; request a later start_line.]");
                    break;
                }
                text.Append(lineNumber).Append('|').AppendLine(line);
            }
            return new(id, text.ToString());
        }

        private static string String(JsonElement args, string key, string fallback = "")
        {
            if (!args.TryGetProperty(key, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"{key} must be a string.");
            return value.GetString();
        }

        private static int Number(JsonElement args, string key, int fallback, int max)
        {
            if (!args.TryGetProperty(key, out var value)) return fallback;
            if (!value.TryGetInt32(out var number) || number < 1 || number > max)
                throw new ArgumentException($"{key} must be between 1 and {max}.");
            return number;
        }

        private static async Task<string> Commit(string repo, string revision, CancellationToken cancellation)
        {
            // 不允许 option、reflog、对象路径或查询表达式借 revision 进入 Git；最终只用解析后的 SHA。
            if (string.IsNullOrWhiteSpace(revision) || revision.Length > 200 || revision.StartsWith('-') ||
                revision.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '^' or '~')) || revision.Contains(".."))
                throw new ArgumentException("revision must be a commit hash or a simple ref (optionally ^ or ~).");
            return (await Run(repo, ["rev-parse", "--verify", "--end-of-options", revision + "^{commit}"], cancellation)).Trim();
        }

        private static async Task<string> TextBlob(string repo, string revision, string path, CancellationToken cancellation)
        {
            // 历史树也逐级检查：工作区不存在的历史符号链接不能被当作普通文件读取。
            var parts = path.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                var prefix = string.Join('/', parts.Take(i + 1));
                var entry = (await Run(repo, ["ls-tree", "-z", revision, "--", prefix], cancellation)).TrimEnd('\0');
                var metadata = entry.Split('\t')[0].Split(' ');
                if (metadata.Length != 3 || (i < parts.Length - 1 ? metadata[0] != "040000" : metadata[0] is not ("100644" or "100755")))
                    throw new ArgumentException("Historical path is missing or is not a regular file/tree; symbolic links and submodules are refused.");
            }
            var objectName = revision + ":" + path;
            var size = await Run(repo, ["cat-file", "-s", objectName], cancellation);
            if (!long.TryParse(size.Trim(), out var bytes) || bytes > 8 * 1024 * 1024)
                throw new ArgumentException("Historical text file exceeds 8 MiB.");
            var text = await Run(repo, ["cat-file", "blob", objectName], cancellation, 8 * 1024 * 1024);
            if (text.Contains('\0')) throw new ArgumentException("Binary file skipped.");
            return text;
        }

        private static async Task<string> Run(string repo, IReadOnlyList<string> args, CancellationToken cancellation, int limit = 80000)
        {
            var result = await Commands.GitReadOnlyForAI.ReadAsync(repo, args, cancellation, limit).ConfigureAwait(false);
            if (!result.IsSuccess) throw new InvalidOperationException(result.StdErr);
            return result.StdOut;
        }
    }
}
