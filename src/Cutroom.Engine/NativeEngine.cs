using System.Runtime.InteropServices;

namespace Cutroom.Engine;

public enum LayerKind { Video = 0, Texture = 1, Color = 2, Paper = 3, Blur = 4 }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct Layer
{
    public LayerKind Kind;
    public int Media;
    public double SourceSeconds;
    public int Texture;
    public int Fit;
    public float R, G, B, A;
    public float Cx, Cy, W, H;
    public float Rotation;
    public float Opacity;
    public float U0, V0, U1, V1;
    public float CornerRadius;
    public float Softness;
    public float Blur;
    public float Dim;
    public float Time;
    public int WipeDir;
    public float Wipe;
    float pad;

    public static Layer Full(LayerKind kind) => new() { Kind = kind, Cx = .5f, Cy = .5f, W = 1, H = 1, Opacity = 1, U1 = 1, V1 = 1, Fit = 0 };
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeMediaInfo
{
    public int Width, Height;
    public double Fps;
    public double Duration;
    public int HasVideo, HasAudio;
}

public sealed class EngineException(string message) : Exception(message);

/// <summary>Owns one native engine (D3D11 device, canvas, decoders). Not thread-safe: use one per thread.</summary>
public sealed class NativeEngine : IDisposable
{
    const string Dll = "Cutroom.Engine.Native.dll";
    [DllImport(Dll)] static extern IntPtr cr_create(int width, int height, int useWarp);
    [DllImport(Dll)] static extern void cr_destroy(IntPtr e);
    [DllImport(Dll)] static extern IntPtr cr_last_error();
    [DllImport(Dll)] static extern IntPtr cr_adapter(IntPtr e);
    [DllImport(Dll, CharSet = CharSet.Unicode)] static extern int cr_open(IntPtr e, string path, int maxHeight);
    [DllImport(Dll)] static extern int cr_media_info(IntPtr e, int media, out NativeMediaInfo info);
    [DllImport(Dll)] static extern void cr_close(IntPtr e, int media);
    [DllImport(Dll)] static extern unsafe int cr_upload(IntPtr e, int id, int w, int h, void* bgra, int stride);
    [DllImport(Dll)] static extern void cr_release_texture(IntPtr e, int id);
    [DllImport(Dll)] static extern void cr_begin(IntPtr e, float r, float g, float b);
    [DllImport(Dll)] static extern int cr_draw(IntPtr e, in Layer layer);
    [DllImport(Dll)] static extern int cr_attach(IntPtr e, IntPtr hwnd, int w, int h);
    [DllImport(Dll)] static extern int cr_resize(IntPtr e, int swap, int w, int h);
    [DllImport(Dll)] static extern void cr_detach(IntPtr e, int swap);
    [DllImport(Dll)] static extern int cr_present(IntPtr e, int swap);
    [DllImport(Dll)] static extern unsafe int cr_readback(IntPtr e, void* bgra, int stride);

    IntPtr handle;
    public int Width { get; }
    public int Height { get; }
    public string Adapter { get; }

    public NativeEngine(int width = 1920, int height = 1080, bool warp = false)
    {
        handle = cr_create(width, height, warp ? 1 : 0);
        if (handle == IntPtr.Zero) throw new EngineException(LastError());
        Width = width;
        Height = height;
        Adapter = Marshal.PtrToStringUTF8(cr_adapter(handle)) ?? "";
    }

    static string LastError() => Marshal.PtrToStringUTF8(cr_last_error()) ?? "Unknown engine error";

    IntPtr H => handle != IntPtr.Zero ? handle : throw new ObjectDisposedException(nameof(NativeEngine));

    public int Open(string path, int maxHeight = 0)
    {
        int id = cr_open(H, path, maxHeight);
        if (id < 0) throw new EngineException($"{LastError()}: {Path.GetFileName(path)}");
        return id;
    }

    public NativeMediaInfo Info(int media)
    {
        if (cr_media_info(H, media, out var info) != 0) throw new EngineException(LastError());
        return info;
    }

    public void Close(int media) => cr_close(H, media);

    public unsafe void Upload(int id, int width, int height, ReadOnlySpan<byte> bgra, int stride)
    {
        fixed (byte* p = bgra)
            if (cr_upload(H, id, width, height, p, stride) < 0) throw new EngineException(LastError());
    }

    public void ReleaseTexture(int id) => cr_release_texture(H, id);
    public void Begin(float r = 0, float g = 0, float b = 0) => cr_begin(H, r, g, b);

    public void Draw(in Layer layer)
    {
        if (cr_draw(H, layer) != 0) throw new EngineException(LastError());
    }

    public int Attach(IntPtr hwnd, int width, int height)
    {
        int id = cr_attach(H, hwnd, width, height);
        if (id < 0) throw new EngineException(LastError());
        return id;
    }

    public void Resize(int swap, int width, int height)
    {
        if (cr_resize(H, swap, width, height) != 0) throw new EngineException(LastError());
    }

    public void Detach(int swap) => cr_detach(H, swap);

    public void Present(int swap)
    {
        if (cr_present(H, swap) != 0) throw new EngineException(LastError());
    }

    public unsafe void Readback(Span<byte> bgra)
    {
        if (bgra.Length < Width * Height * 4) throw new ArgumentException("Buffer too small", nameof(bgra));
        fixed (byte* p = bgra)
            if (cr_readback(H, p, Width * 4) != 0) throw new EngineException(LastError());
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) cr_destroy(handle);
        handle = IntPtr.Zero;
    }
}
