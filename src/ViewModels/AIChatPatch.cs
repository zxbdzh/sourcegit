using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public sealed class AIChatBaseline
    {
        public string Path { get; init; }
        public byte[] Original { get; init; }

        public static async Task<AIChatBaseline> CaptureAsync(string repo, string path, CancellationToken cancellation)
        {
            var full = AI.ChatTools.ResolveRepoPath(repo, path);
            if (Directory.Exists(full)) throw new IOException("A file path is required.");
            if (!File.Exists(full)) return new() { Path = path };
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length > 8 * 1024 * 1024) throw new IOException("Patch baseline exceeds 8 MiB.");
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellation);
            return new() { Path = path, Original = buffer.ToArray() };
        }
    }

    // 展示行不参与补丁应用；写入仍使用已校验的原始 Lines。
    public sealed record AIChatPatchLine(string Text)
    {
        public bool IsAdded => Text.StartsWith('+');
        public bool IsDeleted => Text.StartsWith('-');
        public bool IsHeader => Text.StartsWith("@@", StringComparison.Ordinal);
    }

    public sealed class AIChatPatchHunk : ObservableObject
    {
        public int OldStart { get; init; }
        public int OldCount { get; init; }
        public int NewStart { get; init; }
        public int NewCount { get; init; }
        public string Text { get; init; }
        public IReadOnlyList<string> Lines { get; init; }
        public IEnumerable<AIChatPatchLine> DisplayLines => Text.Split('\n').Select(x => new AIChatPatchLine(x));
        public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
        private bool _selected = true;
    }

    public sealed class AIChatPatchFile : ObservableObject
    {
        public string Path { get; init; }
        public string Section { get; init; }
        public bool IsNewFile { get; init; }
        public bool IsDeleted { get; init; }
        public List<AIChatPatchHunk> Hunks { get; init; }
        public AIChatBaseline Baseline { get; init; }
        public string Status { get => _status; private set => SetProperty(ref _status, value); }
        public string Message { get => _message; private set => SetProperty(ref _message, value); }
        public bool HasBaseline => Baseline != null;
        public bool CanApply => !_applied && !_rejected && !_busy && Baseline != null;
        public bool CanUndo => _applied && !_busy;
        public bool CanReject => !_applied && !_rejected && !_busy;

        public void MarkRejected()
        {
            if (!CanReject) return;
            _rejected = true;
            Status = App.Text("AI.Patch.Rejected");
            NotifyActions();
        }

        public async Task<bool> TryApplyAsync(string repo)
        {
            if (!CanApply) return false;
            _busy = true;
            NotifyActions();
            try
            {
                var selected = Hunks.Where(x => x.IsSelected).ToList();
                if (selected.Count == 0) throw new IOException("Select at least one hunk.");
                if ((IsNewFile || IsDeleted) && selected.Count != Hunks.Count)
                    throw new IOException("Creation and deletion require all hunks.");
                if (IsNewFile != (Baseline.Original == null)) throw new IOException(App.Text("AI.Patch.Stale"));
                var candidate = IsDeleted ? null : BuildContent(Baseline.Original, selected);
                // 删除也先验证完整区块，不能仅凭模型声明就删文件。
                if (IsDeleted && BuildContent(Baseline.Original, selected).Length != 0)
                    throw new IOException("Deletion patch does not remove all content.");
                await WriteCheckedAsync(repo, Baseline.Original, candidate);
                _after = candidate;
                _applied = true;
                Status = App.Text("AI.Patch.Applied");
                Message = string.Empty;
                return true;
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
            {
                Message = error.Message;
                Status = App.Text("AI.Patch.FailedState");
                return false;
            }
            finally { _busy = false; NotifyActions(); }
        }

        public async Task<bool> TryUndoAsync(string repo)
        {
            if (!CanUndo) return false;
            _busy = true;
            NotifyActions();
            try
            {
                await WriteCheckedAsync(repo, _after, Baseline.Original);
                _applied = false;
                _rejected = true;
                Status = App.Text("AI.Patch.Undone");
                Message = string.Empty;
                return true;
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
            {
                Message = error.Message;
                return false;
            }
            finally { _busy = false; NotifyActions(); }
        }

        private async Task WriteCheckedAsync(string repo, byte[] expected, byte[] candidate)
        {
            var full = AI.ChatTools.ResolveRepoPath(repo, Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
            var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full), $".sourcegit-ai-{Guid.NewGuid():N}.tmp");
            try
            {
                if (candidate != null) await File.WriteAllBytesAsync(temp, candidate);
                // 写入前再次校验路径和原始字节；不允许覆盖等待模型期间产生的新改动。
                AI.ChatTools.ResolveRepoPath(repo, Path);
                if (expected == null)
                {
                    if (File.Exists(full) || Directory.Exists(full)) throw new IOException(App.Text("AI.Patch.Stale"));
                    File.Move(temp, full, false);
                }
                else
                {
                    byte[] currentBytes;
                    await using (var current = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var bytes = new MemoryStream())
                    {
                        await current.CopyToAsync(bytes);
                        currentBytes = bytes.ToArray();
                    }
                    if (!expected.AsSpan().SequenceEqual(currentBytes)) throw new IOException(App.Text("AI.Patch.Stale"));
                    if (candidate == null) File.Delete(full);
                    else File.Move(temp, full, true);
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static byte[] BuildContent(byte[] original, List<AIChatPatchHunk> hunks)
        {
            var utf8 = new UTF8Encoding(false, true);
            var bom = original != null && original.AsSpan().StartsWith(new byte[] { 239, 187, 191 });
            var text = original == null ? string.Empty : utf8.GetString(original.AsSpan(bom ? 3 : 0));
            if (text.Contains('\0')) throw new IOException("Binary files cannot be patched.");
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = text.Replace("\r\n", "\n");
            if (normalized.Contains('\r')) throw new IOException("Mixed/legacy line endings cannot be patched.");
            var lines = normalized.Length == 0 ? new List<string>() : normalized.Split('\n').ToList();
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            var output = new List<string>();
            var cursor = 0;
            var endsWithNewline = normalized.EndsWith('\n') || original == null;
            foreach (var hunk in hunks)
            {
                var index = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
                if (index < cursor || index > lines.Count) throw new IOException("Hunk range is invalid or overlaps.");
                output.AddRange(lines.Skip(cursor).Take(index - cursor));
                cursor = index;
                string previous = null;
                foreach (var line in hunk.Lines)
                {
                    if (line.StartsWith("\\ No newline", StringComparison.Ordinal))
                    {
                        if (previous != null && previous[0] != '-') endsWithNewline = false;
                        continue;
                    }
                    var content = line[1..];
                    if (line[0] != '+')
                    {
                        if (cursor >= lines.Count || lines[cursor] != content) throw new IOException("Patch context does not match the frozen baseline.");
                        cursor++;
                    }
                    if (line[0] != '-')
                    {
                        output.Add(content);
                        // 新行默认带换行；只有紧随该行的 EOF 标记才能取消它。
                        endsWithNewline = true;
                    }
                    previous = line;
                }
            }
            // 未触及的文件尾部沿用原始行尾，不能被前面区块的新行改变。
            if (cursor < lines.Count)
                endsWithNewline = normalized.EndsWith('\n');
            output.AddRange(lines.Skip(cursor));
            var result = string.Join(newline, output) + (output.Count > 0 && endsWithNewline ? newline : string.Empty);
            var encoded = utf8.GetBytes(result);
            return bom && encoded.Length > 0 ? new byte[] { 239, 187, 191 }.Concat(encoded).ToArray() : encoded;
        }

        private void NotifyActions()
        {
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanReject));
        }
        private string _status = App.Text("AI.Patch.Pending");
        private string _message = string.Empty;
        private byte[] _after;
        private bool _applied;
        private bool _rejected;
        private bool _busy;
    }

    public static partial class AIChatPatch
    {
        [GeneratedRegex("^```diff[^\\S\\r\\n]*\\r?\\n(.*?)^```[^\\S\\r\\n]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
        private static partial Regex Fence();
        [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
        private static partial Regex Header();

        public static List<AIChatPatchFile> Parse(string reply, string repo, IReadOnlyDictionary<string, AIChatBaseline> baselines)
        {
            var result = new List<AIChatPatchFile>();
            foreach (Match fence in Fence().Matches(reply ?? string.Empty))
            {
                var body = fence.Groups[1].Value.Replace("\r\n", "\n");
                var sections = Regex.Split(body, "(?=^diff --git )", RegexOptions.Multiline);
                foreach (var section in sections.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    var lines = section.Split('\n');
                    var old = lines.FirstOrDefault(x => x.StartsWith("--- ", StringComparison.Ordinal));
                    var next = lines.FirstOrDefault(x => x.StartsWith("+++ ", StringComparison.Ordinal));
                    if (old == null || next == null) throw new ArgumentException("Patch requires old and new file headers.");
                    var added = old == "--- /dev/null";
                    var deleted = next == "+++ /dev/null";
                    var path = deleted ? old[6..] : next[6..];
                    if ((!added && !old.StartsWith("--- a/", StringComparison.Ordinal)) || (!deleted && !next.StartsWith("+++ b/", StringComparison.Ordinal)))
                        throw new ArgumentException("Use a/ and b/ paths or /dev/null.");
                    AI.ChatTools.ResolveRepoPath(repo, path);
                    if (!added && !deleted && old[6..] != path) throw new ArgumentException("Renames require a separate reviewed operation.");
                    if (result.Any(x => x.Path == path)) throw new ArgumentException("Duplicate patch file.");
                    if (section.Contains("GIT binary patch", StringComparison.Ordinal) || lines.Any(x => x.StartsWith("new mode", StringComparison.Ordinal) || x.StartsWith("old mode", StringComparison.Ordinal) || x.Contains("120000", StringComparison.Ordinal) || x.Contains("160000", StringComparison.Ordinal)))
                        throw new ArgumentException("Binary, mode, symlink and submodule patches are refused.");
                    var hunks = new List<AIChatPatchHunk>();
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (!lines[i].StartsWith("@@", StringComparison.Ordinal)) continue;
                        var match = Header().Match(lines[i]);
                        if (!match.Success) throw new ArgumentException("Malformed hunk header.");
                        var oldStart = int.Parse(match.Groups[1].Value);
                        var oldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
                        var newStart = int.Parse(match.Groups[3].Value);
                        var newCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;
                        var header = lines[i];
                        var content = new List<string>();
                        var removed = 0;
                        var inserted = 0;
                        while (++i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal))
                        {
                            var line = lines[i];
                            if (line.Length == 0 && i == lines.Length - 1) break;
                            if (line.StartsWith("\\ No newline", StringComparison.Ordinal)) { content.Add(line); continue; }
                            if (line.Length == 0 || line[0] is not (' ' or '+' or '-')) throw new ArgumentException("Invalid hunk line.");
                            if (line[0] != '+') removed++;
                            if (line[0] != '-') inserted++;
                            content.Add(line);
                        }
                        i--;
                        if (removed != oldCount || inserted != newCount) throw new ArgumentException("Hunk line counts do not match.");
                        hunks.Add(new() { OldStart = oldStart, OldCount = oldCount, NewStart = newStart, NewCount = newCount, Lines = content, Text = header + "\n" + string.Join("\n", content) });
                    }
                    if (hunks.Count == 0) throw new ArgumentException("No text hunks in patch.");
                    baselines.TryGetValue(path, out var baseline);
                    result.Add(new() { Path = path, Section = section, IsNewFile = added, IsDeleted = deleted, Hunks = hunks, Baseline = baseline });
                }
            }
            return result;
        }
    }
}
