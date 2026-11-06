using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanDevLink.Services;

namespace LanDevLink.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly ShareDurationOption UnlimitedDuration = new("不限时", null);
    private readonly LocalServiceScanner _scanner = new();
    private readonly ShareServer _server = new();
    private readonly DispatcherTimer _liveStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _targetUrl = "http://localhost:5173";
    private string? _selectedService;
    private bool _isBusy;
    private bool _isActive;
    private bool _isStopping;
    private string _errorMessage = string.Empty;
    private string _statusMessage = "正在查找本机开发服务";
    private string _shareUrl = string.Empty;
    private string _visitorCountText = "0";
    private string _diagnosticUrl = string.Empty;
    private string _diagnosticResultTitle = string.Empty;
    private string _diagnosticResultDetails = string.Empty;
    private string _networkStatusMessage = "正在读取当前网络";
    private bool _isDiagnosing;
    private BitmapImage? _qrCode;
    private string _sharePassword = string.Empty;
    private ShareDurationOption _selectedShareDuration = UnlimitedDuration;
    private string _shareStatusText = "持续运行";
    private string _shareAccessText = "点击复制地址，或让同事扫描二维码。";
    private DateTimeOffset? _shareExpiresAt;
    private bool _hasCompatibilityWarning;
    private int _relatedServiceCount;

    public MainViewModel()
    {
        _liveStatusTimer.Tick += LiveStatusTimer_Tick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> DiscoveredServices { get; } = [];
    public ObservableCollection<NetworkConnection> NetworkConnections { get; } = [];
    public IReadOnlyList<ShareDurationOption> ShareDurationOptions { get; } =
    [
        UnlimitedDuration,
        new("15 分钟", TimeSpan.FromMinutes(15)),
        new("30 分钟", TimeSpan.FromMinutes(30)),
        new("1 小时", TimeSpan.FromHours(1)),
        new("2 小时", TimeSpan.FromHours(2))
    ];

    public string TargetUrl
    {
        get => _targetUrl;
        set => SetField(ref _targetUrl, value);
    }

    public string? SelectedService
    {
        get => _selectedService;
        set
        {
            if (SetField(ref _selectedService, value) && value is not null)
            {
                TargetUrl = value;
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(StartButtonText));
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetField(ref _isActive, value))
            {
                OnPropertyChanged(nameof(IsInactive));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

    public bool IsInactive => !IsActive;
    public bool CanStart => !IsBusy && !IsActive && !IsDiagnosing && !IsStopping;
    public string StartButtonText => IsBusy ? "正在启动..." : "开始分享";
    public bool IsStopping
    {
        get => _isStopping;
        private set
        {
            if (SetField(ref _isStopping, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(StopButtonText));
            }
        }
    }
    public bool CanStop => IsActive && !IsStopping;
    public string StopButtonText => IsStopping ? "正在停止..." : "停止分享";

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetField(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public string ShareUrl
    {
        get => _shareUrl;
        private set => SetField(ref _shareUrl, value);
    }

    public string VisitorCountText
    {
        get => _visitorCountText;
        private set => SetField(ref _visitorCountText, value);
    }

    public BitmapImage? QrCode
    {
        get => _qrCode;
        private set => SetField(ref _qrCode, value);
    }

    public string SharePassword
    {
        get => _sharePassword;
        set => SetField(ref _sharePassword, value);
    }

    public ShareDurationOption SelectedShareDuration
    {
        get => _selectedShareDuration;
        set => SetField(ref _selectedShareDuration, value);
    }

    public string ShareStatusText
    {
        get => _shareStatusText;
        private set => SetField(ref _shareStatusText, value);
    }

    public string ShareAccessText
    {
        get => _shareAccessText;
        private set => SetField(ref _shareAccessText, value);
    }

    public bool HasCompatibilityWarning
    {
        get => _hasCompatibilityWarning;
        private set => SetField(ref _hasCompatibilityWarning, value);
    }

    public string AiRepairPrompt => "我用 LanDevLink 分享本机前后端项目时，其他设备有部分内容无法加载。请检查浏览器代码中写死的 localhost、127.0.0.1 或 ::1 地址，以及动态拼接的接口和 WebSocket 地址；改为通过页面同源地址访问，并确保开发服务器能将请求转发到相应服务。请勿修改生产环境配置。";

    public string DiagnosticUrl
    {
        get => _diagnosticUrl;
        set
        {
            if (SetField(ref _diagnosticUrl, value))
            {
                OnPropertyChanged(nameof(CanDiagnose));
            }
        }
    }

    public string DiagnosticResultTitle
    {
        get => _diagnosticResultTitle;
        private set
        {
            if (SetField(ref _diagnosticResultTitle, value))
            {
                OnPropertyChanged(nameof(HasDiagnosticResult));
            }
        }
    }

    public string DiagnosticResultDetails
    {
        get => _diagnosticResultDetails;
        private set => SetField(ref _diagnosticResultDetails, value);
    }

    public string NetworkStatusMessage
    {
        get => _networkStatusMessage;
        private set => SetField(ref _networkStatusMessage, value);
    }

    public bool IsDiagnosing
    {
        get => _isDiagnosing;
        private set
        {
            if (SetField(ref _isDiagnosing, value))
            {
                OnPropertyChanged(nameof(CanDiagnose));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(DiagnosticButtonText));
            }
        }
    }

    public bool CanDiagnose => !IsDiagnosing && !string.IsNullOrWhiteSpace(DiagnosticUrl);
    public bool HasDiagnosticResult => !string.IsNullOrEmpty(DiagnosticResultTitle);
    public string DiagnosticButtonText => IsDiagnosing ? "正在检测..." : "检测链接";

    public async Task ScanAsync()
    {
        RefreshNetworkInfo();
        StatusMessage = "正在查找本机开发服务";
        var services = await _scanner.ScanAsync();
        DiscoveredServices.Clear();
        foreach (var service in services)
        {
            DiscoveredServices.Add(service);
        }

        if (services.Count > 0)
        {
            SelectedService = services[0];
            StatusMessage = $"已发现 {services.Count} 个本机 HTTP 服务";
        }
        else
        {
            StatusMessage = "未发现常用端口，可直接输入服务地址";
        }
    }

    public void RefreshNetworkInfo(bool updateStatus = false)
    {
        var connections = NetworkDiagnosticsService.GetActiveIpv4Connections();
        NetworkConnections.Clear();
        foreach (var connection in connections)
        {
            NetworkConnections.Add(connection);
        }

        NetworkStatusMessage = connections.Count == 0
            ? "未检测到活动的 IPv4 连接"
            : $"已检测到 {connections.Count} 个活动 IPv4 连接";
        if (updateStatus)
        {
            StatusMessage = "当前网络信息已刷新";
        }
    }

    public async Task DiagnoseAsync()
    {
        IsDiagnosing = true;
        DiagnosticResultTitle = string.Empty;
        DiagnosticResultDetails = string.Empty;
        RefreshNetworkInfo();
        StatusMessage = "正在检测分享链接与当前网络";

        try
        {
            var result = await NetworkDiagnosticsService.DiagnoseAsync(DiagnosticUrl, NetworkConnections.ToArray());
            DiagnosticResultTitle = result.Title;
            DiagnosticResultDetails = result.Details;
            StatusMessage = "网络诊断已完成";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            DiagnosticResultTitle = "无法检测这个链接";
            DiagnosticResultDetails = exception.Message;
            StatusMessage = "网络诊断未完成";
        }
        finally
        {
            IsDiagnosing = false;
        }
    }

    public async Task StartAsync()
    {
        ErrorMessage = string.Empty;
        IsBusy = true;
        StatusMessage = "正在检查本机服务并创建分享";

        try
        {
            var target = TargetUrlValidator.Parse(TargetUrl);
            var result = await _server.StartAsync(target, SharePassword, SelectedShareDuration.Duration);
            ShareUrl = result.ShareUrl.ToString();
            QrCode = QrCodeService.Create(ShareUrl);
            _shareExpiresAt = result.ExpiresAt;
            HasCompatibilityWarning = result.HasCompatibilityWarning;
            _relatedServiceCount = result.RelatedServiceCount;
            IsActive = true;
            ShareAccessText = string.IsNullOrEmpty(SharePassword)
                ? "点击复制地址，或让同事扫描二维码。"
                : "已启用密码保护；访问者打开链接后需要输入密码。";
            StatusMessage = result.RelatedServiceCount > 0
                ? $"分享已就绪，已自动连接 {result.RelatedServiceCount} 个关联服务"
                : "分享已就绪";
            UpdateLiveStatus();
            _liveStatusTimer.Start();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "分享未启动";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StopAsync(string statusMessage = "分享已停止")
    {
        IsStopping = true;
        _liveStatusTimer.Stop();
        ErrorMessage = string.Empty;
        ShareStatusText = "正在停止";
        StatusMessage = "正在断开连接并停止分享";
        try
        {
            await _server.StopAsync();
            _shareExpiresAt = null;
            IsActive = false;
            ShareUrl = string.Empty;
            QrCode = null;
            VisitorCountText = "0";
            ShareStatusText = "持续运行";
            ShareAccessText = "点击复制地址，或让同事扫描二维码。";
            HasCompatibilityWarning = false;
            _relatedServiceCount = 0;
            StatusMessage = statusMessage;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            ShareStatusText = "停止失败";
            StatusMessage = $"停止分享失败，请重试：{exception.Message}";
        }
        finally
        {
            IsStopping = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _liveStatusTimer.Stop();
        await _server.DisposeAsync();
    }

    private void UpdateLiveStatus()
    {
        VisitorCountText = _server.VisitorCount.ToString();
        HasCompatibilityWarning = _server.HasCompatibilityWarning;
        if (_server.RelatedServiceCount > _relatedServiceCount)
        {
            _relatedServiceCount = _server.RelatedServiceCount;
            StatusMessage = $"分享已就绪，已自动连接 {_relatedServiceCount} 个关联服务";
        }
        if (_shareExpiresAt is null)
        {
            ShareStatusText = "持续运行";
            return;
        }

        var remaining = _shareExpiresAt.Value - DateTimeOffset.UtcNow;
        ShareStatusText = remaining.TotalHours >= 1
            ? $"剩余 {(int)remaining.TotalHours} 小时 {remaining.Minutes} 分钟"
            : $"剩余 {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} 分钟";
    }

    private async void LiveStatusTimer_Tick(object? sender, EventArgs e)
    {
        if (_shareExpiresAt is not null && _shareExpiresAt <= DateTimeOffset.UtcNow)
        {
            _liveStatusTimer.Stop();
            await StopAsync("分享已到期");
            return;
        }

        UpdateLiveStatus();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record ShareDurationOption(string Label, TimeSpan? Duration);

