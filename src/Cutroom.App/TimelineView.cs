using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.App;

/// <summary>
/// The timeline, drawn directly (fast at 90 minutes with thousands of narration cuts): ruler with section markers,
/// one row per track, clips with waveforms and duck curves, subtitle regions, in/out marks and the playhead.
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    const double Header = 112, RulerHeight = 26, EdgeGrab = 6;
    readonly Session s;
    double pxPerSecond = 40;
    double scrollSeconds;
    readonly Typeface face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    readonly Typeface bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    enum DragMode { None, Seek, Move, TrimStart, TrimEnd }
    DragMode drag;
    Clip? dragClip;
    Track? dragTrack;
    double dragStartX;
    long dragDelta;
    long dragOriginal;

    public TimelineView(Session session)
    {
        s = session;
        ClipToBounds = true;
        Focusable = true;
        AllowDrop = true;
        s.ProjectChanged += InvalidateVisual;
        s.PlayheadChanged += () => { FollowPlayhead(); InvalidateVisual(); };
        s.SelectionChanged += InvalidateVisual;
        s.DocumentReplaced += () => { scrollSeconds = 0; InvalidateVisual(); };
    }

    IReadOnlyList<Track> Rows => s.P.Tracks.OrderBy(t => t.Role switch
    {
        TrackRole.Facecam => 0, TrackRole.Cards => 1, TrackRole.Footage => 2, TrackRole.Subtitles => 3, TrackRole.Narration => 4,
        TrackRole.Game => 5, TrackRole.Music => 6, _ => 7,
    }).ToList();

    double RowHeight => Math.Max(22, (ActualHeight - RulerHeight) / Math.Max(1, Rows.Count));
    double X(long t) => Header + (Time.ToSeconds(t) - scrollSeconds) * pxPerSecond;
    long T(double x) => Time.FromSeconds(Math.Max(0, (x - Header) / pxPerSecond + scrollSeconds));
    double DpiScale => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    public void Zoom(double factor, double? aroundX = null)
    {
        double ax = aroundX ?? X(s.Playhead);
        double at = (ax - Header) / pxPerSecond + scrollSeconds;
        pxPerSecond = Math.Clamp(pxPerSecond * factor, .5, 1200);
        scrollSeconds = Math.Max(0, at - (ax - Header) / pxPerSecond);
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        double seconds = Math.Max(10, Time.ToSeconds(s.P.Duration));
        pxPerSecond = Math.Max(.5, (ActualWidth - Header - 20) / seconds);
        scrollSeconds = 0;
        InvalidateVisual();
    }

    void FollowPlayhead()
    {
        double x = X(s.Playhead);
        double width = ActualWidth - Header;
        if (width <= 0) return;
        if (x > ActualWidth - 40 || x < Header) scrollSeconds = Math.Max(0, Time.ToSeconds(s.Playhead) - (s.Playing ? .1 : .5) * width / pxPerSecond);
    }

    FormattedText Label(string text, double size, Brush brush, bool strong = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, strong ? bold : face, size, brush, DpiScale);

    protected override void OnRender(DrawingContext dc)
    {
        var p = s.P;
        dc.DrawRectangle(Theme.PanelBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double rowH = RowHeight;
        long visibleStart = T(Header), visibleEnd = T(ActualWidth);

        // Ruler.
        dc.DrawRectangle(Theme.Panel2Brush, null, new Rect(0, 0, ActualWidth, RulerHeight));
        double step = new[] { .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 }.First(v => v * pxPerSecond >= 70);
        for (double sec = Math.Floor(scrollSeconds / step) * step; ; sec += step)
        {
            double x = Header + (sec - scrollSeconds) * pxPerSecond;
            if (x > ActualWidth) break;
            if (x < Header) continue;
            dc.DrawLine(new Pen(Theme.MutedBrush, 1), new Point(x, RulerHeight - 6), new Point(x, RulerHeight));
            dc.DrawText(Label(Time.Chapter(Time.FromSeconds(sec)), 10, Theme.MutedBrush), new Point(x + 3, 6));
        }
        foreach (var m in p.Markers)
        {
            double x = X(m.Time);
            if (x < Header || x > ActualWidth) continue;
            var text = Label("§ " + m.Label, 10, Brushes.Black, true);
            dc.DrawRoundedRectangle(Theme.B(Theme.Sub), null, new Rect(x, 0, text.Width + 10, RulerHeight - 4), 4, 4);
            dc.DrawText(text, new Point(x + 5, 5));
        }

        // Rows.
        var envelope = DuckEnvelope.For(p);
        var rows = Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var track = rows[i];
            double y = RulerHeight + i * rowH;
            dc.DrawLine(new Pen(Theme.B(Colors.White, 10), 1), new Point(0, y + rowH), new Point(ActualWidth, y + rowH));
            string id = track.Kind switch { TrackKind.Video => "V", TrackKind.Subtitle => "S", _ => "A" };
            var color = Theme.RoleColor(track.Role);
            dc.DrawRectangle(Theme.B(color, (byte)(s.SelectedTrackId == track.Id ? 40 : 0)), null, new Rect(0, y, Header, rowH));
            dc.DrawText(Label(track.Name, 12, track.Muted ? Theme.MutedBrush : Theme.TextBrush), new Point(14, y + rowH / 2 - 8));
            if (track.Kind == TrackKind.Audio || track.Kind == TrackKind.Video)
            {
                dc.DrawText(Label("M", 10, track.Muted ? Theme.DangerBrush : Theme.MutedBrush, track.Muted), new Point(Header - 34, y + rowH / 2 - 7));
                if (track.Kind == TrackKind.Audio) dc.DrawText(Label("S", 10, track.Solo ? Theme.B(Theme.Sub) : Theme.MutedBrush, track.Solo), new Point(Header - 18, y + rowH / 2 - 7));
            }
            dc.PushClip(new RectangleGeometry(new Rect(Header, y, Math.Max(0, ActualWidth - Header), rowH)));
            if (track.Role == TrackRole.Subtitles) DrawSubtitles(dc, p, y, rowH);
            foreach (var clip in track.Clips)
            {
                if (clip.End < visibleStart || clip.Start > visibleEnd) continue;
                DrawClip(dc, p, track, clip, y, rowH, color, envelope);
            }
            dc.Pop();
        }

        // Marks and playhead.
        if (s.MarkIn is { } mi)
        {
            double a = X(mi), b = s.MarkOut is { } mo ? X(mo) : a + 2;
            dc.DrawRectangle(Theme.B(Theme.Accent, 30), null, new Rect(Math.Max(Header, a), RulerHeight, Math.Max(2, b - Math.Max(Header, a)), ActualHeight - RulerHeight));
        }
        double px = X(s.Playhead);
        if (px >= Header)
        {
            dc.DrawLine(new Pen(Brushes.White, 1.5), new Point(px, 0), new Point(px, ActualHeight));
            var tri = new StreamGeometry();
            using (var g = tri.Open()) { g.BeginFigure(new Point(px - 6, 0), true, true); g.LineTo(new Point(px + 6, 0), true, false); g.LineTo(new Point(px, 9), true, false); }
            dc.DrawGeometry(Brushes.White, null, tri);
        }
        dc.DrawText(Label(Time.Timecode(s.Playhead), 11, Theme.TextBrush), new Point(10, 6));
    }

    void DrawSubtitles(DrawingContext dc, Project p, double y, double rowH)
    {
        var dash = new Pen(Theme.B(Theme.Sub), 1) { DashStyle = DashStyles.Dash };
        foreach (var r in p.SubtitleRegions)
        {
            var rect = new Rect(X(r.Start), y + 3, Math.Max(2, X(r.End) - X(r.Start)), rowH - 6);
            dc.DrawRoundedRectangle(Theme.B(Theme.Sub, (byte)(s.SelectedRegionId == r.Id ? 70 : 28)), dash, rect, 6, 6);
            var preset = s.Settings.Preset(r.PresetId);
            dc.DrawText(Label($"Subtitles · {r.Label} · {preset.Name}", 10, Theme.B(Theme.Sub)), new Point(rect.X + 6, y + 6));
        }
        if (pxPerSecond >= 25)
        {
            foreach (var c in Subtitles.Timings(p))
            {
                double a = X(c.Start), b = X(c.End);
                if (b < Header || a > ActualWidth) continue;
                dc.DrawRectangle(Theme.B(Theme.Sub, 60), null, new Rect(a, y + rowH - 9, Math.Max(1, b - a - 1), 5));
            }
        }
    }

    void DrawClip(DrawingContext dc, Project p, Track track, Clip clip, double y, double rowH, Color color, DuckEnvelope envelope)
    {
        double a = X(clip.Start), b = X(clip.End);
        if (drag is DragMode.Move or DragMode.TrimStart or DragMode.TrimEnd && dragClip is not null && TimelineOps.Linked(p, dragClip).Contains(clip))
        {
            if (drag == DragMode.Move) { a = X(clip.Start + dragDelta); b = X(clip.End + dragDelta); }
            if (drag == DragMode.TrimStart && clip == dragClip) a = X(clip.Start + dragDelta);
            if (drag == DragMode.TrimEnd && clip == dragClip) b = X(clip.End + dragDelta);
        }
        var c = clip.Kind == ClipKind.Card ? Theme.Card : color;
        var rect = new Rect(a + .5, y + 4, Math.Max(1.5, b - a - 1), rowH - 8);
        bool selected = clip.Id == s.SelectedClipId;
        dc.DrawRoundedRectangle(Theme.B(c, (byte)(track.Muted ? 90 : 210)), selected ? new Pen(Brushes.White, 2) : null, rect, 6, 6);
        var media = p.FindMedia(clip.MediaId);
        if (track.Kind == TrackKind.Audio && media is not null && rect.Width > 6) DrawWave(dc, media, clip, rect);
        if (track.DuckEnabled && rect.Width > 8)
        {
            var pen = new Pen(Theme.B(Colors.White, 160), 1.2);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool first = true;
                for (double x = Math.Max(rect.Left, Header); x <= Math.Min(rect.Right, ActualWidth); x += 3)
                {
                    double gain = envelope.Amount(T(x), Time.FromMs(track.DuckAttackMs), Time.FromMs(track.DuckReleaseMs));
                    var pt = new Point(x, rect.Top + 3 + gain * (rect.Height - 6) * .6);
                    if (first) { g.BeginFigure(pt, false, false); first = false; } else g.LineTo(pt, true, false);
                }
            }
            dc.DrawGeometry(null, pen, geo);
        }
        if (clip.TransitionIn is not null)
        {
            var tri = new StreamGeometry();
            using (var g = tri.Open()) { g.BeginFigure(new Point(rect.Left, rect.Top), true, true); g.LineTo(new Point(rect.Left + 12, rect.Top), true, false); g.LineTo(new Point(rect.Left, rect.Bottom), true, false); }
            dc.DrawGeometry(Theme.B(Colors.White, 170), null, tri);
        }
        foreach (var e in clip.Effects)
        {
            double ex = X(clip.Start + e.Offset), ew = Math.Max(4, X(clip.Start + e.Offset + e.Duration) - ex);
            dc.DrawRoundedRectangle(Theme.B(Colors.White, 70), null, new Rect(ex, rect.Bottom - 6, ew, 4), 2, 2);
        }
        if (rect.Width > 30)
        {
            string label = clip.Card is { } card ? $"{card.Template switch { CardTemplateKind.Blur => "Blur card", CardTemplateKind.Background => "Background card", CardTemplateKind.SectionHeader => "§", CardTemplateKind.TitleCard => "Title", _ => "End screen" }} · {(card.Heading.Length > 0 ? card.Heading : card.Lines.FirstOrDefault()?.Text ?? "")}"
                : clip.Label.Length > 0 ? clip.Label : media?.Name ?? "";
            if (clip.GainDb != 0) label += $"  {clip.GainDb:+0.#;-0.#} dB";
            dc.PushClip(new RectangleGeometry(rect));
            var text = Label(label, 10.5, Brushes.White);
            dc.DrawText(text, new Point(Math.Max(rect.X, Header) + 6, rect.Y + 3));
            dc.Pop();
        }
    }

    void DrawWave(DrawingContext dc, MediaRef media, Clip clip, Rect rect)
    {
        var peaks = s.PeaksFor(media);
        if (peaks.Length == 0) return;
        var geo = new StreamGeometry();
        double mid = rect.Top + rect.Height / 2, half = rect.Height / 2 - 2;
        using (var g = geo.Open())
        {
            for (double x = Math.Max(rect.Left, Header); x < Math.Min(rect.Right, ActualWidth); x += 2)
            {
                long src = clip.SourceAt(T(x));
                int i0 = (int)(src / Derivatives.PeakBlock), i1 = Math.Max(i0 + 1, (int)(clip.SourceAt(T(x + 2)) / Derivatives.PeakBlock));
                byte max = 0;
                for (int i = Math.Max(0, i0); i < Math.Min(peaks.Length, i1); i++) max = Math.Max(max, peaks[i]);
                double h = max / 255.0 * half * Gain.Fade(clip, T(x));
                if (h < .5) continue;
                g.BeginFigure(new Point(x, mid - h), false, false);
                g.LineTo(new Point(x, mid + h), true, false);
            }
        }
        dc.DrawGeometry(null, new Pen(Theme.B(Colors.White, 120), 1.2), geo);
    }

    // ---------- Interaction ----------

    (Track? Track, Clip? Clip, bool StartEdge, bool EndEdge) HitTest(Point pt)
    {
        if (pt.Y < RulerHeight) return (null, null, false, false);
        int row = (int)((pt.Y - RulerHeight) / RowHeight);
        var rows = Rows;
        if (row < 0 || row >= rows.Count) return (null, null, false, false);
        var track = rows[row];
        long t = T(pt.X);
        var clip = track.Clips.FirstOrDefault(c => c.TimeRange.Contains(t)) ??
                   track.Clips.FirstOrDefault(c => Math.Abs(X(c.Start) - pt.X) < EdgeGrab || Math.Abs(X(c.End) - pt.X) < EdgeGrab);
        if (clip is null) return (track, null, false, false);
        return (track, clip, Math.Abs(X(clip.Start) - pt.X) < EdgeGrab, Math.Abs(X(clip.End) - pt.X) < EdgeGrab);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var pt = e.GetPosition(this);
        if (pt.X < Header)
        {
            int row = (int)((pt.Y - RulerHeight) / RowHeight);
            if (row >= 0 && row < Rows.Count)
            {
                var track = Rows[row];
                if (pt.X > Header - 38 && pt.X < Header - 22) s.Edit(track.Muted ? "Unmute track" : "Mute track", p => p.Tracks.First(t => t.Id == track.Id).Muted ^= true);
                else if (pt.X >= Header - 22 && track.Kind == TrackKind.Audio) s.Edit("Solo track", p => p.Tracks.First(t => t.Id == track.Id).Solo ^= true);
                else { s.SelectedTrackId = track.Id; s.SelectedClipId = null; }
            }
            return;
        }
        var (trk, clip, startEdge, endEdge) = HitTest(pt);
        if (trk?.Role == TrackRole.Subtitles)
        {
            long t = T(pt.X);
            var region = s.P.SubtitleRegions.FirstOrDefault(r => t >= r.Start && t < r.End);
            s.SelectRegion(region?.Id);
            if (region is null) s.Seek(t);
            return;
        }
        if (clip is null || pt.Y < RulerHeight)
        {
            s.SelectedClipId = null;
            drag = DragMode.Seek;
            s.ShowingSource = false;
            s.Seek(Snap(T(pt.X)));
            CaptureMouse();
            return;
        }
        s.SelectedClipId = clip.Id;
        dragClip = clip;
        dragTrack = trk;
        dragStartX = pt.X;
        dragDelta = 0;
        dragOriginal = clip.Start;
        drag = startEdge ? DragMode.TrimStart : endEdge ? DragMode.TrimEnd : DragMode.Move;
        if (e.ClickCount == 2) { drag = DragMode.None; s.Seek(clip.Start); return; }
        CaptureMouse();
    }

    long Snap(long t) => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? t : TimelineOps.Snap(s.P, t, Time.FromSeconds(8 / pxPerSecond), s.Playhead, dragClip);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pt = e.GetPosition(this);
        if (drag == DragMode.None)
        {
            var hit = HitTest(pt);
            Cursor = hit.StartEdge || hit.EndEdge ? Cursors.SizeWE : hit.Clip is not null ? Cursors.Hand : Cursors.Arrow;
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) { drag = DragMode.None; return; }
        if (drag == DragMode.Seek) { s.Seek(Snap(T(pt.X))); return; }
        long raw = Time.FromSeconds((pt.X - dragStartX) / pxPerSecond);
        long edge = drag switch { DragMode.TrimEnd => dragClip!.End, _ => dragClip!.Start };
        dragDelta = Snap(edge + raw) - edge;
        if (drag == DragMode.Move) dragDelta = Math.Max(-dragOriginal, dragDelta);
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        var mode = drag;
        drag = DragMode.None;
        if (dragClip is null || dragDelta == 0 || mode is DragMode.None or DragMode.Seek) { dragClip = null; InvalidateVisual(); return; }
        var id = dragClip.Id;
        long delta = Time.SnapToFrame(dragDelta);
        s.Edit(mode == DragMode.Move ? "Move clip" : "Trim clip", p =>
        {
            var c = p.FindClip(id)!;
            switch (mode)
            {
                case DragMode.Move: TimelineOps.Move(p, c, c.Start + delta); c.SlotId = null; break;
                case DragMode.TrimStart: foreach (var l in TimelineOps.Linked(p, c)) TimelineOps.TrimStart(l, delta); break;
                case DragMode.TrimEnd: foreach (var l in TimelineOps.Linked(p, c)) TimelineOps.TrimEnd(l, delta, p.FindMedia(l.MediaId)); break;
            }
        });
        dragClip = null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Zoom(e.Delta > 0 ? 1.25 : .8, e.GetPosition(this).X);
        else { scrollSeconds = Math.Max(0, scrollSeconds - e.Delta / 120.0 * 80 / pxPerSecond); InvalidateVisual(); }
        e.Handled = true;
    }

    /// <summary>Files dragged from Explorer or the library land on the track under the cursor at that time.</summary>
    public event Action<string[], Track?, long>? FilesDropped;

    protected override void OnDrop(DragEventArgs e)
    {
        var pt = e.GetPosition(this);
        var (track, _, _, _) = HitTest(pt);
        string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? (e.Data.GetData(DataFormats.StringFormat) is string one ? [one] : null);
        if (files is { Length: > 0 }) FilesDropped?.Invoke(files, track, Time.SnapToFrame(T(pt.X)));
    }
}
