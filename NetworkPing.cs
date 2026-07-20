using System.Net.NetworkInformation;

namespace Pulse;

/// <summary>
/// Shared ICMP ping helper: send one ping, get back round-trip ms or null on
/// any failure/timeout. Used by the widget's live ping display, the outage
/// monitor, and the speed test so each doesn't reimplement the same
/// send-and-interpret logic.
/// </summary>
public static class NetworkPing
{
    public static async Task<long?> PingAsync(string host, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch
        {
            return null;
        }
    }
}
