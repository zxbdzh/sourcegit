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

        private async void OnAskAIShortcut(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button { Tag: string prompt } && DataContext is ViewModels.AIChatContext vm)
                await vm.SendAIMessageAsync(prompt);
        }

        private async void OnAddCurrentDiff(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is ViewModels.AIChatContext vm)
                await vm.AddCurrentDiffAsync();
        }

        private void OnRemoveAttachment(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button { DataContext: ViewModels.AIChatAttachment attachment } && DataContext is ViewModels.AIChatContext vm)
                vm.RemoveAttachment(attachment);
        }
    }
}
