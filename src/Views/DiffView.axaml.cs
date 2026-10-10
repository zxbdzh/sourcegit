using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class DiffView : UserControl
    {
        public DiffView()
        {
            InitializeComponent();
            LayoutUpdated += (_, _) => SynchronizeAIContext();

        }

        public void ToggleHotkeyBindings(bool enabled)
        {
            var isMacOS = OperatingSystem.IsMacOS();
            if (enabled)
            {
                BtnGotoFirstChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+Home" : "Ctrl+Alt+Home");
                BtnGotoPrevChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+Up" : "Ctrl+Alt+Up");
                BtnGotoNextChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+Down" : "Ctrl+Alt+Down");
                BtnGotoLastChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+End" : "Ctrl+Alt+End");
                BtnOpenExternalMergeTool.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Shift+D" : "Ctrl+Shift+D");
            }
            else
            {
                BtnGotoFirstChange.HotKey = null;
                BtnGotoPrevChange.HotKey = null;
                BtnGotoNextChange.HotKey = null;
                BtnGotoLastChange.HotKey = null;
                BtnOpenExternalMergeTool.HotKey = null;
            }
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            if (DataContext is ViewModels.DiffContext vm)
                vm.CheckSettings();

            _aiChat = (this.FindAncestorOfType<Repository>()?.DataContext as ViewModels.Repository)?.AIChat;
            BtnAddDiffToAI.IsEnabled = _aiChat != null;
            SynchronizeAIContext();
            ToggleHotkeyBindings(IsEffectivelyVisible);
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);
            ToggleHotkeyBindings(false);
            if (_aiChat != null && _aiChat.CurrentDiff == _lastAIDiff)
                _aiChat.CurrentDiff = null;
            _lastAIDiff = null;
            _aiChat = null;
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            // 固定 AI 入口并给文件标题留位置；窄窗口内其余差异工具可横向滚动。
            ToolbarActions.MaxWidth = Math.Max(80, e.NewSize.Width - 200);
        }

        private void SynchronizeAIContext()
        {
            if (_aiChat == null)
                return;
            var current = IsEffectivelyVisible && Bounds.Height > 0 && DataContext is ViewModels.DiffContext { IsTextDiff: true } diff ? diff : null;
            if (current == _lastAIDiff)
                return;
            // 隐藏页面不能覆盖另一页的当前差异；布局通知仅在对象或可见性变化时更新状态。
            if (current != null || _aiChat.CurrentDiff == _lastAIDiff)
                _aiChat.CurrentDiff = current;
            _lastAIDiff = current;
        }

        private ViewModels.AIChatContext _aiChat;
        private ViewModels.DiffContext _lastAIDiff;

        private void OnGotoFirstChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.First);
            e.Handled = true;
        }

        private void OnGotoPrevChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Prev);
            e.Handled = true;
        }

        private void OnGotoNextChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Next);
            e.Handled = true;
        }

        private void OnGotoLastChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Last);
            e.Handled = true;
        }

        private async void OnAddDiffToAI(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is ViewModels.DiffContext diff && this.FindAncestorOfType<Repository>()?.DataContext is ViewModels.Repository repo)
            {
                repo.AIChat.CurrentDiff = diff;
                await repo.AIChat.AddCurrentDiffAsync();
            }
        }

        private void OnToggleButtonPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ToggleButton.IsCheckedProperty && DataContext is ViewModels.DiffContext vm)
                vm.CheckSettings();
        }

        private void OnOpenSubmoduleRevisionCompare(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.SubmoduleDiff diff } && diff.CanOpenDetails)
            {
                var vm = new ViewModels.SubmoduleRevisionCompare(diff);
                this.ShowWindow(vm);
            }
        }

        private async void OnOpenBinaryFileViewer(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.BinaryDiff diff } && diff.NewSize > 0)
            {
                await this.ShowDialogAsync(new ViewModels.BinaryFileViewer(diff.Repository, diff.FilePath, diff.NewRevision));
                e.Handled = true;
            }
        }
    }
}
