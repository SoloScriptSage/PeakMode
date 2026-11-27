using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using PeakModeTray.Checkup;

namespace PeakModeTray.ViewModels
{
    // One row of the Checkup screen
    internal sealed class CheckRowViewModel : ObservableObject
    {
        internal CheckRowViewModel(CheckResult r, Action<string> open, bool? isOpen = null)
        {
            Result = r;
            _open = isOpen ?? r.Status == CheckStatus.WorthChecking;
            OpenLink = new RelayCommand(() => open(r.LinkUrl), () => r.LinkUrl.Length > 0);
        }

        internal CheckResult Result { get; }
        public string Id     => Result.Id;
        public string Name   => Result.Name;
        public string Detail => Result.Detail;
        public string Icon   => Result.Icon;
        public string Value  => Result.Value;
        public CheckStatus Status => Result.Status;
        public string Tag => Status switch { CheckStatus.Ok => "OK", CheckStatus.WorthChecking => "WORTH CHECKING", _ => "INFO" };
        public bool IsOk   => Status == CheckStatus.Ok;
        public bool IsWarn => Status == CheckStatus.WorthChecking;

        public bool   HasFix    => Result.Fix.Length > 0;
        public string Fix       => Result.Fix;
        public bool   HasNote   => Result.Note.Length > 0;
        public string NoteLabel => Result.NoteLabel;
        public string Note      => Result.Note;
        public IReadOnlyList<DetailLine> Lines => Result.AllLines;
        public bool   HasLink   => Result.LinkUrl.Length > 0;
        public string LinkText  => Result.LinkText;
        public string LinkName  => $"Open the Microsoft page about {Name.ToLowerInvariant()}";
        public RelayCommand OpenLink { get; }

        // Read by screen readers on the expand button
        public string SpokenStatus => $"{Tag.ToLowerInvariant()}: {Value}. {Detail}" + (HasFix ? ". How to fix: " + Fix : "");

        private bool _open;
        public bool IsOpen { get => _open; set { if (Set(ref _open, value)) OnPropertyChanged(nameof(ExpandName)); } }
        public string ExpandName => (IsOpen ? "Hide details for " : "Show details for ") + Name;
        public RelayCommand ToggleOpen => new(() => IsOpen = !IsOpen);
    }

    // PC Checkup screen: runs the read-only checks off the UI thread when the
    // page is shown and on RE-CHECK. Nothing here changes the system.
    internal sealed class CheckupViewModel : ObservableObject
    {
        private readonly ICoreService _core;
        private readonly Action<string> _open;
        private readonly ISystemReader _reader;
        private readonly Func<DateTime> _now;

        public CheckupViewModel(ICoreService core, Action<string> open, ISystemReader? reader = null, Func<DateTime>? now = null)
        {
            _core = core;
            _open = open;
            _reader = reader ?? new SystemReader();
            _now = now ?? (() => DateTime.Now);
            RecheckCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy);
        }

        public ObservableCollection<CheckRowViewModel> Rows { get; } = new();
        public RelayCommand RecheckCommand { get; }

        private bool _busy;
        public bool IsBusy
        {
            get => _busy;
            private set { if (Set(ref _busy, value)) { RecheckCommand.Refresh(); OnPropertyChanged(nameof(CheckedText)); } }
        }

        private DateTime? _checkedAt;
        public string CheckedText => IsBusy ? "Checking..."
            : _checkedAt is { } t ? $"Checked {t:HH:mm}. Read only: nothing here changes your PC."
            : "Not checked yet.";

        public string OkCount   => Rows.Count(r => r.Status == CheckStatus.Ok).ToString();
        public string WarnCount => Rows.Count(r => r.Status == CheckStatus.WorthChecking).ToString();
        public string InfoCount => Rows.Count(r => r.Status == CheckStatus.Info).ToString();
        public bool   WarnHot   => Rows.Any(r => r.IsWarn);

        public async Task RefreshAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                string sessions;
                try { sessions = _core.ReadSessions(); } catch (Exception) { sessions = "[]"; }
                var ctx = new CheckContext(_now(), _core.State == PeakModeCore.StateActive, Checks.LastGpu(sessions));
                var results = await Task.Run(() => Checks.RunAll(_reader, ctx));

                // A row keeps its open state while its status stays the same
                var was = Rows.ToDictionary(r => r.Id, r => (r.Status, r.IsOpen));
                Rows.Clear();
                foreach (var r in results)
                    Rows.Add(new CheckRowViewModel(r, _open,
                        was.TryGetValue(r.Id, out var w) && w.Status == r.Status ? w.IsOpen : null));
                _checkedAt = _now();
            }
            finally
            {
                IsBusy = false;
                foreach (var n in new[] { nameof(OkCount), nameof(WarnCount), nameof(InfoCount), nameof(WarnHot), nameof(CheckedText) })
                    OnPropertyChanged(n);
            }
        }
    }
}
