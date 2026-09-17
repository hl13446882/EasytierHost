using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EasyTierHost.Abstractions;
using EasyTierHost.Core;

namespace EasyTierHost.Network;

public sealed class GatewayProbe : IGatewayProbe
{
    public async Task<bool> CheckAsync(GatewayContext context, CancellationToken ct)
    {
        if (!IPAddress.TryParse(context.OverlayIp, out var source) || !OverlayAddressPlan.IsClient(source)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var results = await Task.WhenAll(PingGatewayAsync(timeout.Token), DnsAsync(OverlayAddressPlan.Gateway, source, timeout.Token), DnsAsync("8.8.8.8", source, timeout.Token), HttpsAsync(source, timeout.Token));
            return results.All(ok => ok);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or PingException or OperationCanceledException)
        { ct.ThrowIfCancellationRequested(); return false; }
    }
    private static async Task<bool> PingGatewayAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        return (await ping.SendPingAsync(IPAddress.Parse(OverlayAddressPlan.Gateway), 3000).WaitAsync(ct)).Status == IPStatus.Success;
    }
    private static async Task<bool> DnsAsync(string address, IPAddress source, CancellationToken ct)
    {
        var query = DnsMessage.CreateQuery("example.com");
        var response = await DnsMessage.ExchangeAsync(new(IPAddress.Parse(address), 53), query, false, ct, source);
        return DnsMessage.IsResponseTo(query, response) && (response[3] & 15) == 0 && DnsMessage.HasAddressAnswer(response);
    }
    private static async Task<bool> HttpsAsync(IPAddress source, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (_, cancellation) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { socket.Bind(new IPEndPoint(source, 0)); await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443), cancellation); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }
        };
        using var http = new HttpClient(handler);
        using var response = await http.GetAsync("https://1.1.1.1/", HttpCompletionOption.ResponseHeadersRead, ct);
        return (int)response.StatusCode is >= 200 and < 400;
    }
}
