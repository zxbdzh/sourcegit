using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class AIChatMessage : ObservableObject
    {
        public bool IsUser { get; init; }

        public string Context
        {
            get => _context;
            set => SetProperty(ref _context, value);
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

        public bool IsWaiting => IsPending && string.IsNullOrEmpty(_text);

        public bool ShowMarkdown => !IsUser && !IsError && !string.IsNullOrEmpty(_text);

        public string Text
        {
            get => _text;
            set
            {
                if (SetProperty(ref _text, value))
                    NotifyVisibility();
            }
        }

        private void NotifyVisibility()
        {
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(ShowMarkdown));
        }

        private string _context = string.Empty;
        private bool _isError;
        private bool _isPending;
        private string _text = string.Empty;
    }
}
