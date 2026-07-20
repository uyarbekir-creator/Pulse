using System.IO;
using System.Media;
using System.Windows.Threading;

namespace Pulse;

/// <summary>
/// Experimental: plays subtle Geiger-counter clicks whose rate follows the
/// current network traffic (Poisson-style random timing, like real radiation).
/// The click is a tiny generated WAV played through System.Media.SoundPlayer,
/// which copies the buffer internally, so there are no lifetime issues.
/// </summary>
public class GeigerPlayer
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Random _rng = new();
    private readonly SoundPlayer? _player;
    private double _clicksPerSecond;

    public GeigerPlayer()
    {
        try
        {
            _player = new SoundPlayer(new MemoryStream(BuildClickWav()));
            _player.Load();
        }
        catch
        {
            _player = null; // no audio device — stay silent
        }
        _timer.Tick += (_, _) => Tick();
    }

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value && !_timer.IsEnabled) _timer.Start();
            else if (!value && _timer.IsEnabled) _timer.Stop();
        }
    }

    /// <summary>Plays a single click immediately (feedback when enabling).</summary>
    public void ClickOnce()
    {
        try { _player?.Play(); } catch { }
    }

    /// <summary>Update the click rate from the current total traffic.</summary>
    public void SetRate(double totalBytesPerSec)
    {
        // ~1 KB/s -> ~3 clicks/s, ~1 MB/s -> ~9/s, capped at 20/s.
        _clicksPerSecond = totalBytesPerSec < 300
            ? 0
            : Math.Min(20, 2.2 * Math.Log10(totalBytesPerSec / 50.0));
    }

    private void Tick()
    {
        // Poisson process: independent click chance per 50 ms slice.
        if (_clicksPerSecond > 0 && _rng.NextDouble() < _clicksPerSecond * 0.05)
            ClickOnce();
    }

    /// <summary>
    /// Generates a ~60 ms Geiger tick as a WAV: a sharp noise attack plus a
    /// decaying 1.5 kHz ping. Long and loud enough to be clearly audible
    /// (the first 7 ms version was too short to hear on most speakers).
    /// </summary>
    private static byte[] BuildClickWav()
    {
        const int sampleRate = 44100;
        const int samples = 2600;
        var rng = new Random(1234);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        int dataSize = samples * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataSize);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);              // PCM
        w.Write((short)1);              // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);        // byte rate
        w.Write((short)2);              // block align
        w.Write((short)16);             // bits per sample
        w.Write("data"u8);
        w.Write(dataSize);

        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)sampleRate;
            double noise = (rng.NextDouble() * 2 - 1) * Math.Exp(-i / 60.0) * 0.9;
            double ping = Math.Sin(2 * Math.PI * 1500 * t) * Math.Exp(-i / 420.0) * 0.7;
            double v = Math.Clamp(noise + ping, -1.0, 1.0) * 0.95;
            w.Write((short)(v * short.MaxValue));
        }

        return ms.ToArray();
    }
}
