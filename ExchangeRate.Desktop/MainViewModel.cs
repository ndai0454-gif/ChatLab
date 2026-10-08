using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ExchangeRate.Data.Models;
using ExchangeRate.Desktop.Services;

namespace ExchangeRate.Desktop;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IRateQueryService _rateQueryService;
    private string _workerStatusText = "Đang kiểm tra...";
    private string _lastRefreshedText = "Chưa cập nhật";
    private bool _isRefreshing;

    public MainViewModel(IRateQueryService rateQueryService)
    {
        _rateQueryService = rateQueryService;
        RefreshCommand = new RelayCommand(RefreshAsync, () => !_isRefreshing);
    }

    public ObservableCollection<ExchangeRateRecord> LatestRates { get; } = new();

    public string WorkerStatusText
    {
        get => _workerStatusText;
        private set { _workerStatusText = value; OnPropertyChanged(); }
    }

    public string LastRefreshedText
    {
        get => _lastRefreshedText;
        private set { _lastRefreshedText = value; OnPropertyChanged(); }
    }

    public ICommand RefreshCommand { get; }

    public async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var snapshot = await _rateQueryService.GetSnapshotAsync();

            LatestRates.Clear();
            foreach (var rate in snapshot.LatestPerCurrency)
            {
                LatestRates.Add(rate);
            }

            WorkerStatusText = snapshot.WorkerStatus switch
            {
                WorkerStatus.Running => "🟢 Worker đang chạy",
                WorkerStatus.Stopped => "🔴 Worker không hoạt động (dữ liệu cũ)",
                _ => "⚪ Chưa có dữ liệu",
            };

            LastRefreshedText = $"Cập nhật lúc {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
