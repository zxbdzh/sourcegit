using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class DiffContext : ObservableObject
    {
        public string Title
        {
            get;
        }

        public string AIContextKey => _option.ToString();
        public string AIPath => _option.Path;
        public string AISource => _option.IsLocalChange
            ? App.Text(_option.IsUnstaged ? "AI.Sidebar.Unstaged" : "AI.Sidebar.Staged")
            : string.Join(" → ", _option.Revisions.ConvertAll(x => string.IsNullOrEmpty(x) || x == "-R" ? App.Text("Worktree") : x));

        public int OldMode
        {
            get => _oldMode;
            private set => SetProperty(ref _oldMode, value);
        }

        public int NewMode
        {
            get => _newMode;
            private set => SetProperty(ref _newMode, value);
        }

        public bool IsTextDiff
        {
            get => _isTextDiff;
            private set => SetProperty(ref _isTextDiff, value);
        }

        public bool IsIgnoreWhitespaceVisible
        {
            get => _isIgnoreWhitespaceVisible;
            private set => SetProperty(ref _isIgnoreWhitespaceVisible, value);
        }

        public object Content
        {
            get => _content;
            private set => SetProperty(ref _content, value);
        }

        public int UnifiedLines
        {
            get => _unifiedLines;
            private set => SetProperty(ref _unifiedLines, value);
        }

        public DiffContext(string repo, Models.DiffOption option, DiffContext previous = null)
        {
            _repo = repo;
            _option = option;

            if (previous != null)
            {
                _isTextDiff = previous._isTextDiff;
                _isIgnoreWhitespaceVisible = previous._isIgnoreWhitespaceVisible;
                _content = previous._content;
                _oldMode = previous._oldMode;
                _newMode = previous._newMode;
                _unifiedLines = previous._unifiedLines;
                _info = previous._info;
            }

            if (string.IsNullOrEmpty(_option.OrgPath) || _option.OrgPath == "/dev/null")
                Title = _option.Path;
            else
                Title = $"{_option.OrgPath} → {_option.Path}";

            LoadContent();
        }

        public void IncrUnified()
        {
            UnifiedLines = _unifiedLines + 1;
            LoadContent();
        }

        public void DecrUnified()
        {
            UnifiedLines = Math.Max(4, _unifiedLines - 1);
            LoadContent();
        }

        public void OpenExternalMergeTool()
        {
            new Commands.DiffTool(_repo, _option).Open();
        }

        public Task<AIChatAttachment> ReadAIContextAsync(CancellationToken cancellation)
        {
            return ReadAIContextAsync(_repo, _option, cancellation);
        }

        public static async Task<AIChatAttachment> ReadAIContextAsync(string repo, Models.DiffOption option, CancellationToken cancellation)
        {
            var command = new Commands.Diff(repo, option, 999999999, false, false) { CancellationToken = cancellation };
            var result = await command.ReadAsync();
            cancellation.ThrowIfCancellationRequested();
            if (result.TextDiff is not { } textDiff)
                throw new InvalidOperationException(App.Text("Diff.AI.NoTextDiff"));
            return new AIChatAttachment
            {
                Key = option.ToString(),
                Path = option.Path,
                Source = option.IsLocalChange
                    ? (option.IsUnstaged ? App.Text("AI.Sidebar.Unstaged") : App.Text("AI.Sidebar.Staged"))
                    : string.Join(" → ", option.Revisions.ConvertAll(x => string.IsNullOrEmpty(x) || x == "-R" ? App.Text("Worktree") : x)),
                Patch = FormatDiffForAI(textDiff),
            };
        }

        private static string FormatDiffForAI(Models.TextDiff diff)
        {
            const int maxLength = 120000;
            var builder = new StringBuilder();
            foreach (var line in diff.Lines)
            {
                switch (line.Type)
                {
                    case Models.TextDiffLineType.Indicator:
                        builder.AppendLine(line.Content);
                        break;
                    case Models.TextDiffLineType.Added:
                        builder.Append('+').AppendLine(line.Content);
                        break;
                    case Models.TextDiffLineType.Deleted:
                        builder.Append('-').AppendLine(line.Content);
                        break;
                    case Models.TextDiffLineType.Normal:
                        builder.Append(' ').AppendLine(line.Content);
                        break;
                }
            }

            var text = builder.ToString();
            // shortcut: 超过 12 万字符时仅发送 diff 前缀；大型变更需要完整上下文时再改为分块传输。
            return text.Length > maxLength ? text[..maxLength] + "\n[Diff truncated]" : text;
        }

        public void CheckSettings()
        {
            var pref = Preferences.Instance;

            if (Content is TextDiffContext ctx)
            {
                if ((pref.UseFullTextDiff && _info.UnifiedLines != _entireFileLines) ||
                    (!pref.UseFullTextDiff && _info.UnifiedLines == _entireFileLines) ||
                    (pref.IgnoreWhitespaceChangesInDiff != _info.IgnoreWhitespace))
                {
                    LoadContent();
                    return;
                }

                if (ctx.IsSideBySide() != pref.UseSideBySideDiff)
                    Content = ctx.SwitchMode();
            }
            else if (Content is Models.NoOrEOLChange)
            {
                if (pref.IgnoreWhitespaceChangesInDiff != _info.IgnoreWhitespace)
                    LoadContent();
            }
        }

        private void LoadContent()
        {
            if (_option.Path.EndsWith('/'))
            {
                OldMode = 0;
                NewMode = 160000;
                IsTextDiff = false;
                IsIgnoreWhitespaceVisible = false;
                Content = null;
                _info = null;
                return;
            }

            Task.Run(async () =>
            {
                var pref = Preferences.Instance;
                var numLines = pref.UseFullTextDiff ? _entireFileLines : _unifiedLines;
                var ignoreWhitespace = pref.IgnoreWhitespaceChangesInDiff;
                var ignoreCRAtEOL = pref.IgnoreCRAtEOLInDiff;

                var latest = await new Commands.Diff(_repo, _option, numLines, ignoreWhitespace, ignoreCRAtEOL)
                    .ReadAsync()
                    .ConfigureAwait(false);

                var info = new Info(_option, numLines, ignoreWhitespace, latest);
                if (_info != null && info.IsSame(_info))
                    return;

                _info = info;

                object rs = null;
                if (latest.TextDiff is { } textDiff)
                {
                    rs = textDiff;
                }
                else if (latest.LFSDiff is { } lfs)
                {
                    var imgDecoder = ImageSource.GetDecoder(_option.Path);
                    if (imgDecoder != Models.ImageDecoder.None)
                        rs = new LFSImageDiff(_repo, lfs, imgDecoder);
                    else
                        rs = lfs;
                }
                else if (latest.IsBinary)
                {
                    var imgDecoder = ImageSource.GetDecoder(_option.Path);
                    if (imgDecoder != Models.ImageDecoder.None)
                        rs = await CreateImageDiffAsync(imgDecoder).ConfigureAwait(false);
                    else
                        rs = await CreateBinaryDiffAsync().ConfigureAwait(false);
                }
                else if (latest.IsSubmoduleChange)
                {
                    rs = await CreateSubmoduleDiffAsync(latest.OldHash, latest.NewHash).ConfigureAwait(false);
                }
                else if (IsEmptyFileHash(latest.OldHash) || IsEmptyFileHash(latest.NewHash))
                {
                    rs = new Models.EmptyFile();
                }
                else
                {
                    rs = new Models.NoOrEOLChange();
                }

                Dispatcher.UIThread.Post(() =>
                {
                    OldMode = latest.OldMode;
                    NewMode = latest.NewMode;

                    if (rs is Models.TextDiff cur)
                    {
                        IsTextDiff = true;
                        IsIgnoreWhitespaceVisible = true;

                        if (Preferences.Instance.UseSideBySideDiff)
                            Content = new TwoSideTextDiff(_option, cur, _content as TextDiffContext);
                        else
                            Content = new CombinedTextDiff(_option, cur, _content as TextDiffContext);
                    }
                    else
                    {
                        IsTextDiff = false;
                        IsIgnoreWhitespaceVisible = (rs is Models.NoOrEOLChange);
                        Content = rs;
                    }
                });
            });
        }

        private async Task<Models.ImageDiff> CreateImageDiffAsync(Models.ImageDecoder imgDecoder)
        {
            var oldPath = string.IsNullOrEmpty(_option.OrgPath) ? _option.Path : _option.OrgPath;
            var imgDiff = new Models.ImageDiff();
            var fullPath = Path.Combine(_repo, _option.Path);

            if (_option.Revisions.Count == 2) // Two revisions are specified, compare them
            {
                if (_option.Revisions[0].Equals("-R", StringComparison.Ordinal)) // `-R` means the old side is the working tree
                {
                    var oldImage = await ImageSource.FromFileAsync(fullPath, imgDecoder).ConfigureAwait(false);
                    imgDiff.Old = oldImage.Bitmap;
                    imgDiff.OldFileSize = oldImage.Size;
                }
                else
                {
                    var oldImage = await ImageSource.FromRevisionAsync(_repo, _option.Revisions[0], oldPath, imgDecoder).ConfigureAwait(false);
                    imgDiff.Old = oldImage.Bitmap;
                    imgDiff.OldFileSize = oldImage.Size;
                }

                if (string.IsNullOrEmpty(_option.Revisions[1])) // Empty string in the second revision means the new side is the working tree
                {
                    var newImage = await ImageSource.FromFileAsync(fullPath, imgDecoder).ConfigureAwait(false);
                    imgDiff.New = newImage.Bitmap;
                    imgDiff.NewFileSize = newImage.Size;
                }
                else
                {
                    var newImage = await ImageSource.FromRevisionAsync(_repo, _option.Revisions[1], _option.Path, imgDecoder).ConfigureAwait(false);
                    imgDiff.New = newImage.Bitmap;
                    imgDiff.NewFileSize = newImage.Size;
                }
            }
            else if (_option.IsUnstaged) // Unstaged change compared to staged or HEAD
            {
                if (!oldPath.Equals("/dev/null", StringComparison.Ordinal))
                {
                    var oldImage = await ImageSource.FromRevisionAsync(_repo, string.Empty, oldPath, imgDecoder).ConfigureAwait(false);
                    imgDiff.Old = oldImage.Bitmap;
                    imgDiff.OldFileSize = oldImage.Size;
                }

                var newImage = await ImageSource.FromFileAsync(fullPath, imgDecoder).ConfigureAwait(false);
                imgDiff.New = newImage.Bitmap;
                imgDiff.NewFileSize = newImage.Size;
            }
            else // Staged change compared to the last commit (HEAD)
            {
                var oldImage = await ImageSource.FromRevisionAsync(_repo, "HEAD", oldPath, imgDecoder).ConfigureAwait(false);
                imgDiff.Old = oldImage.Bitmap;
                imgDiff.OldFileSize = oldImage.Size;

                var newImage = await ImageSource.FromRevisionAsync(_repo, string.Empty, oldPath, imgDecoder).ConfigureAwait(false);
                imgDiff.New = newImage.Bitmap;
                imgDiff.NewFileSize = newImage.Size;
            }

            return imgDiff;
        }

        private async Task<Models.BinaryDiff> CreateBinaryDiffAsync()
        {
            var oldPath = string.IsNullOrEmpty(_option.OrgPath) ? _option.Path : _option.OrgPath;
            var binaryDiff = new Models.BinaryDiff();
            var fullPath = Path.Combine(_repo, _option.Path);

            binaryDiff.Repository = _repo;
            binaryDiff.FilePath = _option.Path;

            if (_option.Revisions.Count == 2) // Two revisions are specified, compare them
            {
                if (_option.Revisions[0].Equals("-R", StringComparison.Ordinal)) // `-R` means the old side is the working tree
                    binaryDiff.OldSize = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                else
                    binaryDiff.OldSize = await new Commands.QueryFileSize(_repo, oldPath, _option.Revisions[0]).GetResultAsync().ConfigureAwait(false);

                if (string.IsNullOrEmpty(_option.Revisions[1])) // Empty string in the second revision means the new side is the working tree
                {
                    binaryDiff.NewSize = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                    binaryDiff.NewRevision = null;
                }
                else
                {
                    binaryDiff.NewSize = await new Commands.QueryFileSize(_repo, _option.Path, _option.Revisions[1]).GetResultAsync().ConfigureAwait(false);
                    binaryDiff.NewRevision = _option.Revisions[1];
                }
            }
            else if (_option.IsUnstaged) // Unstaged change compared to staged or HEAD
            {
                if (!oldPath.Equals("/dev/null", StringComparison.Ordinal))
                    binaryDiff.OldSize = await new Commands.QueryFileSize(_repo, oldPath, string.Empty).GetResultAsync().ConfigureAwait(false);

                binaryDiff.NewSize = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                binaryDiff.NewRevision = null;
            }
            else // Staged change compared to the last commit (HEAD)
            {
                binaryDiff.OldSize = await new Commands.QueryFileSize(_repo, oldPath, "HEAD").GetResultAsync().ConfigureAwait(false);
                binaryDiff.NewSize = await new Commands.QueryFileSize(_repo, _option.Path, string.Empty).GetResultAsync().ConfigureAwait(false);
                binaryDiff.NewRevision = string.Empty;
            }

            return binaryDiff;
        }

        private async Task<Models.SubmoduleDiff> CreateSubmoduleDiffAsync(string oldRevision, string newRevision)
        {
            var submoduleDiff = new Models.SubmoduleDiff();
            var submoduleRoot = $"{_repo}/{_option.Path}".Replace('\\', '/').TrimEnd('/');
            submoduleDiff.FullPath = submoduleRoot;

            if (IsValidSubmoduleHash(oldRevision))
                submoduleDiff.Old = await new Commands.QuerySubmoduleRevision(submoduleRoot, oldRevision)
                    .GetResultAsync()
                    .ConfigureAwait(false);

            if (IsValidSubmoduleHash(newRevision))
                submoduleDiff.New = await new Commands.QuerySubmoduleRevision(submoduleRoot, newRevision)
                    .GetResultAsync()
                    .ConfigureAwait(false);

            return submoduleDiff;
        }

        private bool IsValidSubmoduleHash(string hash)
        {
            for (int i = 0; i < hash.Length; i++)
            {
                if (hash[i] != '0')
                    return true;
            }

            return false;
        }

        private bool IsEmptyFileHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
                return false;

            if (hash.Length == 40)
                return hash.Equals(Models.EmptyFile.SHA1, StringComparison.Ordinal);

            if (hash.Length == 64)
                return hash.Equals(Models.EmptyFile.SHA256, StringComparison.Ordinal);

            return false;
        }

        private class Info
        {
            public string Argument { get; }
            public int UnifiedLines { get; }
            public bool IgnoreWhitespace { get; }
            public string OldHash { get; }
            public string NewHash { get; }

            public Info(Models.DiffOption option, int unifiedLines, bool ignoreWhitespace, Models.DiffResult result)
            {
                Argument = option.ToString();
                UnifiedLines = unifiedLines;
                IgnoreWhitespace = ignoreWhitespace;
                OldHash = result.OldHash;
                NewHash = result.NewHash;
            }

            public bool IsSame(Info other)
            {
                return Argument.Equals(other.Argument, StringComparison.Ordinal) &&
                    UnifiedLines == other.UnifiedLines &&
                    IgnoreWhitespace == other.IgnoreWhitespace &&
                    OldHash.Equals(other.OldHash, StringComparison.Ordinal) &&
                    NewHash.Equals(other.NewHash, StringComparison.Ordinal);
            }
        }

        private readonly int _entireFileLines = 999999999;
        private readonly string _repo;
        private readonly Models.DiffOption _option = null;
        private int _oldMode = 0;
        private int _newMode = 0;
        private int _unifiedLines = 4;
        private bool _isTextDiff = false;
        private bool _isIgnoreWhitespaceVisible = true;
        private object _content = null;
        private Info _info = null;
    }
}
