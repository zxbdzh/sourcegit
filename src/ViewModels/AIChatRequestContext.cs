using System;
using System.Collections.Generic;
using System.Text;

namespace SourceGit.ViewModels
{
    public sealed class AIChatRequestContext
    {
        public const int CharacterLimit = 120000;
        public string Text { get; private init; }
        public string Receipt { get; private init; }
        public string Summary { get; private init; }
        public string Overview { get; private init; }

        public static AIChatRequestContext Create(IReadOnlyList<AIChatAttachment> contexts)
        {
            var builder = new StringBuilder();
            var receipt = new List<string>();
            var sent = 0;
            var truncated = 0;
            var omitted = 0;
            foreach (var context in contexts)
            {
                var header = $"Current file: {context.Path}\nComparison: {context.Source}\n<diff>\n";
                const string footer = "\n</diff>\n";
                const string marker = "\n[Diff truncated]";
                var remaining = CharacterLimit - builder.Length - header.Length - footer.Length;
                var patch = context.Patch ?? string.Empty;
                var isTruncated = patch.Length > remaining;
                var length = isTruncated ? remaining - marker.Length : patch.Length;
                // 标题、分隔符和截断标记也占预算；放不下的文件必须逐项明确省略。
                if (length < 0)
                {
                    omitted++;
                    receipt.Add($"{context.Path} · {App.Text("AI.Sidebar.Omitted")}");
                    continue;
                }

                builder.Append(header).Append(patch.AsSpan(0, length));
                if (isTruncated)
                {
                    truncated++;
                    builder.Append(marker);
                }
                builder.Append(footer);
                sent++;
                receipt.Add(context.Path + (isTruncated ? $" · {App.Text("AI.Sidebar.Truncated")}" : string.Empty));
            }

            var summary = App.Text("AI.Sidebar.Statistics", sent, contexts.Count, builder.Length, CharacterLimit, truncated, omitted);
            return new AIChatRequestContext
            {
                Text = builder.ToString(),
                Summary = summary,
                Overview = App.Text("AI.Sidebar.SendScope", sent, builder.Length)
                    + (truncated + omitted > 0 ? " · " + App.Text("AI.Sidebar.ContextWarnings", truncated, omitted) : string.Empty),
                Receipt = receipt.Count > 0 ? summary + Environment.NewLine + string.Join(Environment.NewLine, receipt) : string.Empty,
            };
        }
    }
}
