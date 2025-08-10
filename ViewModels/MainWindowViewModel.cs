using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DvdRipper.Commands;
using DvdRipper.Models;
using DvdRipper.Services;

namespace DvdRipper.ViewModels
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private readonly DvdService _dvdService = new();
        private readonly SynchronizationContext _syncContext;

        private string _device = "/dev/sr0";
        private ObservableCollection<TitleInfo> _titles = new();
        private TitleInfo? _selectedTitle;
        private string _outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "movie.mkv");
        private double _progress;
        private string _log = string.Empty;
        private readonly string _logFilePath = Path.Combine("./dvd_ripper_debug{0}.log", DateTime.Now.ToString());
        private bool _isBusy;

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainWindowViewModel()
        {
            _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
            ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
            RipCommand = new AsyncRelayCommand(RipAsync, () => !IsBusy && SelectedTitle != null);
        }

        public string Device
        {
            get => _device;
            set => SetProperty(ref _device, value);
        }

        public ObservableCollection<TitleInfo> Titles
        {
            get => _titles;
            private set => SetProperty(ref _titles, value);
        }

        public TitleInfo? SelectedTitle
        {
            get => _selectedTitle;
            set
            {
                if (SetProperty(ref _selectedTitle, value))
                {
                    // Update RipCommand's ability to execute
                    (RipCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public string OutputPath
        {
            get => _outputPath;
            set => SetProperty(ref _outputPath, value);
        }

        public double Progress
        {
            get => _progress;
            private set => SetProperty(ref _progress, value);
        }

        public string Log
        {
            get => _log;
            private set => SetProperty(ref _log, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    // Raise can execute changed on commands
                    (ScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (RipCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public ICommand ScanCommand { get; }
        public ICommand RipCommand { get; }

        private CancellationTokenSource? _cts;

        public void CancelCurrentRip()
        {
            _cts?.Cancel();
            _cts = null;
        }

        private async Task ScanAsync()
        {
            try
            {
                IsBusy = true;
                AppendLog($"Scanning titles on {Device}…\n");
                List<TitleInfo> result = await DvdService.ScanTitlesAsync(Device, new Progress<string>(AppendLog));
                _syncContext.Post(_ =>
                {
                    Titles = new ObservableCollection<TitleInfo>(result);
                    SelectedTitle = Titles.FirstOrDefault();
                }, null);
            }
            catch (Exception ex)
            {
                AppendLog($"Error scanning titles: {ex.Message}\n");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RipAsync()
        {
            if (SelectedTitle == null) return;
            IsBusy = true;
            Progress = 0;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            AppendLog($"Starting rip of title {SelectedTitle.Number} to {OutputPath}\n");

            try
            {
                Progress<double> progress = new(v => _syncContext.Post(_ => Progress = v, null));
                Progress<string> logger = new(AppendLog);
                await _dvdService.RipAsync(Device, SelectedTitle.Number, OutputPath, progress, logger, token);
                AppendLog("Rip completed.\n");
                Progress = 100;
            }
            catch (Exception ex)
            {
                AppendLog($"Error ripping: {ex.Message}\n");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void AppendLog(string message)
        {
            // Limit log size to avoid unbounded growth
            const int maxLength = 20000;
            _log += message;
            if (_log.Length > maxLength)
            {
                _log = _log.Substring(_log.Length - maxLength);
            }
            OnPropertyChanged(nameof(Log));

            // Append to the file
            try
            {
                // Ensure the directory exists and append text
                Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
                File.AppendAllText(_logFilePath, message);
            }
            catch (Exception ex)
            {
                // In case the log file can’t be written, still report to UI
                _log += $"[File log error: {ex.Message}]\n";
                OnPropertyChanged(nameof(Log));
            }
        }

        protected bool SetProperty<T>(ref T backingField, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(backingField, value)) return false;
            backingField = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}