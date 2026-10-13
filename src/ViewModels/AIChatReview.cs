using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public enum AIReviewSeverity
    {
        Critical,
        High,
        Medium,
        Low,
    }

    // 审查卡片只是模型回复的结构化视图；字段缺失时整体回退为原始 Markdown。
    public sealed class AIChatReviewFinding : ObservableObject
    {
        public string Title { get; init; } = string.Empty;
        public AIReviewSeverity Severity { get; init; }
        public string SeverityLabel => App.Text($"AI.Review.Severity.{Severity}");
        public string LocationText { get; init; } = string.Empty;
        public string Path { get; init; }
        public int? LineStart { get; init; }
        public int? LineEnd { get; init; }
        public bool LocationUnverified { get; init; }
        public string Problem { get; init; } = string.Empty;
        public string Evidence { get; init; } = string.Empty;
        public string Impact { get; init; } = string.Empty;
        public string Recommendation { get; init; } = string.Empty;
        public string Verification { get; init; } = string.Empty;

        // 只有明确的“文件 + 行号”且模型没有声明未验证时，才提供定位入口。
        public bool CanLocate => !string.IsNullOrEmpty(Path) && LineStart.HasValue && !LocationUnverified;
        public string LocationLabel => LocationUnverified
            ? $"{LocationText} · {App.Text("AI.Review.Card.Unverified")}"
            : LocationText;
        public bool HasEvidence => !string.IsNullOrWhiteSpace(Evidence);
        // 证据、影响、验证方式合并为一个折叠区；各字段原文保留，缺失字段不显示。
        public string MoreDetails => string.Join("\n\n", new[]
        {
            HasEvidence ? $"{App.Text("AI.Review.Card.Evidence")}: {Evidence}" : null,
            string.IsNullOrWhiteSpace(Impact) ? null : $"{App.Text("AI.Review.Card.Impact")}: {Impact}",
            string.IsNullOrWhiteSpace(Verification) ? null : $"{App.Text("AI.Review.Card.Verification")}: {Verification}",
        }.Where(x => x != null));
        public bool HasMoreDetails => MoreDetails.Length > 0;
        public bool HasImpact => !string.IsNullOrWhiteSpace(Impact);
        public bool HasRecommendation => !string.IsNullOrWhiteSpace(Recommendation);
        public bool HasVerification => !string.IsNullOrWhiteSpace(Verification);

        public bool IsHandled { get => _handled; set => SetProperty(ref _handled, value); }
        public string LocateMessage
        {
            get => _locateMessage;
            set
            {
                if (SetProperty(ref _locateMessage, value))
                    OnPropertyChanged(nameof(HasLocateMessage));
            }
        }
        public bool HasLocateMessage => !string.IsNullOrEmpty(_locateMessage);

        private bool _handled;
        private string _locateMessage = string.Empty;
    }

    public sealed record AIChatReviewResult(List<AIChatReviewFinding> Findings, string Remainder);

    public static partial class AIChatReview
    {
        private const string LabelPattern = "验证方式|验证|修复建议|建议|严重程度|严重性|位置|文件|问题|证据|影响|Severity|Location|Problem|Evidence|Impact|Recommendation|Verification";

        [GeneratedRegex("^\\s*(?:[-*+]\\s+)?[*_]{0,2}(?<label>" + LabelPattern + ")[*_]{0,2}\\s*[:：]\\s*[*_]{0,2}\\s*(?<value>.*)$", RegexOptions.IgnoreCase)]
        private static partial Regex Label();
        [GeneratedRegex("^\\s{0,3}#{1,6}\\s+|^\\s*\\d+[.)]\\s+|^\\s*[*_]{2}[^*_]+[*_]{2}\\s*$")]
        private static partial Regex TitleLine();
        [GeneratedRegex("^\\s{0,3}#{1,3}\\s+")]
        private static partial Regex Heading();
        [GeneratedRegex("(?<path>[\\w@./\\\\+-]*[\\w@+-]\\.[A-Za-z0-9]{1,8})")]
        private static partial Regex PathToken();
        [GeneratedRegex("^\\s*(?:[:：]|#L?|[（(,，]?\\s*(?:lines?|行)\\s*)\\s*(?<a>\\d+)(?:\\s*[-–~]\\s*L?(?<b>\\d+))?", RegexOptions.IgnoreCase)]
        private static partial Regex LineToken();

        // 返回 null 表示没有可靠的结构化结果，调用方必须继续显示原始 Markdown。
        public static AIChatReviewResult Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var lines = text.Replace("\r\n", "\n").Split('\n');
            var severityRows = new List<int>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsSeverity(lines[i]))
                    severityRows.Add(i);
            }
            if (severityRows.Count == 0)
                return null;

            var findings = new List<AIChatReviewFinding>();
            var consumed = new bool[lines.Length];
            for (var n = 0; n < severityRows.Count; n++)
            {
                var severityRow = severityRows[n];
                var start = TitleStart(lines, severityRow, n == 0 ? 0 : severityRows[n - 1] + 1);
                var end = BlockEnd(lines, severityRow, n + 1 < severityRows.Count ? TitleStart(lines, severityRows[n + 1], severityRow + 1) : lines.Length);
                var finding = ParseBlock(lines, start, severityRow, end);
                if (finding == null)
                    return null;

                findings.Add(finding);
                for (var i = start; i < end; i++)
                    consumed[i] = true;
            }

            var remainder = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                if (!consumed[i])
                    remainder.Append(lines[i]).Append('\n');
            }
            return new AIChatReviewResult(findings, Regex.Replace(remainder.ToString(), "\n{3,}", "\n\n").Trim());
        }

        private static bool IsSeverity(string line)
        {
            var match = Label().Match(line);
            return match.Success && IsSeverityLabel(match.Groups["label"].Value);
        }

        private static bool IsSeverityLabel(string label) =>
            label.Equals("Severity", StringComparison.OrdinalIgnoreCase) || label is "严重程度" or "严重性";

        private static int TitleStart(string[] lines, int severityRow, int minimum)
        {
            // 标题行只可能紧贴严重程度之前，避免把上一个发现的正文吞进来。
            for (var i = severityRow - 1; i >= minimum && i >= severityRow - 2; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;
                return TitleLine().IsMatch(lines[i]) && !Label().IsMatch(lines[i]) ? i : severityRow;
            }
            return severityRow;
        }

        private static int BlockEnd(string[] lines, int severityRow, int limit)
        {
            for (var i = severityRow + 1; i < limit; i++)
            {
                // 非发现标题（例如“验证/测试计划”）结束当前卡片，后续内容保留在正文里。
                if (Heading().IsMatch(lines[i]) && !Label().IsMatch(lines[i]))
                    return i;
            }
            return limit;
        }

        private static AIChatReviewFinding ParseBlock(string[] lines, int start, int severityRow, int end)
        {
            var fields = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
            string current = null;
            for (var i = severityRow; i < end; i++)
            {
                var match = Label().Match(lines[i]);
                if (match.Success)
                {
                    current = Canonical(match.Groups["label"].Value);
                    if (!fields.TryGetValue(current, out var builder))
                        fields[current] = builder = new StringBuilder();
                    else if (builder.Length > 0)
                        builder.Append('\n');
                    builder.Append(match.Groups["value"].Value.Trim());
                }
                else if (current != null)
                {
                    fields[current].Append('\n').Append(lines[i]);
                }
            }

            string Value(string key) => fields.TryGetValue(key, out var value) ? value.ToString().Trim() : string.Empty;
            if (!TryParseSeverity(Value("Severity"), out var severity))
                return null;

            var location = Value("Location").Trim('`', ' ');
            var problem = Value("Problem");
            if (location.Length == 0 || problem.Length == 0 || Value("Evidence").Length == 0 || Value("Impact").Length == 0 || Value("Recommendation").Length == 0 || Value("Verification").Length == 0)
                return null;

            var (path, startLine, endLine) = ParseLocation(location);
            var unverified = Regex.IsMatch(location, "not verified|unverified|未验证|无法验证|未确认", RegexOptions.IgnoreCase);
            var title = start < severityRow ? Regex.Replace(lines[start], "^\\s*(?:#{1,6}\\s*|\\d+[.)]\\s*)?[*_]*|[*_]+\\s*$", string.Empty).Trim() : string.Empty;
            return new AIChatReviewFinding
            {
                Title = title,
                Severity = severity,
                LocationText = location.Replace("`", string.Empty),
                Path = path,
                LineStart = startLine,
                LineEnd = endLine,
                LocationUnverified = unverified,
                Problem = problem,
                Evidence = Value("Evidence"),
                Impact = Value("Impact"),
                Recommendation = Value("Recommendation"),
                Verification = Value("Verification"),
            };
        }

        private static string Canonical(string label) => label.ToLowerInvariant() switch
        {
            "severity" or "严重程度" or "严重性" => "Severity",
            "location" or "位置" or "文件" => "Location",
            "problem" or "问题" => "Problem",
            "evidence" or "证据" => "Evidence",
            "impact" or "影响" => "Impact",
            "recommendation" or "建议" or "修复建议" => "Recommendation",
            _ => "Verification",
        };

        private static bool TryParseSeverity(string value, out AIReviewSeverity severity)
        {
            var text = value.ToLowerInvariant();
            severity = AIReviewSeverity.Low;
            if (text.Contains("critical") || text.Contains("严重") || text.Contains("致命")) severity = AIReviewSeverity.Critical;
            else if (text.Contains("high") || text.Contains('高')) severity = AIReviewSeverity.High;
            else if (text.Contains("medium") || text.Contains("moderate") || text.Contains('中')) severity = AIReviewSeverity.Medium;
            else if (text.Contains("low") || text.Contains("minor") || text.Contains('低')) severity = AIReviewSeverity.Low;
            else return false;
            return true;
        }

        private static (string, int?, int?) ParseLocation(string location)
        {
            var clean = location.Replace("`", string.Empty);
            var pathMatch = PathToken().Match(clean);
            if (!pathMatch.Success)
                return (null, null, null);

            var path = pathMatch.Groups["path"].Value.Replace('\\', '/');
            if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            var line = LineToken().Match(clean[(pathMatch.Index + pathMatch.Length)..]);
            if (!line.Success || !int.TryParse(line.Groups["a"].Value, out var a) || a <= 0)
                return (path, null, null);

            int? b = line.Groups["b"].Success && int.TryParse(line.Groups["b"].Value, out var parsedB) && parsedB >= a ? parsedB : null;
            return (path, a, b);
        }
    }
}
