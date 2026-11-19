using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cutroom.AI;
using Cutroom.Core;

namespace Cutroom.App;

/// <summary>
/// The one place to change the selection: sliders, toggles, dropdowns and preset strips only.
/// No node graphs, no curve editors; motion is presets with a duration and an easing choice.
/// </summary>
public sealed class InspectorPanel : ScrollViewer
{
    readonly Session s;
    readonly EditCommands cmds;
    readonly StackPanel body = new() { Margin = new Thickness(2, 0, 6, 0) };
    string? builtFor;
    int builtRevision = -1;
    string? focusEffect;

    public InspectorPanel(Session session, EditCommands commands)
    {
        s = session;
        cmds = commands;
        Content = body;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        s.SelectionChanged += () => Rebuild(force: true);
        s.ProjectChanged += () => Rebuild(force: false);
        s.Inspector = this;
        Loaded += (_, _) => Rebuild(true);
    }

    public void FocusEffect(string id) { focusEffect = id; Rebuild(true); }

    /// <summary>Ctrl+G: draft text for the selected card.</summary>
    public void DraftSelected()
    {
        if (s.SelectedClip is { Card: not null } clip && s.Ai.Enabled) _ = DraftText(clip);
        else s.Say(s.Ai.Enabled ? "Select a card first." : "Install and sign in to Claude Code or Codex to use AI.");
    }

    void Rebuild(bool force)
    {
        string key = (s.SelectedClipId ?? "") + "|" + (s.SelectedRegionId ?? "") + "|" + (s.SelectedTrackId ?? "");
        if (!force && key == builtFor && (builtRevision == s.Doc.Revision || Keyboard.FocusedElement is TextBox)) return;
        builtFor = key;
        builtRevision = s.Doc.Revision;
        body.Children.Clear();
        if (s.SelectedClip is { } clip)
        {
            if (clip.Card is not null) CardSection(clip);
            else if (s.P.TrackOf(clip) is { Kind: TrackKind.Video } track) VideoSection(clip, track);
            else AudioSection(clip);
            return;
        }
        if (s.SelectedRegionId is { } rid && s.P.SubtitleRegions.FirstOrDefault(r => r.Id == rid) is { } region) { RegionSection(region); return; }
        ProjectSection();
    }

    void Add(UIElement e) => body.Children.Add(e);
    void Title(string text) { var t = Ui.Header(text); t.Margin = new Thickness(0, 6, 0, 10); Add(t); }
    void Note(string text) { var t = Ui.Text(text, 11.5, true); t.Margin = new Thickness(0, 0, 0, 10); Add(t); }

    void EditClip(string label, Action<Clip> change)
    {
        var id = s.SelectedClipId;
        if (id is null) return;
        s.Edit(label, p => { if (p.FindClip(id) is { } c) change(c); });
    }

    // ---------- Cards ----------

    void CardSection(Clip clip)
    {
        var card = clip.Card!;
        Title(card.Template switch
        {
            CardTemplateKind.Blur => "Blur card", CardTemplateKind.Background => "Background card", CardTemplateKind.SectionHeader => "Section header",
            CardTemplateKind.TitleCard => "Title card", _ => "End screen",
        });
        var templates = s.Settings.CardTemplates.Where(t => t.Kind == card.Template || card.Template is CardTemplateKind.Blur or CardTemplateKind.Background && t.Kind is CardTemplateKind.Blur or CardTemplateKind.Background).ToList();
        Add(Ui.Text("Template", 12, true));
        Add(Ui.Combo(templates, s.Settings.Template(card.TemplateId, card.Template), t => t.Name, t => EditClip("Card template", c =>
        {
            c.Card!.TemplateId = t.Id; c.Card.Template = t.Kind; c.Card.Blur = t.Blur; c.Card.Dim = t.Dim;
        })));
        if (card.Template is CardTemplateKind.SectionHeader or CardTemplateKind.TitleCard or CardTemplateKind.EndScreen || card.Heading.Length > 0)
        {
            Add(Ui.Text("Heading", 12, true));
            var heading = new TextBox { Text = card.Heading, Margin = new Thickness(0, 0, 0, 10) };
            heading.LostFocus += (_, _) => { if (heading.Text != card.Heading) EditClip("Card heading", c => { c.Card!.Heading = heading.Text; c.Label = heading.Text; }); };
            Add(heading);
        }
        if (card.Template is CardTemplateKind.Blur or CardTemplateKind.Background || card.Lines.Count > 0)
        {
            Add(Ui.Text("Text (one line per row; long lines wrap automatically)", 12, true));
            var text = new TextBox { Text = card.FullText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, Margin = new Thickness(0, 0, 0, 6) };
            text.LostFocus += (_, _) => { if (text.Text != card.FullText) EditClip("Card text", c => { Cards.SetText(c.Card!, text.Text); Cards.AutoTime(s.P, c); }); };
            Add(text);
            var ai = Ui.Button("AI: draft text", () => _ = DraftText(clip), "card.aiText", tip: "Draft 2–3 versions of this card's text from what you say here");
            ai.IsEnabled = s.Ai.Enabled;
            Add(Ui.Row(ai));
            Add(Ui.Row(
                Ui.Chip("Line by line", card.Reveal == RevealMode.LineByLine, on => { if (on) EditClip("Reveal", c => { c.Card!.Reveal = RevealMode.LineByLine; Cards.AutoTime(s.P, c); }); }),
                Ui.Chip("All at once", card.Reveal == RevealMode.AllAtOnce, on => { if (on) EditClip("Reveal", c => c.Card!.Reveal = RevealMode.AllAtOnce); })));
            if (card.Reveal == RevealMode.LineByLine)
                Note("Lines appear when you say them. To retime: play the card and press . (period) where each next line should appear.");
            if (card.Lines.Any(l => l.Manual))
                Add(Ui.Row(Ui.Button("Reset timing to narration", () => EditClip("Auto timing", c => { foreach (var l in c.Card!.Lines) l.Manual = false; Cards.AutoTime(s.P, c); }))));
        }
        if (card.Template is CardTemplateKind.Blur or CardTemplateKind.TitleCard)
        {
            Add(Ui.Slider("Blur", 0, 1, card.Blur, v => EditClip("Blur", c => c.Card!.Blur = v), "0%"));
            Add(Ui.Slider("Darken", 0, .9, card.Dim, v => EditClip("Darken", c => c.Card!.Dim = v), "0%"));
        }
        Add(Ui.Text("Content", 12, true));
        Add(Ui.Row(
            Ui.Chip("Text", card.Content == CardContent.Text, on => { if (on) EditClip("Card content", c => c.Card!.Content = CardContent.Text); }),
            Ui.Chip("Image / clip from source", card.Content != CardContent.Text, on => { if (on) cmds.SwapContent(); })));
        if (card.Template is CardTemplateKind.Background or CardTemplateKind.SectionHeader or CardTemplateKind.EndScreen)
            Note(card.BackgroundMediaId is null ? "Background: animated notebook page. Pick a Background asset in the Assets tab to replace it." : "Background: " + s.P.FindMedia(card.BackgroundMediaId)?.Name);
        if (card.BackgroundMediaId is not null) Add(Ui.Row(Ui.Button("Use notebook page", () => EditClip("Card background", c => c.Card!.BackgroundMediaId = null))));
        Add(Ui.Row(Ui.Button("Delete card", cmds.Delete)));
    }

    async Task DraftText(Clip clip)
    {
        var words = Derive.TimelineWords(s.P).Where(w => w.TimeRange.Start >= clip.Start && w.TimeRange.Start < clip.End).Select(w => w.Word).ToList();
        if (words.Count == 0) { s.Say("No narration under this card to draft from."); return; }
        var spoken = Derive.Join(words);
        var paragraph = words.FirstOrDefault(w => w.SentenceId is not null) is { } first && s.P.Script?.ParagraphOf(first.SentenceId!) is { } para
            ? string.Join(" ", para.Sentences.Select(x => x.Text)) : "";
        s.Say("Asking AI for card text…");
        try
        {
            var drafts = await s.Ai.DraftCardText(spoken, paragraph, s.P.Script?.Vocabulary() ?? [], CancellationToken.None);
            ShowDrafts(clip.Id, drafts);
        }
        catch (AiException ex) { s.Say(ex.Message); }
    }

    void ShowDrafts(string clipId, CardDrafts drafts)
    {
        var options = new[] { ("Condensed", drafts.Condensed), ("Key points", drafts.KeyPoints), ("Your words", drafts.ExactWords) };
        var window = new Window { Title = "Pick card text (1, 2 or 3)", Width = 980, SizeToContent = SizeToContent.Height, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Theme.Window(window);
        var grid = new UniformGridPanel(3);
        void Pick(int i)
        {
            var text = options[i].Item2.Replace("\\n", "\n");
            s.Edit("AI card text", p => { var c = p.FindClip(clipId)!; Cards.SetText(c.Card!, text); Cards.AutoTime(p, c); });
            window.Close();
        }
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var b = Ui.Button($"{i + 1}. {options[i].Item1}", () => Pick(index), primary: i == 0);
            grid.Add(Ui.Card(Ui.Stack(Ui.Header(options[i].Item1), new TextBlock { Text = options[i].Item2.Replace("\\n", "\n"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12), Foreground = Theme.TextBrush }, b)));
        }
        window.Content = new Border { Padding = new Thickness(14), Child = grid.Panel };
        window.KeyDown += (_, e) => { if (e.Key is Key.D1 or Key.NumPad1) Pick(0); if (e.Key is Key.D2 or Key.NumPad2) Pick(1); if (e.Key is Key.D3 or Key.NumPad3) Pick(2); if (e.Key == Key.Escape) window.Close(); };
        window.ShowDialog();
    }

    // ---------- Video clips ----------

    void VideoSection(Clip clip, Track track)
    {
        var media = s.P.FindMedia(clip.MediaId);
        Title(media?.Name ?? "Clip");
        Note($"{Time.Timecode(clip.Start)} · {Time.Short(clip.Duration)}" + (clip.SlotId is not null ? " · fills a script slot" : ""));
        var tr = clip.Transform;
        Add(Ui.Slider("Size", .1, 2, tr.Scale, v => EditClip("Size", c => c.Transform.Scale = v), "0%"));
        Add(Ui.Slider("Horizontal position", 0, 1, tr.X, v => EditClip("Position", c => c.Transform.X = v), "0%"));
        Add(Ui.Slider("Vertical position", 0, 1, tr.Y, v => EditClip("Position", c => c.Transform.Y = v), "0%"));
        Add(Ui.Slider("Rounded corners", 0, 60, tr.CornerRadius, v => EditClip("Corners", c => c.Transform.CornerRadius = v), "0"));
        if (track.Role == TrackRole.Facecam)
        {
            Add(Ui.Text("Facecam corner", 12, true));
            Add(Ui.Row(
                Ui.Button("↖", () => EditClip("Facecam", c => (c.Transform.X, c.Transform.Y) = (.16, .21))),
                Ui.Button("↗", () => EditClip("Facecam", c => (c.Transform.X, c.Transform.Y) = (.84, .21))),
                Ui.Button("↙", () => EditClip("Facecam", c => (c.Transform.X, c.Transform.Y) = (.16, .79))),
                Ui.Button("↘", () => EditClip("Facecam", c => (c.Transform.X, c.Transform.Y) = (.84, .79)))));
            Add(Ui.Slider("Crop sides", 0, .4, tr.CropLeft, v => EditClip("Crop", c => c.Transform.CropLeft = c.Transform.CropRight = v), "0%"));
        }
        if (!tr.IsDefault) Add(Ui.Row(Ui.Button("Reset placement", () => EditClip("Reset placement", c => c.Transform = new Transform()))));

        Title("Transition in");
        var kinds = Enum.GetValues<TransitionKind>();
        var strip = new WrapPanel();
        foreach (var k in kinds)
        {
            var kind = k;
            strip.Children.Add(Ui.Chip(TransitionName(k), clip.TransitionIn?.Kind == k, on => { if (on) EditClip("Transition", c => c.TransitionIn = new Transition { Kind = kind, Duration = c.TransitionIn?.Duration ?? Time.FromMs(300) }); }));
        }
        strip.Children.Add(Ui.Chip("None", clip.TransitionIn is null, on => { if (on) EditClip("Remove transition", c => c.TransitionIn = null); }));
        Add(strip);
        if (clip.TransitionIn is { } t)
            Add(Ui.Slider("Transition length (s)", .1, 1.5, Time.ToSeconds(t.Duration), v => EditClip("Transition length", c => c.TransitionIn!.Duration = Time.FromSeconds(v)), "0.00"));

        Title("Effects");
        if (clip.Effects.Count == 0) Note("Add one with Z (zoom), Shift+O (freeze + outline), Shift+K (shake), Shift+X (flash) or the toolbar.");
        foreach (var e in clip.Effects) EffectEditor(clip, e);
        Add(Ui.Row(
            Ui.Button("Pop in", () => cmds.AddEffect(EffectKind.PopIn)),
            Ui.Button("Slide in", () => cmds.AddEffect(EffectKind.SlideLeft)),
            Ui.Button("Punch", () => cmds.AddEffect(EffectKind.Punch))));
        if (s.P.AllClips.Any(c => c.LinkId is not null && c.LinkId == clip.LinkId && c != clip))
            Add(Ui.Slider("Game audio (dB)", -30, 12, s.P.AllClips.First(c => c.LinkId == clip.LinkId && c != clip).GainDb,
                v => s.Edit("Clip volume", p => { foreach (var l in TimelineOps.Linked(p, p.FindClip(clip.Id)!).Where(l => p.TrackOf(l)?.Kind == TrackKind.Audio)) l.GainDb = v; }), "0.0"));
    }

    static string TransitionName(TransitionKind k) => k switch
    {
        TransitionKind.CrossDissolve => "Fade", TransitionKind.DipToBlack => "Dip to black", TransitionKind.SweepLeft => "Sweep ←",
        TransitionKind.SweepRight => "Sweep →", TransitionKind.SweepUp => "Sweep ↑", _ => "Sweep ↓",
    };

    void EffectEditor(Clip clip, Effect e)
    {
        var panel = new StackPanel();
        void EditEffect(string label, Action<Effect> change) => EditClip(label, c => { if (c.Effects.FirstOrDefault(x => x.Id == e.Id) is { } fx) change(fx); });
        var remove = Ui.Button("Remove", () => EditClip("Remove effect", c => { c.Effects.RemoveAll(x => x.Id == e.Id); }));
        panel.Children.Add(Ui.Columns((Ui.Header(e.Kind switch { EffectKind.ZoomToBox => "Zoom to box", EffectKind.FreezeOutline => "Freeze + outline", _ => e.Kind.ToString() }), new GridLength(1, GridUnitType.Star)), (remove, GridLength.Auto)));
        panel.Children.Add(Ui.Slider("Starts at (s into clip)", 0, Time.ToSeconds(clip.Duration), Time.ToSeconds(e.Offset), v => EditEffect("Effect start", fx => fx.Offset = Time.SnapToFrame(Time.FromSeconds(v))), "0.00"));
        panel.Children.Add(Ui.Slider("Duration (s)", .1, Math.Max(.2, Time.ToSeconds(clip.Duration)), Time.ToSeconds(e.Duration), v => EditEffect("Effect length", fx => fx.Duration = Time.FromSeconds(v)), "0.00"));
        if (e.Kind is not (EffectKind.Flash or EffectKind.Shake))
            panel.Children.Add(Ui.Slider("Animation speed (s)", .05, 1, Time.ToSeconds(e.Ramp), v => EditEffect("Effect speed", fx => fx.Ramp = Time.FromSeconds(v)), "0.00"));
        panel.Children.Add(Ui.Combo(Enum.GetValues<Easing>(), e.Easing, x => x.ToString(), v => EditEffect("Easing", fx => fx.Easing = v)));
        if (e.Kind is EffectKind.Shake or EffectKind.Punch)
            panel.Children.Add(Ui.Slider("Strength", .2, 3, e.Amount, v => EditEffect("Strength", fx => fx.Amount = v), "0.0"));
        if (e.Kind is EffectKind.ZoomToBox or EffectKind.FreezeOutline)
        {
            if (e.Kind == EffectKind.FreezeOutline)
                panel.Children.Add(Ui.Row(
                    Ui.Chip("Circle", e.Shape == ShapeKind.Ellipse, on => { if (on) EditEffect("Shape", fx => fx.Shape = ShapeKind.Ellipse); }),
                    Ui.Chip("Box", e.Shape == ShapeKind.Box, on => { if (on) EditEffect("Shape", fx => fx.Shape = ShapeKind.Box); }),
                    Ui.Chip("Arrow", e.Shape == ShapeKind.Arrow, on => { if (on) EditEffect("Shape", fx => fx.Shape = ShapeKind.Arrow); })));
            panel.Children.Add(Ui.Text("Box: click two corners on the preview, or fine-tune here.", 11, true));
            panel.Children.Add(Ui.Slider("Box left", 0, 1, e.BoxX, v => EditEffect("Box", fx => fx.BoxX = v), "0%"));
            panel.Children.Add(Ui.Slider("Box top", 0, 1, e.BoxY, v => EditEffect("Box", fx => fx.BoxY = v), "0%"));
            panel.Children.Add(Ui.Slider("Box width", .03, 1, e.BoxW, v => EditEffect("Box", fx => { fx.BoxW = v; if (fx.Kind == EffectKind.ZoomToBox) fx.BoxH = v; }), "0%"));
            if (e.Kind == EffectKind.FreezeOutline) panel.Children.Add(Ui.Slider("Box height", .03, 1, e.BoxH, v => EditEffect("Box", fx => fx.BoxH = v), "0%"));
        }
        var card = Ui.Card(panel, 10);
        card.Background = e.Id == focusEffect ? Theme.AccentSoft : Theme.Panel2Brush;
        card.Margin = new Thickness(0, 0, 0, 10);
        Add(card);
    }

    // ---------- Audio ----------

    void AudioSection(Clip clip)
    {
        var track = s.P.TrackOf(clip);
        Title(s.P.FindMedia(clip.MediaId)?.Name ?? "Audio");
        if (track?.Role == TrackRole.Narration) { Note("Narration cuts are reviewed in the Clean stage."); TrackSection(track); return; }
        Add(Ui.Slider("Volume (dB)", -30, 12, clip.GainDb, v => EditClip("Clip volume", c => c.GainDb = v), "0.0"));
        Add(Ui.Slider("Fade in (s)", 0, Math.Min(10, Time.ToSeconds(clip.Duration) / 2), Time.ToSeconds(clip.FadeIn), v => EditClip("Fade in", c => c.FadeIn = Time.FromSeconds(v)), "0.0"));
        Add(Ui.Slider("Fade out (s)", 0, Math.Min(10, Time.ToSeconds(clip.Duration) / 2), Time.ToSeconds(clip.FadeOut), v => EditClip("Fade out", c => c.FadeOut = Time.FromSeconds(v)), "0.0"));
        if (track is not null) TrackSection(track);
    }

    void TrackSection(Track track)
    {
        Title($"{track.Name} track");
        var id = track.Id;
        void EditTrack(string label, Action<Track> change) => s.Edit(label, p => change(p.Tracks.First(t => t.Id == id)));
        Add(Ui.Slider("Track volume (dB)", -40, 12, track.VolumeDb, v => EditTrack("Track volume", t => t.VolumeDb = v), "0.0"));
        if (track.Role is TrackRole.Game or TrackRole.Music or TrackRole.Sfx)
        {
            Add(Ui.Check("Duck under narration", track.DuckEnabled, on => EditTrack("Ducking", t => t.DuckEnabled = on)));
            if (track.DuckEnabled)
            {
                Add(Ui.Slider("How far it ducks (dB)", -30, 0, track.DuckDepthDb, v => EditTrack("Duck depth", t => t.DuckDepthDb = v), "0"));
                Add(Ui.Slider("Duck speed (ms)", 30, 600, track.DuckAttackMs, v => EditTrack("Duck speed", t => t.DuckAttackMs = v), "0", 10));
                Add(Ui.Slider("Recovery (ms)", 100, 1500, track.DuckReleaseMs, v => EditTrack("Duck recovery", t => t.DuckReleaseMs = v), "0", 10));
            }
        }
    }

    // ---------- Subtitles ----------

    void RegionSection(SubtitleRegion region)
    {
        Title($"Subtitles · {region.Label}");
        var id = region.Id;
        void EditRegion(string label, Action<SubtitleRegion> change) => s.Edit(label, p => change(p.SubtitleRegions.First(r => r.Id == id)));
        Add(Ui.Text("Style preset", 12, true));
        Add(Ui.Combo(s.Settings.SubtitlePresets, s.Settings.Preset(region.PresetId), x => x.Name, x => EditRegion("Subtitle preset", r => r.PresetId = x.Id)));
        Add(Ui.Check("Show over text cards", region.ShowOverCards, on => EditRegion("Subtitles over cards", r => r.ShowOverCards = on)));
        var preset = s.Settings.Preset(region.PresetId);
        Title($"Preset: {preset.Name}");
        Note("Presets are shared by all your projects.");
        void EditPreset(Action<SubtitlePreset> change) { change(preset); s.Settings.Save(); s.Edit("Subtitle style", _ => { }); }
        Add(Ui.Slider("Size", 30, 110, preset.Size, v => EditPreset(x => x.Size = v), "0"));
        Add(Ui.Slider("Outline", 0, 16, preset.OutlineWidth, v => EditPreset(x => x.OutlineWidth = v), "0"));
        Add(Ui.Slider("Shadow", 0, 1, preset.ShadowOpacity, v => EditPreset(x => x.ShadowOpacity = v), "0%"));
        Add(Ui.Slider("Height on screen", .5, .95, preset.Y, v => EditPreset(x => x.Y = v), "0%"));
        Add(Ui.Text("Fill / outline colors", 12, true));
        Add(Ui.Row(
            ColorBox(preset.Fill, v => EditPreset(x => x.Fill = v)),
            ColorBox(preset.Outline, v => EditPreset(x => x.Outline = v))));
        Add(Ui.Combo(new[] { "Montserrat ExtraBold", "Montserrat SemiBold", "Segoe UI Black", "Arial Black", "Impact" }, preset.FontFamily, x => x, v => EditPreset(x => x.FontFamily = v)));
        Add(Ui.Row(
            Ui.Chip("No animation", preset.Animation == "None", on => { if (on) EditPreset(x => x.Animation = "None"); }),
            Ui.Chip("Fade", preset.Animation == "Fade", on => { if (on) EditPreset(x => x.Animation = "Fade"); }),
            Ui.Chip("Pop", preset.Animation == "Pop", on => { if (on) EditPreset(x => x.Animation = "Pop"); })));
        Add(Ui.Row(
            Ui.Button("Save as new preset", () =>
            {
                var name = Ui.Ask(Window.GetWindow(this)!, "New preset", "Preset name:", preset.Name + " copy");
                if (string.IsNullOrWhiteSpace(name)) return;
                var copy = System.Text.Json.JsonSerializer.Deserialize<SubtitlePreset>(System.Text.Json.JsonSerializer.Serialize(preset))!;
                copy.Id = Ids.New(); copy.Name = name;
                s.Settings.SubtitlePresets.Add(copy);
                s.Settings.Save();
                EditRegion("Subtitle preset", r => r.PresetId = copy.Id);
            })));
        CaptionEditor(region);
        Add(Ui.Row(Ui.Button("Remove this region", cmds.Delete)));
    }

    TextBox ColorBox(string value, Action<string> commit)
    {
        var box = new TextBox { Text = value, Width = 100, Margin = new Thickness(0, 0, 8, 10), ToolTip = "Hex color, e.g. #FFFFFF" };
        box.LostFocus += (_, _) => { if (box.Text != value && box.Text.StartsWith('#')) commit(box.Text); };
        return box;
    }

    void CaptionEditor(SubtitleRegion region)
    {
        Title("Caption at playhead");
        var timings = Subtitles.Timings(s.P);
        var current = timings.FirstOrDefault(c => s.Playhead >= c.Start && s.Playhead < c.End) ?? timings.FirstOrDefault(c => c.Start >= region.Start && c.Start >= s.Playhead);
        if (current is null) { Note("No caption here."); return; }
        var id = current.Caption.Id;
        var box = new TextBox { Text = current.Caption.Text, Margin = new Thickness(0, 0, 0, 8) };
        box.LostFocus += (_, _) => { if (box.Text != current.Caption.Text) s.Edit("Edit caption", p => p.Captions.First(c => c.Id == id).Text = box.Text); };
        Add(box);
        Add(Ui.Row(Ui.Button("Split at playhead", cmds.SplitCaption, "sub.split"), Ui.Button("Merge with next", cmds.MergeCaption, "sub.merge")));
    }

    // ---------- Project ----------

    void ProjectSection()
    {
        Title("Project");
        Note("Select a clip, card or subtitle region on the timeline to edit it here.");
        Add(Ui.Text("Subtitles", 12, true));
        Add(Ui.Row(Ui.Button("Intro only", cmds.SubtitlesIntroOnly), Ui.Button("Whole video", cmds.SubtitlesWholeVideo), Ui.Button("In/out range", cmds.SubtitlesOnRange, "sub.customRegion")));
        Add(Ui.Check("Auto-pair whooshes and impacts (from SFX tagged #whoosh / #impact)", s.P.AutoSfx, on => s.Edit("Auto SFX", p => p.AutoSfx = on)));
        Add(Ui.Check("One slot per paragraph (instead of per sentence)", s.P.SlotsPerParagraph, on => s.Edit("Slot size", p => { p.SlotsPerParagraph = on; Derive.GenerateSlots(p); })));
        var plan = Ui.Button("AI: plan visuals for every slot", () => _ = cmds.PlanVisuals());
        plan.IsEnabled = s.Ai.Enabled;
        Add(Ui.Row(plan));
        if (!s.Ai.Enabled) Note("AI is off: install and sign in to Claude Code or Codex to enable it.");
        Add(Ui.Row(Ui.Button("Re-place section headers", () => s.Edit("Section headers", p => Derive.PlaceSectionHeaders(p, Time.FromSeconds(2.5))))));
        if (s.SelectedTrackId is { } tid && s.P.Tracks.FirstOrDefault(t => t.Id == tid) is { Kind: TrackKind.Audio } track) TrackSection(track);
        else foreach (var t in s.P.Tracks.Where(t => t.Role is TrackRole.Game or TrackRole.Music)) TrackSection(t);
    }
}

/// <summary>Equal-width columns helper (UniformGrid without needing XAML).</summary>
public sealed class UniformGridPanel(int columns)
{
    public System.Windows.Controls.Primitives.UniformGrid Panel { get; } = new() { Columns = columns };
    public void Add(UIElement e) { if (e is FrameworkElement f) f.Margin = new Thickness(0, 0, 10, 0); Panel.Children.Add(e); }
}
