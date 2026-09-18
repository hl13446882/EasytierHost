using System.Windows;
using EasyTierHost.Client.Windows.Services;
using EasyTierHost.Client.Windows.ViewModels;

namespace EasyTierHost.Client.Windows;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly MainViewModel viewModel;

    public MainWindow()
    {
        InitializeComponent();
        var paths = ClientRuntimePaths.Discover();
        var service = new EasyTierClientService(paths);
        var health = new ClientHealthMonitor(paths, service);
        var gateway = new ClientGatewayCoordinator(health);
        var bootstrapper = new ClientBootstrapper(paths, service, gateway);
        viewModel = new MainViewModel(bootstrapper, health, service);
        DataContext = viewModel;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => lifetime.Cancel();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) =>
        await viewModel.InitializeAsync(lifetime.Token);

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var secret = NetworkSecret.Password;
        try { await viewModel.ConnectAsync(secret, lifetime.Token); }
        finally { NetworkSecret.Clear(); }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e) =>
        await viewModel.DisconnectAsync(lifetime.Token);

    private async void Reconnect_Click(object sender, RoutedEventArgs e) =>
        await viewModel.ReconnectAsync(lifetime.Token);

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await viewModel.RefreshAsync(lifetime.Token);

    private async void Diagnostics_Click(object sender, RoutedEventArgs e) =>
        await viewModel.DiagnosticsAsync(lifetime.Token);
}
