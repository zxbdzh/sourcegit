using System.Linq;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class AIChatSidebar : UserControl
    {
        public AIChatSidebar()
        {
            InitializeComponent();
            // 多行输入框在冒泡前消费 Enter，因此发送必须在隧道阶段拦截。
            AIInputBox.AddHandler(KeyDownEvent, OnAIInputKeyDown, RoutingStrategies.Tunnel);
            AIConversationScrollViewer.LayoutUpdated += (_, _) =>
            {
                if (DataContext is ViewModels.AIChatContext { IsAIResponding: true })
                    AIConversationScrollViewer.ScrollToEnd();
            };
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            (DataContext as ViewModels.AIChatContext)?.RefreshServices();
        }

        private async void OnAIInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                return;
            e.Handled = true;
            if (DataContext is ViewModels.AIChatContext { CanSendAIMessage: true } vm)
                await vm.SendAIMessageAsync();
        }

        private async void OnSendAIMessage(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is ViewModels.AIChatContext vm)
                await vm.SendAIMessageAsync();
        }


        private async void OnAddCurrentDiff(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is ViewModels.AIChatContext vm)
                await vm.AddCurrentDiffAsync();
        }

        private void OnLocateReviewFinding(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Button { DataContext: ViewModels.AIChatReviewFinding finding } || DataContext is not ViewModels.AIChatContext vm)
                return;
            var line = vm.ResolveReviewLocation(finding);
            if (line < 0)
                return;
            var presenter = this.FindAncestorOfType<Repository>()?.GetVisualDescendants().OfType<ThemedTextDiffPresenter>()
                .FirstOrDefault(x => x.IsEffectivelyVisible && !x.IsOld);
            if (presenter == null)
            {
                finding.LocateMessage = App.Text("AI.Review.Card.NotCurrentDiff");
                return;
            }
            presenter.TextArea.Caret.Line = line;
            presenter.ScrollToLine(line);
        }

        private async void OnFixReviewFinding(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button { DataContext: ViewModels.AIChatReviewFinding finding } && DataContext is ViewModels.AIChatContext vm)
                await vm.RequestReviewFixAsync(finding);
        }

        private void OnRemoveContextItem(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button { DataContext: ViewModels.AIChatContextItem item } && DataContext is ViewModels.AIChatContext vm)
                vm.RemoveContextItem(item);
        }
        private void OnRemoveFollowedDiff(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button { DataContext: ViewModels.AIChatDiff diff } && DataContext is ViewModels.AIChatContext vm)
                vm.RemoveFollowedDiff(diff);
        }

        private void OnRemoveAttachment(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button { DataContext: ViewModels.AIChatAttachment attachment } && DataContext is ViewModels.AIChatContext vm)
                vm.RemoveAttachment(attachment);
        }

        private async void OnApplyPatch(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button { DataContext: ViewModels.AIChatPatchFile patch } && DataContext is ViewModels.AIChatContext vm)
                await vm.ApplyPatchAsync(patch);
        }

        private void OnRejectPatch(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button { DataContext: ViewModels.AIChatPatchFile patch })
                patch.MarkRejected();
        }

        private async void OnUndoPatch(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button { DataContext: ViewModels.AIChatPatchFile patch } && DataContext is ViewModels.AIChatContext vm)
                await vm.UndoPatchAsync(patch);
        }

        private async void OnRetryAIMessage(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button { DataContext: ViewModels.AIChatMessage reply } && DataContext is ViewModels.AIChatContext vm)
                await vm.RetryAIMessageAsync(reply);
        }
    }
}
