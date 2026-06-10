using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DBAnalyzer.Models
{
    public enum ContextStatus { Loading, Success, Empty, Failed }

    public class ContextField
    {
        public string Key   { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class ContextRow
    {
        public List<ContextField> Fields { get; set; } = new();
    }

    public class ContextItem : INotifyPropertyChanged
    {
        private ContextStatus _status = ContextStatus.Loading;
        private bool          _isExpanded = false;
        private string        _header = string.Empty;
        private List<ContextRow> _rows = new();
        private string?       _errorMessage;

        public string Header
        {
            get => _header;
            set { _header = value; OnPropertyChanged(); }
        }

        public ContextStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CardBorderColor));
                OnPropertyChanged(nameof(CardHeaderBackground));
                OnPropertyChanged(nameof(IsLoading));
            }
        }

        public List<ContextRow> Rows
        {
            get => _rows;
            set
            {
                _rows = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpandIcon)); }
        }

        public string RawText { get; set; } = string.Empty;

        // ── Computed display props ───────────────────────────────────────────
        public string StatusIcon => Status switch
        {
            ContextStatus.Success => "✅",
            ContextStatus.Empty   => "⚪",
            ContextStatus.Failed  => "❌",
            ContextStatus.Loading => "⏳",
            _                     => "?"
        };

        public string StatusText => Status switch
        {
            ContextStatus.Success => $"{Rows.Count} row(s)",
            ContextStatus.Empty   => "0 rows",
            ContextStatus.Failed  => ErrorMessage ?? "Error",
            ContextStatus.Loading => "กำลังดึงข้อมูล...",
            _                     => string.Empty
        };

        public string CardBorderColor => Status switch
        {
            ContextStatus.Success => "#2A5A2A",
            ContextStatus.Empty   => "#3A3A50",
            ContextStatus.Failed  => "#5A1A1A",
            ContextStatus.Loading => "#2A2A50",
            _                     => "#3A3A50"
        };

        public string CardHeaderBackground => Status switch
        {
            ContextStatus.Success => "#1A2E1A",
            ContextStatus.Empty   => "#1E1E2E",
            ContextStatus.Failed  => "#2E1A1A",
            ContextStatus.Loading => "#1A1A2E",
            _                     => "#1E1E2E"
        };

        public bool IsLoading => Status == ContextStatus.Loading;

        public string ExpandIcon => IsExpanded ? "▲" : "▼";

        // ── INotifyPropertyChanged ───────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
