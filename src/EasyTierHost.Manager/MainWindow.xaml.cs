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
        RefreshClientPackage();
        ApplyDefaultUsername(SeedOs, SeedUsername);
        ApplyDefaultUsername(DedicatedOs, DedicatedUsername);
        ApplyDefaultUsername(ClientOs, ClientUsername);
    }

    private async void SeedTestConnection_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("测试 Seed SSH", ct => _deployment.TestConnectionAsync(BuildSeedOptions(includeSecret: false), ct));

    private async void SeedDiagnostics_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("读取 Seed 诊断", ct => _deployment.DiagnosticsAsync(BuildSeedOptions(includeSecret: false), ct));

    private async void SeedInstall_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("部署 Seed", ct => _deployment.InstallAsync(BuildSeedOptions(includeSecret: true), ct));

    private async void SeedUninstall_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("卸载 Seed", ct => _deployment.UninstallAsync(BuildSeedOptions(includeSecret: false), ct));

    private async void DedicatedTestConnection_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("测试专用服务器 SSH", ct => _deployment.TestConnectionAsync(BuildDedicatedOptions(includeSecret: false), ct));

    private async void DedicatedDiagnostics_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("读取专用服务器诊断", ct => _deployment.DiagnosticsAsync(BuildDedicatedOptions(includeSecret: false), ct));

    private async void DedicatedInstall_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("部署专用服务器", ct => _deployment.InstallAsync(BuildDedicatedOptions(includeSecret: true), ct));

    private async void DedicatedUninstall_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("卸载专用服务器", ct => _deployment.UninstallAsync(BuildDedicatedOptions(includeSecret: false), ct));

    private async void ClientTestConnection_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("测试普通客户端 SSH", ct => _deployment.TestConnectionAsync(BuildClientOptions(includeSecret: false), ct));

    private async void ClientDiagnostics_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("读取普通客户端诊断", ct => _deployment.DiagnosticsAsync(BuildClientOptions(includeSecret: false), ct));

    private async void ClientInstall_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("安装虚拟网", ct => _deployment.InstallAsync(BuildClientOptions(includeSecret: true), ct));

    private async void ClientUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "将远程停止并卸载该主机上的 EasyTierHost 虚拟网（服务、配置与程序）。继续？", "卸载虚拟网", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunOperationAsync("卸载虚拟网", ct => _deployment.UninstallAsync(BuildClientOptions(includeSecret: false), ct));
    }

    private void SeedOs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSeedPackage();
        ApplyDefaultUsername(SeedOs, SeedUsername);
    }

    private void DedicatedOs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSpecialDisplay();
        ApplyDefaultUsername(DedicatedOs, DedicatedUsername);
    }

    private void ClientOs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshClientPackage();
        ApplyDefaultUsername(ClientOs, ClientUsername);
    }

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

    private ManagerDeploymentOptions BuildClientOptions(bool includeSecret) => new()
    {
        OsType = SelectedOs(ClientOs),
        Role = NodeRole.Client,
        RemotePhysicalIp = ClientRemoteIp.Text.Trim(),
        SshPort = ParsePort(ClientSshPort.Text, "Client SSH port"),
        Username = ClientUsername.Text.Trim(),
        PrivateKeyPath = EmptyToNull(ClientPrivateKey.Text),
        SeedPhysicalIp = ClientSeedPhysicalIp.Text.Trim(),
        NetworkName = ClientNetworkName.Text.Trim(),
        NetworkSecret = includeSecret ? ClientNetworkSecret.Password : "connection-test-placeholder",
        ListenerPort = ParsePort(ClientListenerPort.Text, "Client listener port"),
        LocalPackageDirectory = ClientPackageDirectory.Text.Trim()
    };

    private async Task RunOperationAsync(string name, Func<CancellationToken, Task<DeploymentResult>> operation)
    {
        if (_operation is not null)
        {
            AppendLog("已有部署操作正在执行。\n");
            return;
        }

        _operation = new CancellationTokenSource();
        if (MainTabs is not null) MainTabs.IsEnabled = false;
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
        catch (HostException ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {ex.Code}: {ex.Message}\n");
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ERROR: {ex.GetType().Name}\n");
        }
        finally
        {
            if (MainTabs is not null) MainTabs.IsEnabled = true;
            _operation.Dispose();
            _operation = null;
        }
    }

    private void RefreshSeedPackage()
    {
        if (SeedOs is null || SeedPackageDirectory is null) return;
        var os = SelectedOs(SeedOs);
        var located = PackageDirectoryLocator.Locate("seed", os);
        if (PackageDirectoryLocator.IsManagedPath(SeedPackageDirectory.Text, "seed"))
            SeedPackageDirectory.Text = located;
    }

    private void RefreshSpecialDisplay()
    {
        if (DedicatedIndex is null || DedicatedOverlayIp is null) return;
        var index = SelectedIndex();
        DedicatedOverlayIp.Text = $"10.10.0.{index}";
        if (DedicatedPackageDirectory is not null)
        {
            var os = DedicatedOs is null ? ServerOsType.Linux : SelectedOs(DedicatedOs);
            var located = PackageDirectoryLocator.Locate("dedicated", os);
            if (PackageDirectoryLocator.IsManagedPath(DedicatedPackageDirectory.Text, "dedicated")
                || PackageDirectoryLocator.IsManagedPath(DedicatedPackageDirectory.Text, "gateway"))
                DedicatedPackageDirectory.Text = located;
        }
    }

    private void RefreshClientPackage()
    {
        if (ClientOs is null || ClientPackageDirectory is null) return;
        var located = PackageDirectoryLocator.Locate("client", SelectedOs(ClientOs));
        if (PackageDirectoryLocator.IsManagedPath(ClientPackageDirectory.Text, "client"))
            ClientPackageDirectory.Text = located;
    }

    private static void ApplyDefaultUsername(ComboBox os, TextBox username)
    {
        if (os is null || username is null) return;
        var selected = SelectedOs(os);
        var current = username.Text.Trim();
        if (selected == ServerOsType.Windows)
        {
            if (string.IsNullOrWhiteSpace(current) || current.Equals("root", StringComparison.OrdinalIgnoreCase))
                username.Text = "Administrator";
        }
        else if (string.IsNullOrWhiteSpace(current) || current.Equals("Administrator", StringComparison.OrdinalIgnoreCase))
        {
            username.Text = "root";
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
