using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace EasyTierHost.Core;

public static class DnsMessage
{
    public static byte[] CreateQuery(string name)
    {
        using var stream = new MemoryStream();
        byte[] header = new byte[12]; RandomNumberGenerator.Fill(header.AsSpan(0, 2));
        header[2] = 1; header[5] = 1; stream.Write(header);
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is < 1 or > 63) throw new ArgumentException("Invalid DNS name");
            stream.WriteByte((byte)bytes.Length); stream.Write(bytes);
        }
        stream.Write([0, 0, 1, 0, 1]); // A / IN
        return stream.ToArray();
    }
    public static string Question(ReadOnlySpan<byte> packet, out int end)
    {
        if (packet.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(packet[4..]) != 1) throw new IOException("Expected one DNS question");
        int offset = 12, consumed = -1, hops = 0, length = 0;
        var result = new StringBuilder();
        var visited = new HashSet<int>();
        while (true)
        {
            if (offset >= packet.Length || !visited.Add(offset) || ++hops > 128) throw new IOException("Invalid DNS name pointer");
            byte size = packet[offset++];
            if (size == 0) break;
            if ((size & 0xc0) == 0xc0)
            {
                if (offset >= packet.Length) throw new IOException("Truncated DNS pointer");
                if (consumed < 0) consumed = offset + 1;
                offset = ((size & 0x3f) << 8) | packet[offset];
                continue;
            }
            if (size > 63 || offset + size > packet.Length || (length += size + 1) > 254) throw new IOException("Invalid DNS label");
            result.Append(size).Append(':');
            for (int i = 0; i < size; i++)
            {
                var b = packet[offset++]; if (b is >= (byte)'A' and <= (byte)'Z') b += 32;
                result.Append(b.ToString("X2"));
            }
            result.Append('/');
        }
        end = (consumed < 0 ? offset : consumed) + 4;
        if (end > packet.Length) throw new IOException("Truncated DNS question");
        result.Append(Convert.ToHexString(packet.Slice(end - 4, 4)));
        return result.ToString();
    }
    public static bool IsResponseTo(byte[] query, byte[] response)
    {
        try
        {
            return response.Length >= 12 && response[0] == query[0] && response[1] == query[1]
                && (response[2] & 0x80) != 0 && (query[2] & 0x78) == (response[2] & 0x78)
                && Question(query, out _) == Question(response, out _);
        }
        catch (IOException) { return false; }
    }
    public static byte[] Failure(byte[] query)
    {
        Question(query, out var end);
        var result = query[..end]; result[2] = (byte)((query[2] & 0x79) | 0x80); result[3] = 0x82;
        Array.Clear(result, 6, 6); return result;
    }
    public static bool HasAddressAnswer(byte[] response)
    {
        try
        {
            Question(response, out var offset);
            var count = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6));
            if (count > 128) return false;
            for (int i = 0; i < count; i++)
            {
                while (true)
                {
                    if (offset >= response.Length) return false;
                    int label = response[offset++];
                    if (label == 0) break;
                    if ((label & 0xc0) == 0xc0)
                    {
                        if (offset >= response.Length || (((label & 0x3f) << 8) | response[offset]) >= offset - 1) return false;
                        offset++; break;
                    }
                    if (label > 63 || offset + label >= response.Length) return false;
                    offset += label;
                }
                if (offset + 10 > response.Length) return false;
                int type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset));
                int cls = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 2));
                int size = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 8));
                offset += 10;
                if (offset + size > response.Length) return false;
                if (cls == 1 && ((type == 1 && size == 4) || (type == 28 && size == 16))) return true;
                offset += size;
            }
            return false;
        }
        catch (IOException) { return false; }
    }
    public static async Task<byte[]> ExchangeAsync(IPEndPoint endpoint, byte[] query, bool tcp, CancellationToken ct)
    {
        if (!tcp)
        {
            using var socket = new UdpClient(endpoint.AddressFamily); socket.Connect(endpoint);
            await socket.SendAsync(query, ct);
            return (await socket.ReceiveAsync(ct)).Buffer;
        }
        using var client = new TcpClient(endpoint.AddressFamily);
        await client.ConnectAsync(endpoint.Address, endpoint.Port, ct);
        await WriteFrameAsync(client.GetStream(), query, ct);
        return await ReadFrameAsync(client.GetStream(), ct);
    }
    public static async Task<bool> ProbeAsync(IPEndPoint endpoint, bool tcp, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var query = CreateQuery("example.com"); var response = await ExchangeAsync(endpoint, query, tcp, timeout.Token);
            return IsResponseTo(query, response) && (response[3] & 15) == 0 && HasAddressAnswer(response);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { ct.ThrowIfCancellationRequested(); return false; }
    }
    public static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] size = new byte[2]; await stream.ReadExactlyAsync(size, ct);
        int length = BinaryPrimitives.ReadUInt16BigEndian(size);
        if (length < 12) throw new IOException("Invalid DNS TCP frame");
        byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, ct); return data;
    }
    public static async Task WriteFrameAsync(NetworkStream stream, byte[] data, CancellationToken ct)
    {
        byte[] size = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(size, checked((ushort)data.Length));
        await stream.WriteAsync(size, ct); await stream.WriteAsync(data, ct);
    }
}
