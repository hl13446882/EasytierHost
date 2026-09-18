using System.Text.Json;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

var failures = 0;
foreach (var (name, test) in new (string Name, Action Test)[]
{
    ("Client profile uses DHCP role and optional gateway", ClientProfile),
    ("Client profile rejects overlay Seed address", RejectOverlaySeed),
    ("Client profile never serializes network secret", SecretNotSerialized)
})
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}

Console.WriteLine($"{3 - failures}/3 passed");
return failures == 0 ? 0 : 1;

static NetworkProfile Build(bool gateway = true) => NetworkProfileBuilder.CreateClient(
    "company-overlay", "192.0.2.10", "network.secret", "easytier-core", "easytier-cli", gateway);

static void Check(bool value, string reason)
{
    if (!value) throw new InvalidOperationException(reason);
}

static void ClientProfile()
{
    var profile = Build();
    Check(profile.Role == NodeRole.Client, "wrong role");
    Check(profile.EnableInternetGateway, "gateway flag was lost");
    Check(profile.DedicatedIndex is null, "client received a dedicated index");
    var config = EasyTierConfigBuilder.Build(profile, "test-secret");
    Check(config.Contains("dhcp = true", StringComparison.Ordinal), "DHCP is not enabled");
    Check(config.Contains("exit_nodes = [\"10.10.0.1\"]", StringComparison.Ordinal), "gateway exit node missing");
}

static void RejectOverlaySeed()
{
    try
    {
        _ = NetworkProfileBuilder.CreateClient("company-overlay", "10.10.0.1", "network.secret", "easytier-core", "easytier-cli", true);
    }
    catch (HostException) { return; }
    throw new InvalidOperationException("overlay Seed address was accepted");
}

static void SecretNotSerialized()
{
    const string marker = "must-never-appear-in-profile";
    var json = JsonSerializer.Serialize(Build(), ConfigurationStore.Json);
    Check(!json.Contains(marker, StringComparison.Ordinal), "secret leaked into profile");
    Check(json.Contains("network.secret", StringComparison.Ordinal), "secret file reference missing");
}
