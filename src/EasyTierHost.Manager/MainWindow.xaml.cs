using System.Windows;
using System.Windows.Controls;
using EasyTierHost.Abstractions;

namespace EasyTierHost.Manager;

public partial class MainWindow : Window
{
    private readonly ManagerDeploymentService _deployment = new();
    private CancellationTokenSource? _operation;

    public MainWindow()
    {
        InitializeComponent();
        RefreshSeedPackage();
        RefreshSpecialDisplay();
    }

    private async void SeedTestConnection_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("测试 Seed SSH", ct => _deployment.TestConnectionAsync(BuildSeedOptions(includeSecret: false), ct));

    private async void SeedInstall_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("部署 Seed", ct => _deployment.InstallAsync(BuildSeedOptions(includeSecret: true), ct));

    private async void DedicatedTestConnection_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("测试专用服务器 SSH", ct => _deployment.TestConnectionAsync(BuildDedicatedOptions(includeSecret: false), ct));

    private async void DedicatedInstall_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("部署专用服务器", ct => _deployment.InstallAsync(BuildDedicatedOptions(includeSecret: true), ct));

    private void SeedOs_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSeedPackage();
    private void DedicatedOs_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSpecialDisplay();
    private void DedicatedIndex_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSpecialDisplay();

    private ManagerDeploymentOptions BuildSeedOptions(bool includeSecret) => new()
    {
        OsType = SelectedOs(SeedOs),
        Role = NodeRole.Seed,
        RemotePhysicalIp = SeedRemoteIp.Text.Trim(),
        SshPort = ParsePort(SeedSshPort.Text, "Seed SSH port"),
        Username = SeedUsername.Text.Trim(),
        PrivateKeyPath = EmptyToNull(SeedPrivateKey.Text),
        NetworkName = SeedNetworkName.Text.Trim(),
        NetworkSecret = includeSecret ? SeedNetworkSecret.Password : "connection-test-placeholder",
        ListenerPort = ParsePort(SeedListenerPort.Text, "Seed listener port"),
        LocalPackageDirectory = SeedPackageDirectory.Text.Trim()
    };

    private ManagerDeploymentOptions BuildDedicatedOptions(bool includeSecret)
    {
        var index = SelectedIndex();
        return new ManagerDeploymentOptions
        {
            OsType = SelectedOs(DedicatedOs),
            Role = index == 1 ? NodeRole.Gateway : NodeRole.Dedicated,
            RemotePhysicalIp = DedicatedRemoteIp.Text.Trim(),
            SshPort = ParsePort(DedicatedSshPort.Text, "Dedicated SSH port"),
            Username = DedicatedUsername.Text.Trim(),
            PrivateKeyPath = EmptyToNull(DedicatedPrivateKey.Text),
            SeedPhysicalIp = DedicatedSeedPhysicalIp.Text.Trim(),
            DedicatedIndex = index == 1 ? null : index,
            NetworkName = DedicatedNetworkName.Text.Trim(),
            NetworkSecret = includeSecret ? DedicatedNetworkSecret.Password : "connection-test-placeholder",
            ListenerPort = ParsePort(DedicatedListenerPort.Text, "Dedicated listener port"),
            LocalPackageDirectory = DedicatedPackageDirectory.Text.Trim()
        };
    }

    private async Task RunOperationAsync(string name, Func<CancellationToken, Task<DeploymentResult>> operation)
    {
        if (_operation is not null)
        {
            AppendLog("已有部署操作正在执行。\n");
            return;
        }

        _operation = new CancellationTokenSource();
        try
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {name}...\n");
            var result = await operation(_operation.Token);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {(result.Success ? "OK" : result.Code)}: {result.Message}\n");
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 操作已取消。\n");
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ERROR: {ex.GetType().Name}\n");
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
        }
    }

    private void RefreshSeedPackage()
    {
        if (SeedOs is null || SeedPackageDirectory is null) return;
        var os = SelectedOs(SeedOs);
        var suffix = os == ServerOsType.Windows ? "windows" : "linux";
        var current = SeedPackageDirectory.Text.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(current) || current.StartsWith("publish/seed-", StringComparison.OrdinalIgnoreCase))
            SeedPackageDirectory.Text = $"publish/seed-{suffix}";
    }

    private void RefreshSpecialDisplay()
    {
        if (DedicatedIndex is null || DedicatedOverlayIp is null) return;
        var index = SelectedIndex();
        DedicatedOverlayIp.Text = $"10.10.0.{index}";
        if (DedicatedPackageDirectory is not null)
        {
            var os = DedicatedOs is null ? ServerOsType.Linux : SelectedOs(DedicatedOs);
            var role = index == 1 ? "gateway" : "dedicated";
            var suffix = os == ServerOsType.Windows ? "windows" : "linux";
            var current = DedicatedPackageDirectory.Text.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(current) || current.StartsWith("publish/gateway-", StringComparison.OrdinalIgnoreCase) || current.StartsWith("publish/dedicated-", StringComparison.OrdinalIgnoreCase))
                DedicatedPackageDirectory.Text = $"publish/{role}-{suffix}";
        }
    }

    private static ServerOsType SelectedOs(ComboBox combo)
    {
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not string value)
            throw new HostException("ETH003", "Server OS must be selected");
        return Enum.Parse<ServerOsType>(value, ignoreCase: true);
    }

    private int SelectedIndex()
    {
        if (DedicatedIndex.SelectedItem is not ComboBoxItem item || item.Tag is not string value || !int.TryParse(value, out var index) || index is < 1 or > 10)
            throw new HostException("ETH003", "Dedicated index must be 1..10");
        return index;
    }

    private static int ParsePort(string value, string label)
    {
        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
            throw new HostException("ETH003", $"{label} must be 1..65535");
        return port;
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void AppendLog(string text)
    {
        DeploymentLog.AppendText(text);
        DeploymentLog.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        _operation?.Cancel();
        base.OnClosed(e);
    }
}
