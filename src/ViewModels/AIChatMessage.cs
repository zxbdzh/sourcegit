using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using SourceGit.AI;

namespace SourceGit.ViewModels
{
    public class AIChatMessage : ObservableObject
    {
        public bool IsUser { get; init; }
        public ReviewMode ReviewMode { get; init; }
        public string ModeLabel => App.Text($"AI.Review.Mode.{ReviewMode}");
        public string ContextSummary => _context?.Split('\n')[0].TrimEnd('\r') ?? string.Empty;
        public string CompatibilityNotice { get => _compatibilityNotice; set => SetProperty(ref _compatibilityNotice, value); }
        public string ErrorDetails { get => _errorDetails; set => SetProperty(ref _errorDetails, value); }
        private string _errorDetails;
        private string _compatibilityNotice;
        public bool ShowReviewMode => ReviewMode != ReviewMode.Chat;

        // 阶段只描述已发生的请求事件，不猜测模型内部推理进度。
        public string RequestStage { get => _requestStage; set => SetProperty(ref _requestStage, value); }
        public string EvidenceContext
        {
            get => _evidenceContext;
            set
            {
                if (SetProperty(ref _evidenceContext, value))
                    OnPropertyChanged(nameof(HasEvidence));
            }
        }
        public bool HasEvidence => !string.IsNullOrEmpty(EvidenceContext) || HasToolCalls;
        // 只列实际调用目标和执行状态；模型结论不作为已验证依据。
        public string EvidenceSummary => string.Join(Environment.NewLine, ToolCalls.Select(x => $"{x.Name} · {x.Status} · {x.Target}"));
        private string _requestStage = string.Empty;
        private string _evidenceContext = string.Empty;

        public string Context
        {
            get => _context;
            set
            {
                if (SetProperty(ref _context, value))
                    OnPropertyChanged(nameof(ContextSummary));
            }
        }

        public bool IsError
        {
            get => _isError;
            set
            {
                if (SetProperty(ref _isError, value))
                    NotifyVisibility();
            }
        }

        public bool IsPending
        {
            get => _isPending;
            set
            {
                if (SetProperty(ref _isPending, value))
                    NotifyVisibility();
            }
        }

        public bool CanRetry
        {
            get => _canRetry;
            set
            {
                if (SetProperty(ref _canRetry, value))
                    OnPropertyChanged(nameof(IsRetryable));
            }
        }

        public bool IsRetryable => IsError && CanRetry;

        public bool IsWaiting => IsPending && string.IsNullOrEmpty(_text);

        // 有补丁卡片时只显示围栏外的说明，不能整段隐藏有效回答。
        public string DisplayMarkdown => HasPatches ? AIChatPatch.RemovePatchFences(_text) : HasReviewCards ? _reviewRemainder : _text;
        public bool ShowMarkdown => !IsUser && !IsError && !string.IsNullOrWhiteSpace(DisplayMarkdown);
        public bool HasRawResponse => (HasPatches || HasReviewCards) && !string.IsNullOrEmpty(_text);

        // 审查卡片只是已完成回复的结构化视图；解析失败时集合为空，界面继续显示原始 Markdown。
        public ObservableCollection<AIChatReviewFinding> ReviewFindings { get; } = [];
        public bool HasReviewCards => ReviewFindings.Count > 0;
        private string _reviewRemainder = string.Empty;

        public void ClearReview()
        {
            ReviewFindings.Clear();
            _reviewRemainder = string.Empty;
            NotifyReview();
        }

        public void PrepareReview()
        {
            ClearReview();
            if (ReviewMode is not (ReviewMode.Bugs or ReviewMode.Security or ReviewMode.Exceptions or ReviewMode.Tests) || HasPatches)
                return;
            var result = AIChatReview.Parse(Text);
            if (result == null)
                return;
            foreach (var finding in result.Findings)
                ReviewFindings.Add(finding);
            _reviewRemainder = result.Remainder;
            NotifyReview();
        }

        private void NotifyReview()
        {
            OnPropertyChanged(nameof(HasReviewCards));
            OnPropertyChanged(nameof(ShowMarkdown));
            OnPropertyChanged(nameof(DisplayMarkdown));
            OnPropertyChanged(nameof(HasRawResponse));
        }

        public string Text
        {
            get => _text;
            set
            {
                if (SetProperty(ref _text, value))
                    {
                        NotifyVisibility();
                        OnPropertyChanged(nameof(DisplayMarkdown));
                        OnPropertyChanged(nameof(HasRawResponse));
                    }
            }
        }

        public ObservableCollection<AIChatPatchFile> Patches { get; } = [];
        public bool HasPatches => Patches.Count > 0;
        public string PatchError { get => _patchError; private set => SetProperty(ref _patchError, value); }

        public void PreparePatches(string repo, System.Collections.Generic.IReadOnlyDictionary<string, AIChatBaseline> baselines)
        {
            Patches.Clear();
            PatchError = string.Empty;
            try
            {
                foreach (var patch in AIChatPatch.Parse(Text, repo, baselines))
                    Patches.Add(patch);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.IOException or OverflowException)
            {
                Patches.Clear();
                PatchError = error.Message;
            }
            OnPropertyChanged(nameof(HasPatches));
            OnPropertyChanged(nameof(ShowMarkdown));
            OnPropertyChanged(nameof(DisplayMarkdown));
            OnPropertyChanged(nameof(HasRawResponse));
        }
        public ObservableCollection<AIChatToolCall> ToolCalls { get; } = [];
        public bool HasToolCalls => ToolCalls.Count > 0;

        public string ToolSummary
        {
            get
            {
                if (ToolCalls.Count == 0)
                    return string.Empty;

                var total = TimeSpan.FromMilliseconds(ToolCalls.Sum(x => x.Duration.TotalMilliseconds));
                var elapsed = total.TotalMilliseconds < 1000 ? $"{total.TotalMilliseconds:0} ms" : $"{total.TotalSeconds:0.0} s";
                var running = ToolCalls.Count(x => x.State == ToolCallState.Running);
                var issues = ToolCalls.Count(x => x.State is ToolCallState.Failed or ToolCallState.TimedOut or ToolCallState.Canceled);
                if (running > 0)
                    return App.Text("AI.Sidebar.ToolSummaryRunning", ToolCalls.Count, elapsed);
                if (issues > 0)
                    return App.Text("AI.Sidebar.ToolSummaryIssue", ToolCalls.Count, issues, elapsed);
                return App.Text("AI.Sidebar.ToolSummaryCompleted", ToolCalls.Count, elapsed);
            }
        }
        public void ResetToolProgress()
        {
            ToolCalls.Clear();
            OnPropertyChanged(nameof(HasToolCalls));
            OnPropertyChanged(nameof(ToolSummary));
            OnPropertyChanged(nameof(EvidenceSummary));
            OnPropertyChanged(nameof(HasEvidence));
        }
        public void ApplyToolProgress(ToolCallProgress progress)
        {
            var item = ToolCalls.FirstOrDefault(x => x.Id == progress.Id);
            if (item == null)
            {
                item = new AIChatToolCall { Id = progress.Id };
                ToolCalls.Add(item);
            }

            item.Update(progress);
            OnPropertyChanged(nameof(HasToolCalls));
            OnPropertyChanged(nameof(EvidenceSummary));
            OnPropertyChanged(nameof(HasEvidence));
            OnPropertyChanged(nameof(ToolSummary));
        }

        private void NotifyVisibility()
        {
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(ShowMarkdown));
            OnPropertyChanged(nameof(IsRetryable));
        }

        private string _context = string.Empty;
        private bool _isError;
        private bool _isPending;
        private string _text = string.Empty;
        private bool _canRetry;
        private string _patchError = string.Empty;
    }

    public sealed class AIChatToolCall : ObservableObject
    {
        public string Id { get; init; }
        public string Name { get; private set; } = string.Empty;
        public string Arguments { get; private set; } = string.Empty;
        // 依据列表显示可读目标（键: 值），而不是原始 JSON；参数被截断导致无法解析时回退原文。
        public string Target
        {
            get
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(Arguments);
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                        return Arguments;
                    var parts = doc.RootElement.EnumerateObject().Select(x => $"{x.Name}: {x.Value}");
                    var text = string.Join(", ", parts);
                    return text.Length == 0 ? Arguments : text;
                }
                catch (System.Text.Json.JsonException)
                {
                    return Arguments;
                }
            }
        }
        public string Result { get; private set; } = string.Empty;
        public string Status { get; private set; } = string.Empty;
        public string Elapsed { get; private set; } = string.Empty;
        public string SummaryScope { get; private set; } = string.Empty;
        public string SummaryText { get; private set; } = string.Empty;
        public bool HasSummary => SummaryText.Length > 0;
        public bool IsRunning => _state == ToolCallState.Running;
        public ToolCallState State => _state;
        public TimeSpan Duration => _duration;

        public void Update(ToolCallProgress progress)
        {
            Name = progress.Name;
            Arguments = progress.Arguments;
            Result = progress.Result;
            // 摘要仅用于显示，原始结果仍完整保留在 Result 中。
            SummaryScope = progress.SummaryScope;
            SummaryText = progress.SummaryText;
            _state = progress.State;
            _duration = progress.Elapsed;
            Status = progress.State switch
            {
                ToolCallState.Running => App.Text("AI.Sidebar.ToolRunning"),
                ToolCallState.Completed => App.Text("AI.Sidebar.ToolCompleted"),
                ToolCallState.Failed => App.Text("AI.Sidebar.ToolFailed"),
                ToolCallState.TimedOut => App.Text("AI.Sidebar.ToolTimedOut"),
                ToolCallState.Canceled => App.Text("AI.Sidebar.ToolCanceled"),
                _ => string.Empty,
            };
            Elapsed = progress.Elapsed.TotalMilliseconds < 1000
                ? $"{progress.Elapsed.TotalMilliseconds:0} ms"
                : $"{progress.Elapsed.TotalSeconds:0.0} s";
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(SummaryScope));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(HasSummary));
            OnPropertyChanged(nameof(Arguments));
            OnPropertyChanged(nameof(Target));
            OnPropertyChanged(nameof(Result));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(Elapsed));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(Duration));
        }

        private ToolCallState _state;
        private TimeSpan _duration;
    }
}
