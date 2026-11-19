using Cutroom.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Cutroom.Audio;

/// <summary>
/// Plays the mixer through WASAPI and is the master clock for A/V sync: the video playhead follows what the
/// speakers have actually played.
/// </summary>
public sealed class Player : IDisposable
{
    sealed class MixProvider(Player owner) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        public int Read(Span<float> span)
        {
            int count = span.Length;
            if (owner.clip is { } c)
            {
                int n = Math.Min(count, c.Length - owner.clipPos);
                if (n > 0) c.AsSpan(owner.clipPos, n).CopyTo(span);
                span[Math.Max(0, n)..].Clear();
                owner.clipPos += Math.Max(0, n);
                return count;
            }
            int samples = owner.endTick is { } end
                ? (int)Math.Clamp(end - owner.cursor, 0, count / 2) * 2
                : count;
            if (samples > 0) owner.mixer?.Render(owner.cursor, span[..samples]);
            span[samples..].Clear();
            owner.cursor += count / 2;
            return count;
        }
    }

    WasapiOut? output;
    Mixer? mixer;
    long cursor;
    long startTick;
    long? endTick;
    float[]? clip;
    int clipPos;

    public bool Playing => output?.PlaybackState == PlaybackState.Playing;
    public event Action? Stopped;

    /// <summary>Starts mixer playback at a timeline position.</summary>
    public void Play(Mixer m, long from, long? until = null)
    {
        Stop();
        mixer = m;
        clip = null;
        cursor = startTick = from;
        endTick = until;
        Start();
    }

    /// <summary>Plays a prepared stereo buffer (before/after auditions in the Clean stage).</summary>
    public void PlayBuffer(float[] stereo)
    {
        Stop();
        clip = stereo;
        clipPos = 0;
        startTick = 0;
        Start();
    }

    void Start()
    {
        try
        {
            output = new WasapiOut(AudioClientShareMode.Shared, true, 40);
            output.Init(new MixProvider(this));
            output.PlaybackStopped += (_, _) => Stopped?.Invoke();
            output.Play();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            output?.Dispose();
            output = null; // No audio device: playback still runs on the wall clock.
        }
    }

    /// <summary>Timeline position currently audible, or null when no device is playing.</summary>
    public long? Clock
    {
        get
        {
            if (output is null || clip is not null) return null;
            try
            {
                long bytes = output.GetPosition();
                return startTick + bytes / 8;
            }
            catch (Exception) { return null; }
        }
    }

    public bool BufferFinished => clip is not null && clipPos >= clip.Length;

    public void Stop()
    {
        var o = output;
        output = null;
        o?.Stop();
        o?.Dispose();
    }

    public void Dispose() => Stop();
}

public sealed record InputDevice(string Id, string Name);

/// <summary>
/// Records the microphone to a 24-bit WAV that is playable even if Cutroom crashes mid-take
/// (the header is rewritten every second).
/// </summary>
public sealed class Recorder : IDisposable
{
    WasapiCapture? capture;
    WaveFileWriter? writer;
    DateTime lastFlush;
    long frames;
    int rate;
    public string? Path { get; private set; }
    public bool Recording => capture is not null;
    public bool Paused { get; set; }
    public float Peak { get; private set; }
    public bool Clipped { get; private set; }
    public TimeSpan Elapsed => rate > 0 ? TimeSpan.FromSeconds(frames / (double)rate) : TimeSpan.Zero;
    /// <summary>Elapsed time in project ticks, for retake marks.</summary>
    public long ElapsedTicks => rate > 0 ? frames * Time.Rate / rate : 0;
    public event Action<float>? Level;
    public event Action<string>? Failed;

    public static IReadOnlyList<InputDevice> Devices()
    {
        using var e = new MMDeviceEnumerator();
        return e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => new InputDevice(d.ID, d.FriendlyName)).ToList();
    }

    public void Start(string path, string? deviceId)
    {
        Stop();
        using var e = new MMDeviceEnumerator();
        var device = deviceId is null ? e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.ID == deviceId) ?? e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        capture = new WasapiCapture(device, true, 50);
        var format = capture.WaveFormat;
        rate = format.SampleRate;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        writer = new WaveFileWriter(path, new WaveFormat(rate, 24, 1));
        Path = path;
        frames = 0;
        Clipped = false;
        lastFlush = DateTime.UtcNow;
        capture.DataAvailable += (_, a) => Write(a.Buffer, a.BytesRecorded, format);
        capture.RecordingStopped += (_, a) => { if (a.Exception is not null) Failed?.Invoke(a.Exception.Message); };
        capture.StartRecording();
    }

    void Write(byte[] buffer, int bytes, WaveFormat format)
    {
        var w = writer;
        if (w is null) return;
        int channels = format.Channels;
        int bytesPerSample = format.BitsPerSample / 8;
        int count = bytes / (bytesPerSample * channels);
        float peak = 0;
        var output = new byte[count * 3];
        for (int i = 0; i < count; i++)
        {
            float s = format.Encoding == WaveFormatEncoding.IeeeFloat || format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32
                ? BitConverter.ToSingle(buffer, i * channels * 4)
                : BitConverter.ToInt16(buffer, i * channels * bytesPerSample) / 32768f;
            peak = Math.Max(peak, Math.Abs(s));
            int v = (int)Math.Clamp(s * 8_388_607.0, -8_388_608, 8_388_607);
            output[i * 3] = (byte)v;
            output[i * 3 + 1] = (byte)(v >> 8);
            output[i * 3 + 2] = (byte)(v >> 16);
        }
        Peak = peak;
        if (peak >= .99f) Clipped = true;
        Level?.Invoke(peak);
        if (Paused) return;
        w.Write(output, 0, output.Length);
        frames += count;
        if (DateTime.UtcNow - lastFlush > TimeSpan.FromSeconds(1))
        {
            w.Flush();
            lastFlush = DateTime.UtcNow;
        }
    }

    public string? Stop()
    {
        var c = capture;
        capture = null;
        if (c is not null)
        {
            c.StopRecording();
            c.Dispose();
        }
        writer?.Dispose();
        writer = null;
        return Path;
    }

    public void Dispose() => Stop();
}
