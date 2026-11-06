using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using LanDevLink.ViewModels;

namespace LanDevLink;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _closingAfterStop;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.ScanAsync();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_closingAfterStop && _viewModel.IsActive)
        {
            e.Cancel = true;
            _closingAfterStop = true;
            await _viewModel.StopAsync();
            if (_viewModel.IsActive)
            {
                _closingAfterStop = false;
                return;
            }
            Close();
            return;
        }

        await _viewModel.DisposeAsync();
        base.OnClosing(e);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Scan_Click(object sender, RoutedEventArgs e) => await _viewModel.ScanAsync();
    private void RefreshNetwork_Click(object sender, RoutedEventArgs e) => _viewModel.RefreshNetworkInfo(true);
    private async void Diagnose_Click(object sender, RoutedEventArgs e) => await _viewModel.DiagnoseAsync();

    private void ShowDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        ShareView.Visibility = Visibility.Collapsed;
        DiagnosticsView.Visibility = Visibility.Visible;
        StartShareButton.IsDefault = false;
        DiagnosticActionButton.IsDefault = true;
        _viewModel.RefreshNetworkInfo();
        DiagnosticUrlTextBox.Focus();
    }

    private void ShowShare_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsView.Visibility = Visibility.Collapsed;
        ShareView.Visibility = Visibility.Visible;
        DiagnosticActionButton.IsDefault = false;
        StartShareButton.IsDefault = true;
        DiagnosticsNavButton.Focus();
    }

    private async void StartShare_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartAsync();
    }

    private void SharePassword_Changed(object sender, RoutedEventArgs e)
    {
        _viewModel.SharePassword = SharePasswordBox.Password;
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewModel.ShareUrl))
        {
            return;
        }

        Clipboard.SetText(_viewModel.ShareUrl);
        _viewModel.StatusMessage = "链接已复制到剪贴板";
    }

    private void CopyRepairPrompt_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_viewModel.AiRepairPrompt);
        _viewModel.StatusMessage = "修复指令已复制到剪贴板";
    }

    private async void StopShare_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanStop) return;

        var result = MessageBox.Show(
            this,
            "停止后，当前链接会立即失效。",
            "停止分享？",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (result == MessageBoxResult.OK)
        {
            await _viewModel.StopAsync();
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
