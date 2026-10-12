using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public sealed class AIChatDiff
    {
        public DiffContext VisualDiff { get; }
        public string Key => VisualDiff?.AIContextKey ?? _option.ToString();
        public string Path => VisualDiff?.AIPath ?? _option.Path;
        public string Source => VisualDiff?.AISource ?? (_option.IsLocalChange
            ? (_option.IsUnstaged ? App.Text("AI.Sidebar.Unstaged") : App.Text("AI.Sidebar.Staged"))
            : string.Join(" → ", _option.Revisions.ConvertAll(x => string.IsNullOrEmpty(x) || x == "-R" ? App.Text("Worktree") : x)));
        public string DisplaySource => FormatSource(Source);

        // 只缩短完整 SHA；保留 ^、~ 等修订语义，模型上下文和附件 key 仍使用原始版本。
        public static string FormatSource(string source) => System.Text.RegularExpressions.Regex.Replace(
            source ?? string.Empty, @"\b[0-9a-fA-F]{40}\b", match => match.Value[..7]);

        private readonly string _repo;
        private readonly Models.DiffOption _option;

        private AIChatDiff(string repo, Models.DiffOption option, DiffContext visualDiff)
        {
            _repo = repo;
            _option = option;
            VisualDiff = visualDiff;
        }

        public static AIChatDiff FromDiffContext(DiffContext diff)
        {
            return diff == null ? null : new AIChatDiff(null, null, diff);
        }

        public static AIChatDiff FromOption(string repo, Models.DiffOption option)
        {
            return option == null ? null : new AIChatDiff(repo, option, null);
        }

        public Task<AIChatAttachment> ReadAIContextAsync(CancellationToken cancellation)
        {
            return VisualDiff != null
                ? VisualDiff.ReadAIContextAsync(cancellation)
                : DiffContext.ReadAIContextAsync(_repo, _option, cancellation);
        }
    }
}
