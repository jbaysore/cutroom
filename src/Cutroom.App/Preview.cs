using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Cutroom.Core;
using Cutroom.Engine;

namespace Cutroom.App;

/// <summary>A Win32 child window the native engine presents into (WPF cannot draw over it, so keep overlays outside).</summary>
public sealed class PreviewHost : HwndHost
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);

    public event Action<int, int>? Resized;
    public event Action<Point>? Clicked;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, SS_NOTIFY = 0x100;
        var child = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | SS_NOTIFY, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, child);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        var dpi = VisualTreeHelper.GetDpi(this);
        Resized?.Invoke(Math.Max(1, (int)(info.NewSize.Width * dpi.DpiScaleX)), Math.Max(1, (int)(info.NewSize.Height * dpi.DpiScaleY)));
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_LBUTTONDOWN = 0x0201;
        if (msg == WM_LBUTTONDOWN)
        {
            int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            var dpi = VisualTreeHelper.GetDpi(this);
            Clicked?.Invoke(new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY));
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }
}

/// <summary>
/// Owns the preview engine and draws either the timeline or the source viewer into one or two surfaces
/// (the main preview and the optional pop-out on the second monitor).
/// </summary>
public sealed class PreviewRenderer : IDisposable
{
    readonly Session session;
    readonly NativeEngine engine;
    readonly Composer composer;
    readonly Dictionary<PreviewHost, int> swaps = [];
    long lastDrawn = -1;
    int lastRevision = -1;
    bool lastSource;
    bool dirty = true;
    Project? sourceProject;
    string? sourceId;

    public string Adapter => engine.Adapter;

    public PreviewRenderer(Session session)
    {
        this.session = session;
        engine = new NativeEngine(1920, 1080);
        composer = new Composer(engine, session.Settings, session.PreviewPathFor) { MediaError = (m, e) => session.Say($"Can't play {m.Name}: {e}") };
        session.ProjectChanged += () => dirty = true;
        session.SourceChanged += () => dirty = true;
        session.SelectionChanged += () => dirty = true;
    }

    public void Attach(PreviewHost host)
    {
        void Connect(int w, int h)
        {
            if (host.Handle == IntPtr.Zero) return;
            if (swaps.TryGetValue(host, out var id)) engine.Resize(id, w, h);
            else swaps[host] = engine.Attach(host.Handle, w, h);
            dirty = true;
        }
        host.Resized += Connect;
        host.Loaded += (_, _) => Connect(Math.Max(1, (int)host.ActualWidth), Math.Max(1, (int)host.ActualHeight));
        host.Unloaded += (_, _) => { if (swaps.Remove(host, out var id)) engine.Detach(id); };
    }

    public void Invalidate() => dirty = true;

    /// <summary>Draws when something changed; during playback that is every frame.</summary>
    public void Render()
    {
        if (swaps.Count == 0) return;
        bool source = session.ShowingSource && session.Source is not null;
        long t = source ? session.SourcePosition : session.Playhead;
        if (!dirty && t == lastDrawn && session.Doc.Revision == lastRevision && source == lastSource) return;
        dirty = false;
        lastDrawn = t;
        lastSource = source;
        if (session.Doc.Revision != lastRevision) composer.Prepare(session.P, session.Doc.Revision);
        lastRevision = session.Doc.Revision;
        try
        {
            if (source) composer.Draw(SourceProject(session.Source!), t);
            else composer.Draw(session.P, t);
            foreach (var id in swaps.Values) engine.Present(id);
        }
        catch (EngineException ex) { session.Say(ex.Message); }
    }

    Project SourceProject(MediaRef m)
    {
        if (sourceProject is null || sourceId != m.Id)
        {
            sourceProject = Project.CreateDefault("source");
            sourceProject.Media.Add(m);
            sourceProject.Track(TrackRole.Footage).Clips.Add(new Clip { MediaId = m.Id, Start = 0, Duration = Math.Max(m.Duration, Time.Frame) });
            sourceId = m.Id;
            composer.Prepare(sourceProject, 0);
        }
        return sourceProject;
    }

    public void Dispose()
    {
        composer.Dispose();
        engine.Dispose();
    }
}
