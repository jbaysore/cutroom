using System.Windows;
using Cutroom.Core;

namespace Cutroom.Engine;

public static class Ease
{
    public static double Apply(Easing e, double x)
    {
        x = Math.Clamp(x, 0, 1);
        return e switch
        {
            Easing.Linear => x,
            Easing.Snappy => 1 - Math.Pow(1 - x, 4),
            Easing.Bouncy => 1 + 2.2 * Math.Pow(x - 1, 3) + 1.2 * Math.Pow(x - 1, 2),
            _ => x * x * (3 - 2 * x),
        };
    }
}

/// <summary>
/// Turns a project at a timeline instant into native draw calls. The same composer drives the preview and the export,
/// so they always match. Media opens lazily; preview passes proxy paths, export passes originals.
/// </summary>
public sealed class Composer : IDisposable
{
    readonly NativeEngine engine;
    readonly Func<MediaRef, string> pathFor;
    readonly int maxDecodeHeight;
    readonly Dictionary<string, (int Handle, string Path)> media = [];
    readonly Dictionary<string, (int Id, int W, int H, long Used)> textures = [];
    int nextTexture = 1;
    long frameCounter;

    Project? prepared;
    int preparedRevision = -1;
    List<CaptionTiming> captions = [];

    public AppSettings Settings { get; set; }
    /// <summary>Called when a media file cannot be opened (missing/offline); the frame shows a placeholder instead.</summary>
    public Action<MediaRef, string>? MediaError { get; set; }
    readonly HashSet<string> failed = [];

    public Composer(NativeEngine engine, AppSettings settings, Func<MediaRef, string>? pathFor = null, int maxDecodeHeight = 1080)
    {
        this.engine = engine;
        Settings = settings;
        this.pathFor = pathFor ?? (m => m.Path);
        this.maxDecodeHeight = maxDecodeHeight;
    }

    int W => engine.Width;
    int H => engine.Height;

    /// <summary>Caches data derived from the project (caption timings). Call when the document revision changes.</summary>
    public void Prepare(Project p, int revision)
    {
        if (ReferenceEquals(p, prepared) && revision == preparedRevision) return;
        prepared = p;
        preparedRevision = revision;
        captions = Subtitles.Timings(p);
    }

    public void Draw(Project p, long t)
    {
        if (!ReferenceEquals(p, prepared)) Prepare(p, -1);
        frameCounter++;
        engine.Begin(0, 0, 0);
        DrawTrack(p, p.Track(TrackRole.Footage), t, cardsAllowed: true);
        DrawCards(p, t);
        DrawTrack(p, p.Track(TrackRole.Facecam), t, cardsAllowed: false);
        DrawSubtitles(p, t);
        TrimTextures();
    }

    // ---------- Media ----------

    int? Handle(MediaRef m)
    {
        var path = pathFor(m);
        if (media.TryGetValue(m.Id, out var h) && h.Path == path) return h.Handle;
        if (failed.Contains(path)) return null;
        try
        {
            if (h.Handle > 0) engine.Close(h.Handle);
            int id = engine.Open(path, maxDecodeHeight);
            media[m.Id] = (id, path);
            return id;
        }
        catch (EngineException ex)
        {
            failed.Add(path);
            MediaError?.Invoke(m, ex.Message);
            return null;
        }
    }

    (int Id, int W, int H)? Texture(string key, Func<Bitmap?> make)
    {
        if (textures.TryGetValue(key, out var t))
        {
            textures[key] = t with { Used = frameCounter };
            return (t.Id, t.W, t.H);
        }
        var bmp = make();
        if (bmp is null) return null;
        int id = nextTexture++;
        engine.Upload(id, bmp.Width, bmp.Height, bmp.Pixels, bmp.Stride);
        textures[key] = (id, bmp.Width, bmp.Height, frameCounter);
        return (id, bmp.Width, bmp.Height);
    }

    void TrimTextures()
    {
        if (textures.Count < 400) return;
        foreach (var old in textures.OrderBy(kv => kv.Value.Used).Take(textures.Count - 300).ToList())
        {
            engine.ReleaseTexture(old.Value.Id);
            textures.Remove(old.Key);
        }
    }

    (int Id, int W, int H)? ImageTexture(MediaRef m) => Texture("img:" + m.Id + ":" + pathFor(m), () =>
    {
        try { return TextRaster.Image(pathFor(m)); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            if (failed.Add(pathFor(m))) MediaError?.Invoke(m, ex.Message);
            return null;
        }
    });

    /// <summary>Draws a media source (video frame or image) into a destination box.</summary>
    void DrawSource(MediaRef m, double sourceSeconds, Layer box)
    {
        if (m.Kind == MediaKind.Image)
        {
            var tex = ImageTexture(m);
            if (tex is null) { Placeholder(box); return; }
            box.Kind = LayerKind.Texture;
            box.Texture = tex.Value.Id;
            engine.Draw(box);
            return;
        }
        if (m.Kind != MediaKind.Video) return;
        var handle = Handle(m);
        if (handle is null) { Placeholder(box); return; }
        box.Kind = LayerKind.Video;
        box.Media = handle.Value;
        box.SourceSeconds = Math.Max(0, sourceSeconds);
        engine.Draw(box);
    }

    void Placeholder(Layer box)
    {
        box.Kind = LayerKind.Color;
        (box.R, box.G, box.B, box.A) = (.35f, .12f, .14f, 1);
        engine.Draw(box);
    }

    // ---------- Video tracks ----------

    void DrawTrack(Project p, Track track, long t, bool cardsAllowed)
    {
        if (track.Muted) return;
        var clip = track.At(t);
        if (clip is null) return;
        if (clip.TransitionIn is { } tr && t < clip.Start + tr.Duration)
        {
            var prev = track.Clips.FirstOrDefault(c => c != clip && Math.Abs(c.End - clip.Start) <= Time.Frame);
            double k = Math.Clamp((t - clip.Start) / (double)tr.Duration, 0, 1);
            switch (tr.Kind)
            {
                case TransitionKind.CrossDissolve:
                    if (prev is not null) DrawClip(p, prev, t, 1);
                    DrawClip(p, clip, t, (float)Ease.Apply(Easing.Smooth, k));
                    break;
                case TransitionKind.DipToBlack:
                    if (k < .5) { if (prev is not null) DrawClip(p, prev, t, (float)(1 - k * 2)); }
                    else DrawClip(p, clip, t, (float)((k - .5) * 2));
                    break;
                default:
                    if (prev is not null) DrawClip(p, prev, t, 1);
                    int dir = tr.Kind switch { TransitionKind.SweepLeft => 1, TransitionKind.SweepRight => 2, TransitionKind.SweepUp => 3, _ => 4 };
                    DrawClip(p, clip, t, 1, dir, (float)Ease.Apply(Easing.Snappy, k));
                    break;
            }
            return;
        }
        DrawClip(p, clip, t, 1);
    }

    /// <summary>Source time for a clip, accounting for freeze-frame effects that hold and then resume.</summary>
    static long SourceTime(Clip clip, long t)
    {
        long local = t - clip.Start;
        long held = 0;
        foreach (var e in clip.Effects.Where(e => e.Kind == EffectKind.FreezeOutline).OrderBy(e => e.Offset))
        {
            if (local >= e.Offset && local < e.Offset + e.Duration) return clip.SourceIn + e.Offset - held;
            if (local >= e.Offset + e.Duration) held += e.Duration;
        }
        return clip.SourceIn + local - held;
    }

    void DrawClip(Project p, Clip clip, long t, float opacity, int wipeDir = 0, float wipe = 0)
    {
        var m = p.FindMedia(clip.MediaId);
        if (m is null) return;
        long local = t - clip.Start;
        var tr = clip.Transform;
        var box = Layer.Full(LayerKind.Video);
        box.Fit = 1;
        box.Cx = (float)tr.X; box.Cy = (float)tr.Y;
        box.W = (float)tr.Scale; box.H = (float)tr.Scale;
        box.U0 = (float)tr.CropLeft; box.V0 = (float)tr.CropTop; box.U1 = (float)(1 - tr.CropRight); box.V1 = (float)(1 - tr.CropBottom);
        box.CornerRadius = (float)tr.CornerRadius;
        box.Opacity = (float)(tr.Opacity * opacity);
        box.WipeDir = wipeDir;
        box.Wipe = wipe;

        double flash = 0;
        Effect? outline = null;
        foreach (var e in clip.Effects)
        {
            long into = local - e.Offset;
            if (into < 0 || into >= e.Duration) continue;
            ApplyEffect(e, into, ref box, ref flash);
            if (e.Kind == EffectKind.FreezeOutline) outline = e;
        }
        if (clip.Transform.CornerRadius > 0 && !clip.Transform.IsDefault)
        {
            var shadow = box;
            shadow.Kind = LayerKind.Color;
            (shadow.R, shadow.G, shadow.B, shadow.A) = (0, 0, 0, .45f);
            shadow.Softness = 18;
            shadow.Cy += 6f / H;
            shadow.Fit = 0;
            ApplyAspect(ref shadow, m);
            engine.Draw(shadow);
        }
        DrawSource(m, Time.ToSeconds(SourceTime(clip, t)), box);
        if (outline is not null) DrawOutline(outline, t - clip.Start - outline.Offset);
        if (flash > 0)
        {
            var white = Layer.Full(LayerKind.Color);
            (white.R, white.G, white.B, white.A) = (1, 1, 1, (float)flash);
            engine.Draw(white);
        }
    }

    /// <summary>Shrinks a box to the media's aspect (used for shadows under fitted media).</summary>
    void ApplyAspect(ref Layer box, MediaRef m)
    {
        if (m.Width <= 0 || m.Height <= 0) return;
        double src = m.Width / (double)m.Height, dst = box.W * W / (box.H * H);
        if (src > dst) box.H = (float)(box.W * W / src / H); else box.W = (float)(box.H * H * src / W);
    }

    static void ApplyEffect(Effect e, long into, ref Layer box, ref double flash)
    {
        double ramp = Math.Max(1, e.Ramp);
        double kIn = Ease.Apply(e.Easing, into / ramp);
        double kOut = Ease.Apply(e.Easing, (e.Duration - into) / ramp);
        double k = Math.Min(kIn, kOut);
        switch (e.Kind)
        {
            case EffectKind.ZoomToBox:
            {
                double size = Math.Max(.05, Math.Max(e.BoxW, e.BoxH));
                double s = 1 + (1 / size - 1) * k;
                double bx = .5 + (e.BoxX + e.BoxW / 2 - .5) * k, by = .5 + (e.BoxY + e.BoxH / 2 - .5) * k;
                box.Cx = (float)(.5 + (box.Cx - bx) * s);
                box.Cy = (float)(.5 + (box.Cy - by) * s);
                box.W *= (float)s; box.H *= (float)s;
                break;
            }
            case EffectKind.Punch:
            {
                double s = 1 + .12 * e.Amount * k;
                box.W *= (float)s; box.H *= (float)s;
                break;
            }
            case EffectKind.Shake:
            {
                double decay = 1 - into / (double)e.Duration;
                double secs = into / (double)Time.Rate;
                double amp = .012 * e.Amount * decay;
                box.Cx += (float)(amp * Math.Sin(secs * 71) * Math.Cos(secs * 13));
                box.Cy += (float)(amp * Math.Sin(secs * 53 + 1.3));
                box.W *= 1.03f; box.H *= 1.03f;
                break;
            }
            case EffectKind.Flash:
                flash = Math.Max(flash, .85 * (1 - Math.Abs(into / (double)Math.Max(1, e.Duration) * 2 - 1)));
                break;
            case EffectKind.PopIn:
            {
                double s = .82 + .18 * Ease.Apply(Easing.Bouncy, into / ramp);
                box.W *= (float)s; box.H *= (float)s;
                box.Opacity *= (float)Math.Clamp(into / ramp * 2, 0, 1);
                break;
            }
            case EffectKind.SlideLeft or EffectKind.SlideRight or EffectKind.SlideUp or EffectKind.SlideDown:
            {
                double off = 1 - Ease.Apply(e.Easing, into / ramp);
                if (e.Kind == EffectKind.SlideLeft) box.Cx += (float)off;
                if (e.Kind == EffectKind.SlideRight) box.Cx -= (float)off;
                if (e.Kind == EffectKind.SlideUp) box.Cy += (float)off;
                if (e.Kind == EffectKind.SlideDown) box.Cy -= (float)off;
                break;
            }
        }
    }

    void DrawOutline(Effect e, long into)
    {
        int pw = Math.Max(8, (int)(e.BoxW * W)), ph = Math.Max(8, (int)(e.BoxH * H));
        double progress = Math.Clamp(into / (double)Time.FromMs(350), 0, 1);
        int step = (int)Math.Round(progress * 12);
        double thickness = 7;
        var tex = Texture($"shape:{e.Shape}:{pw}x{ph}:{step}:{e.X1:0.###},{e.Y1:0.###},{e.X2:0.###},{e.Y2:0.###}",
            () => TextRaster.Shape(e.Shape, pw, ph, step / 12.0, Settings.CardTemplates.FirstOrDefault()?.Accent ?? "#FFD43B", thickness, e.X1, e.Y1, e.X2, e.Y2));
        if (tex is null) return;
        var l = Layer.Full(LayerKind.Texture);
        l.Texture = tex.Value.Id;
        l.Cx = (float)(e.BoxX + e.BoxW / 2); l.Cy = (float)(e.BoxY + e.BoxH / 2);
        l.W = tex.Value.W / (float)W; l.H = tex.Value.H / (float)H;
        engine.Draw(l);
    }

    // ---------- Cards ----------

    void DrawCards(Project p, long t)
    {
        var track = p.Track(TrackRole.Cards);
        if (track.Muted) return;
        var clip = track.At(t);
        if (clip?.Card is not { } card) return;
        var template = Settings.Template(card.TemplateId, card.Template);
        long local = t - clip.Start;
        double fadeIn = Math.Clamp(local / (double)Time.FromMs(220), 0, 1);
        double fadeOut = Math.Clamp((clip.End - t) / (double)Time.FromMs(220), 0, 1);
        float alpha = (float)Ease.Apply(Easing.Smooth, Math.Min(fadeIn, fadeOut));
        float seconds = (float)Time.ToSeconds(t);

        bool dark = false;
        switch (card.Template)
        {
            case CardTemplateKind.Blur:
            case CardTemplateKind.TitleCard:
            {
                var blur = Layer.Full(LayerKind.Blur);
                blur.Blur = (float)(card.Template == CardTemplateKind.TitleCard ? Math.Max(card.Blur, .9) : card.Blur);
                blur.Dim = (float)(card.Template == CardTemplateKind.TitleCard ? Math.Max(card.Dim, .5) : card.Dim);
                blur.Opacity = alpha;
                engine.Draw(blur);
                break;
            }
            default:
            {
                var bg = p.FindMedia(card.BackgroundMediaId);
                var bgPath = template.BackgroundPath;
                if (bg is null && bgPath is not null && File.Exists(bgPath))
                    bg = new MediaRef { Id = "bg:" + bgPath, Path = bgPath, Kind = IsImage(bgPath) ? MediaKind.Image : MediaKind.Video };
                if (bg is null)
                {
                    var paper = Layer.Full(LayerKind.Paper);
                    paper.Time = seconds;
                    paper.Opacity = alpha;
                    (paper.R, paper.G, paper.B) = (.5f, .5f, .5f);
                    engine.Draw(paper);
                }
                else
                {
                    var box = Layer.Full(LayerKind.Video);
                    box.Fit = 2;
                    box.Opacity = alpha;
                    double loop = bg.Duration > 0 ? Time.ToSeconds(local % bg.Duration) : Time.ToSeconds(local);
                    DrawSource(bg, loop, box);
                }
                dark = true;
                break;
            }
        }

        double pop = template.AnimationIn == "Pop" ? .9 + .1 * Ease.Apply(Easing.Bouncy, local / (double)Time.FromMs(300)) : 1;
        var layout = CardLayout.Compute(card, template, W, H);
        string ink = dark ? "#1D2433" : "#FFFFFF";
        double shadow = dark ? 0 : .45;

        if (card.Template == CardTemplateKind.EndScreen) DrawEndScreenZones(alpha);

        if (layout.Heading is { } heading)
        {
            var tex = Texture($"txt:{heading.Text}|{template.HeadingFont}|{heading.Size}|{ink}|{heading.Width}|{heading.Align}",
                () => TextRaster.Block(heading.Text, template.HeadingFont, heading.Size, ink, heading.Width, heading.Align, shadow));
            if (tex is not null) DrawTex(tex.Value, heading.Cx, heading.Cy, alpha, pop);
            if (card.Template is CardTemplateKind.SectionHeader or CardTemplateKind.TitleCard)
            {
                var bar = Layer.Full(LayerKind.Color);
                var accent = TextRaster.ParseColor(template.Accent);
                (bar.R, bar.G, bar.B, bar.A) = (accent.R / 255f, accent.G / 255f, accent.B / 255f, 1);
                double grow = Ease.Apply(Easing.Snappy, (local - Time.FromMs(150)) / (double)Time.FromMs(350));
                bar.W = (float)(.12 * grow); bar.H = 10f / H;
                bar.Cx = .5f; bar.Cy = (float)(heading.Cy + heading.Height / 2 / H + 22.0 / H);
                bar.CornerRadius = 5;
                bar.Opacity = alpha;
                if (grow > 0) engine.Draw(bar);
            }
        }

        if (card.Content != CardContent.Text && p.FindMedia(card.ContentMediaId) is { } content)
        {
            var box = Layer.Full(LayerKind.Video);
            box.Fit = 1;
            box.Cx = .5f; box.Cy = (float)layout.ContentCy;
            box.W = .7f; box.H = (float)layout.ContentHeight;
            box.CornerRadius = 18;
            box.Opacity = alpha;
            box.W *= (float)pop; box.H *= (float)pop;
            var shadowBox = box;
            shadowBox.Kind = LayerKind.Color;
            (shadowBox.R, shadowBox.G, shadowBox.B, shadowBox.A) = (0, 0, 0, .5f);
            shadowBox.Softness = 22;
            shadowBox.Cy += 8f / H;
            ApplyAspect(ref shadowBox, content);
            engine.Draw(shadowBox);
            DrawSource(content, Time.ToSeconds(card.ContentSourceIn + local), box);
        }

        for (int i = 0; i < layout.Lines.Count; i++)
        {
            var line = layout.Lines[i];
            double lineAlpha = alpha * Cards.LineOpacity(card, line.Index, local);
            if (lineAlpha <= 0) continue;
            double rise = (1 - Cards.LineOpacity(card, line.Index, local)) * 14.0 / H;
            var tex = Texture($"txt:{line.Text}|{template.BodyFont}|{line.Size}|{ink}|{line.Width}|{line.Align}",
                () => TextRaster.Block(line.Text, template.BodyFont, line.Size, ink, line.Width, line.Align, shadow, line.Size * 1.28));
            if (tex is not null) DrawTex(tex.Value, line.Cx, line.Cy + rise, (float)lineAlpha, 1);
        }
    }

    void DrawEndScreenZones(float alpha)
    {
        // YouTube end-screen elements sit in these zones; keep them clear and mark them subtly.
        foreach (var (cx, cy, w, h, radius) in new[] { (.27, .58, .34, .34, 16.0), (.73, .58, .34, .34, 16.0) })
        {
            var zone = Layer.Full(LayerKind.Color);
            (zone.R, zone.G, zone.B, zone.A) = (0, 0, 0, .14f);
            zone.Cx = (float)cx; zone.Cy = (float)cy; zone.W = (float)w; zone.H = (float)h;
            zone.CornerRadius = (float)radius;
            zone.Opacity = alpha;
            engine.Draw(zone);
        }
    }

    void DrawTex((int Id, int W, int H) tex, double cx, double cy, float alpha, double scale)
    {
        var l = Layer.Full(LayerKind.Texture);
        l.Texture = tex.Id;
        l.Cx = (float)cx; l.Cy = (float)cy;
        l.W = (float)(tex.W * scale / W); l.H = (float)(tex.H * scale / H);
        l.Opacity = alpha;
        engine.Draw(l);
    }

    static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";

    // ---------- Subtitles ----------

    void DrawSubtitles(Project p, long t)
    {
        if (p.Track(TrackRole.Subtitles).Muted) return;
        var visible = Subtitles.Visible(p, t, captions);
        if (visible is not { } v) return;
        var preset = Settings.Preset(v.Region.PresetId);
        var tex = Texture($"cap:{v.Caption.Caption.Text}|{preset.Id}|{preset.FontFamily}|{preset.Size}|{preset.Fill}|{preset.Outline}|{preset.OutlineWidth}|{preset.ShadowOpacity}",
            () => TextRaster.Caption(v.Caption.Caption.Text, preset));
        if (tex is null) return;
        double alpha = 1, scale = 1;
        long into = t - v.Caption.Start;
        if (preset.Animation == "Fade") alpha = Math.Clamp(into / (double)Time.FromMs(120), 0, 1);
        if (preset.Animation == "Pop") { scale = .9 + .1 * Ease.Apply(Easing.Bouncy, into / (double)Time.FromMs(180)); }
        DrawTex(tex.Value, .5, preset.Y, (float)alpha, scale);
    }

    public void Dispose()
    {
        foreach (var m in media.Values) engine.Close(m.Handle);
        foreach (var t in textures.Values) engine.ReleaseTexture(t.Id);
        media.Clear();
        textures.Clear();
    }
}

public sealed record CardTextItem(int Index, string Text, double Size, double Width, TextAlignment Align, double Cx, double Cy, double Height);

public sealed record CardLayoutResult(CardTextItem? Heading, List<CardTextItem> Lines, double ContentCy, double ContentHeight, double FontSize, bool Overflow);

/// <summary>
/// Automatic card typography: text box at most 70% of the frame, 30-45 characters per line, font size stepped
/// down from 64 to 40 px until everything fits in 60% of the frame height.
/// </summary>
public static class CardLayout
{
    public static CardLayoutResult Compute(CardData card, CardTemplate template, int w, int h)
    {
        bool headingCard = card.Template is CardTemplateKind.SectionHeader or CardTemplateKind.TitleCard or CardTemplateKind.EndScreen;
        string headingText = card.Heading.Length > 0 ? card.Heading : headingCard && card.Lines.Count > 0 ? card.Lines[0].Text : "";
        var lines = headingCard && card.Heading.Length == 0 ? card.Lines.Skip(1).ToList() : card.Lines;
        if (card.Template == CardTemplateKind.EndScreen && headingText.Length == 0) headingText = "Thanks for watching!";

        double maxBox = w * .7, maxHeight = h * .6;
        double headingSize = card.Template switch { CardTemplateKind.TitleCard => 110, CardTemplateKind.SectionHeader => 96, CardTemplateKind.EndScreen => 72, _ => 76 };
        bool hasContent = card.Content != CardContent.Text;

        foreach (var size in new double[] { 64, 60, 56, 52, 48, 44, 40 })
        {
            double charWidth = TextRaster.MeasureWidth("abcdefghijklmnopqrstuvwxyz ABCDEFGHIJ", template.BodyFont, size) / 37.0;
            double width = Math.Min(maxBox, Math.Max(charWidth * 30, charWidth * 45));
            double headingHeight = headingText.Length > 0 ? TextRaster.Measure(headingText, template.HeadingFont, headingSize, maxBox) : 0;
            double gap = size * .45;
            var heights = lines.Select(l => TextRaster.Measure(l.Text, template.BodyFont, size, width, size * 1.28)).ToList();
            double contentHeight = hasContent ? h * .42 : 0;
            double total = headingHeight + (headingHeight > 0 && (heights.Count > 0 || hasContent) ? gap * 1.6 : 0)
                           + heights.Sum() + gap * Math.Max(0, heights.Count - 1) + contentHeight + (hasContent && heights.Count > 0 ? gap : 0);
            if (total <= maxHeight || size == 40)
            {
                bool singleShort = lines.Count == 1 && heights.Count == 1 && heights[0] < size * 1.6;
                var align = singleShort || headingCard ? TextAlignment.Center : TextAlignment.Left;
                double y = card.Template == CardTemplateKind.EndScreen ? h * .12 : (h - total) / 2;
                CardTextItem? heading = null;
                if (headingHeight > 0)
                {
                    heading = new CardTextItem(-1, headingText, headingSize, maxBox, TextAlignment.Center, .5, (y + headingHeight / 2) / h, headingHeight);
                    y += headingHeight + (heights.Count > 0 || hasContent ? gap * 1.6 : 0);
                }
                double contentCy = 0;
                if (hasContent)
                {
                    contentCy = (y + contentHeight / 2) / h;
                    y += contentHeight + (heights.Count > 0 ? gap : 0);
                }
                var items = new List<CardTextItem>();
                for (int i = 0; i < lines.Count; i++)
                {
                    int index = card.Lines.IndexOf(lines[i]);
                    items.Add(new CardTextItem(index, lines[i].Text, size, width, align, .5, (y + heights[i] / 2) / h, heights[i]));
                    y += heights[i] + gap;
                }
                return new CardLayoutResult(heading, items, contentCy, contentHeight / h, size, total > maxHeight);
            }
        }
        throw new InvalidOperationException("unreachable");
    }
}
