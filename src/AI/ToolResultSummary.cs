using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SourceGit.AI
{
    public sealed record ToolSummary(string Scope, string Text)
    {
        public static readonly ToolSummary Empty = new(string.Empty, string.Empty);
    }

    // 摘要只由工具参数和完整原始结果计算，不改变回传给模型的内容；原始详情始终保留。
    public static partial class ToolResultSummary
    {
        [GeneratedRegex("^\\^?[0-9a-fA-F]{7,64}\\b")]
        private static partial Regex BlameCommit();
        [GeneratedRegex("^(\\d+)\\|")]
        private static partial Regex NumberedLine();

        public static ToolSummary Summarize(string name, string argumentsJson, string result)
        {
            if (string.IsNullOrEmpty(result) || result.StartsWith("Tool error:", StringComparison.Ordinal))
                return ToolSummary.Empty;

            JsonElement args = default;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                args = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // 参数无法解析时仍按结果文本计数，范围留空而不是猜测。
            }

            var truncated = result.Contains("[Result truncated", StringComparison.Ordinal) || result.Contains("[Result limit reached", StringComparison.Ordinal);
            var lines = result.Replace("\r\n", "\n").Split('\n');
            ToolSummary summary = name switch
            {
                "GitStatus" => Status(lines),
                "GitDiff" => Diff(args, lines),
                "GitLog" => Log(args, lines),
                "GitBlame" => Blame(args, lines),
                "ReadRevisionFile" => RevisionFile(args, lines),
                _ => ToolSummary.Empty,
            };
            if (summary == ToolSummary.Empty)
                return summary;
            return truncated ? summary with { Text = summary.Text + " · " + App.Text("AI.Tool.Truncated") } : summary;
        }

        private static ToolSummary Status(string[] lines)
        {
            int staged = 0, unstaged = 0, untracked = 0;
            foreach (var line in lines)
            {
                if (line.Length < 3 || line.StartsWith('[')) continue;
                if (line[0] == '?' && line[1] == '?') { untracked++; continue; }
                if (line[0] != ' ') staged++;
                if (line[1] != ' ') unstaged++;
            }
            var text = staged + unstaged + untracked == 0
                ? App.Text("AI.Tool.StatusClean")
                : App.Text("AI.Tool.Status", staged, unstaged, untracked);
            return new ToolSummary(App.Text("AI.Tool.ScopeStatus"), text);
        }

        private static ToolSummary Diff(JsonElement args, string[] lines)
        {
            int files = 0, added = 0, removed = 0;
            var inHunk = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("diff --git ", StringComparison.Ordinal)) { files++; inHunk = false; continue; }
                if (line.StartsWith("@@", StringComparison.Ordinal)) { inHunk = true; continue; }
                if (!inHunk) continue;
                if (line.StartsWith('+')) added++;
                else if (line.StartsWith('-')) removed++;
            }

            var from = Str(args, "from");
            var to = Str(args, "to");
            var staged = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("staged", out var s) && s.ValueKind == JsonValueKind.True;
            var range = !string.IsNullOrEmpty(from)
                ? $"{Short(from)} → {(string.IsNullOrEmpty(to) ? App.Text("Worktree") : Short(to))}"
                : App.Text(staged ? "AI.Sidebar.Staged" : "AI.Sidebar.Unstaged");
            var path = Str(args, "path");
            var scope = $"{range} · {(string.IsNullOrEmpty(path) ? App.Text("AI.Tool.AllFiles") : path)}";
            return new ToolSummary(scope, files == 0 ? App.Text("AI.Tool.DiffEmpty") : App.Text("AI.Tool.Diff", files, added, removed));
        }

        private static ToolSummary Log(JsonElement args, string[] lines)
        {
            var commits = lines.Count(x => x.Length == 40 && x.All(Uri.IsHexDigit));
            var path = Str(args, "path");
            var revision = Str(args, "revision");
            var scope = $"{(string.IsNullOrEmpty(revision) ? "HEAD" : Short(revision))} · {(string.IsNullOrEmpty(path) ? App.Text("AI.Tool.AllFiles") : path)}";
            return new ToolSummary(scope, App.Text("AI.Tool.Log", commits));
        }

        private static ToolSummary Blame(JsonElement args, string[] lines)
        {
            var body = lines.Where(x => !x.StartsWith("Revision: ", StringComparison.Ordinal) && !x.StartsWith("Path: ", StringComparison.Ordinal) && x.Length > 0 && !x.StartsWith('[')).ToList();
            var commits = body.Select(x => BlameCommit().Match(x)).Where(x => x.Success).Select(x => x.Value.TrimStart('^')).Distinct().Count();
            var start = Number(args, "start_line", 1);
            var scope = $"{Header(lines, "Path: ", Str(args, "path"))} @ {Short(Header(lines, "Revision: ", Str(args, "revision")))} · {App.Text("AI.Tool.FromLine", start)}";
            return new ToolSummary(scope, App.Text("AI.Tool.Blame", body.Count, commits));
        }

        private static ToolSummary RevisionFile(JsonElement args, string[] lines)
        {
            var numbers = lines.Select(x => NumberedLine().Match(x)).Where(x => x.Success).Select(x => int.Parse(x.Groups[1].Value)).ToList();
            var scope = $"{Header(lines, "Path: ", Str(args, "path"))} @ {Short(Header(lines, "Revision: ", Str(args, "revision")))}";
            var text = numbers.Count == 0
                ? App.Text("AI.Tool.NoLines")
                : App.Text("AI.Tool.Lines", numbers[0], numbers[^1], numbers.Count);
            return new ToolSummary(scope, text);
        }

        private static string Header(string[] lines, string prefix, string fallback)
        {
            var line = lines.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
            return line == null ? fallback : line[prefix.Length..].Trim();
        }

        private static string Str(JsonElement args, string key) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

        private static int Number(JsonElement args, string key, int fallback) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.TryGetInt32(out var n) ? n : fallback;

        // 只缩短展示用的完整 SHA；不影响传给 Git 的 revision。
        private static string Short(string revision) =>
            Regex.Replace(revision ?? string.Empty, "\\b[0-9a-fA-F]{40}\\b", match => match.Value[..7]);
    }
}
