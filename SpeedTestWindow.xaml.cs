using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows;

namespace InternetSpeedWidget;

public partial class SpeedTestWindow : Window
{
    private static readonly HttpClient Http = CreateHttp();
    private bool _running;

    // Tried in order until one succeeds (Cloudflare first, then mirrors).
    private static readonly string[] DownloadSources =
    {
        "https://speed.cloudflare.com/__down?bytes=90000000", // caps below 100 MB
        "https://proof.ovh.net/files/100Mb.dat",
        "https://download.thinkbroadband.com/100MB.zip",
    };

    /// <summary>
    /// HttpClient that connects over IPv4 only. On machines with a broken
    /// IPv6 route (AAAA resolves but packets go nowhere) the default handler
    /// dials IPv6 first and hangs until the timeout.
    /// </summary>
    private static HttpClient CreateHttp()
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
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public SpeedTestWindow()
    {
        InitializeComponent();
        StartButton.Click += async (_, _) => await RunTestAsync();
    }

    private async Task RunTestAsync()
    {
        if (_running)
            return;
        _running = true;
        StartButton.IsEnabled = false;
        PingResult.Text = DownResult.Text = UpResult.Text = "—";

        string stage = "starting";
        try
        {
            stage = "ping";
            StatusText.Text = "Measuring ping…";
            PingResult.Text = await MeasurePingAsync();

            stage = "download";
            StatusText.Text = "Measuring download (up to 8 s)…";
            DownResult.Text = FormatMbps(await MeasureDownloadAsync());

            stage = "upload";
            StatusText.Text = "Measuring upload (up to 6 s)…";
            UpResult.Text = FormatMbps(await MeasureUploadAsync());

            StatusText.Text = "Done.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Test failed during {stage}: {ex.Message}";
        }
        finally
        {
            _running = false;
            StartButton.IsEnabled = true;
            StartButton.Content = "Run Again";
        }
    }

    private static async Task<string> MeasurePingAsync()
    {
        long total = 0;
        int ok = 0;
        for (int i = 0; i < 3; i++)
        {
            long? ms = await NetworkPing.PingAsync("1.1.1.1", 2000);
            if (ms.HasValue)
            {
                total += ms.Value;
                ok++;
            }
        }
        return ok > 0 ? $"{total / ok} ms" : "failed";
    }

    private static async Task<double> MeasureDownloadAsync()
    {
        Exception? lastError = null;
        foreach (var url in DownloadSources)
        {
            try
            {
                return await MeasureDownloadFromAsync(url);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new InvalidOperationException("No download sources available.");
    }

    private static async Task<double> MeasureDownloadFromAsync(string url)
    {
        // Stream the payload repeatedly until the 8-second window is up.
        var buffer = new byte[81920];
        long bytes = 0;
        var start = DateTime.UtcNow;

        while ((DateTime.UtcNow - start).TotalSeconds < 8)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            int read;
            while ((DateTime.UtcNow - start).TotalSeconds < 8 &&
                   (read = await stream.ReadAsync(buffer)) > 0)
            {
                bytes += read;
            }
        }

        double seconds = Math.Max(0.5, (DateTime.UtcNow - start).TotalSeconds);
        return bytes / seconds; // bytes per second
    }

    private static async Task<double> MeasureUploadAsync()
    {
        var payload = new byte[8 * 1024 * 1024];
        new Random().NextBytes(payload);

        long bytes = 0;
        var start = DateTime.UtcNow;

        while ((DateTime.UtcNow - start).TotalSeconds < 6)
        {
            using var content = new ByteArrayContent(payload);
            using var response = await Http.PostAsync("https://speed.cloudflare.com/__up", content);
            response.EnsureSuccessStatusCode();
            bytes += payload.Length;
        }

        double seconds = Math.Max(0.5, (DateTime.UtcNow - start).TotalSeconds);
        return bytes / seconds;
    }

    private static string FormatMbps(double bytesPerSec) =>
        SpeedMonitor.Format(bytesPerSec, SpeedUnit.Mbps);
}
