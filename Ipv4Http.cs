using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Pulse;

/// <summary>
/// Builds HttpClients that connect over IPv4 only.
///
/// On machines with a broken IPv6 route (AAAA records resolve but packets go
/// nowhere) the default handler dials IPv6 first and hangs until the request
/// times out. Every outbound HTTP call in this app must go through here —
/// the speed test and the weather fetch both depend on it.
/// </summary>
public static class Ipv4Http
{
    public static HttpClient Create(int timeoutSeconds)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(
                    ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, ct);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }
}
