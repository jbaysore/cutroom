using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.App;

/// <summary>The Edit stage's one-key actions. Each is a single undoable change.</summary>
public sealed class EditCommands(Session s)
{
    Project P => s.P;
    int sfxRotation;

    TimeRange TargetRange()
    {
        if (s.MarkIn is { } a && s.MarkOut is { } b && b > a) return new TimeRange(a, b);
        if (s.CurrentSlot is { } slot) return new TimeRange(slot.Start, slot.End);
        return new TimeRange(s.Playhead, s.Playhead + Time.FromSeconds(3));
    }

    // ---------- Basic editing ----------

    public void MarkIn() { s.MarkIn = s.ShowingSource ? null : s.Playhead; if (s.ShowingSource) s.SourceIn = s.SourcePosition; s.Say("In point set"); Touch(); }
    public void MarkOut() { if (s.ShowingSource) s.SourceOut = s.SourcePosition; else s.MarkOut = s.Playhead; s.Say("Out point set"); Touch(); }
    void Touch() { s.Seek(s.ShowingSource ? s.SourcePosition : s.Playhead); }

    public void Blade() => s.Edit("Blade", p => TimelineOps.Blade(p, s.Playhead, p.Tracks.Where(t => t.Role != TrackRole.Narration)));

    public void RippleDelete()
    {
        if (s.SelectedClip is { } clip)
        {
            var id = clip.Id;
            s.Edit("Ripple delete", p => TimelineOps.RippleDelete(p, [p.FindClip(id)!]));
            s.SelectedClipId = null;
        }
        else if (s.MarkIn is { } a && s.MarkOut is { } b && b > a)
        {
            s.Edit("Ripple delete range", p => TimelineOps.RippleDeleteRange(p, new TimeRange(a, b), p.Tracks.Where(t => t.Role != TrackRole.Narration && t.Role != TrackRole.Subtitles)));
            s.MarkIn = s.MarkOut = null;
        }
    }

    public void Delete()
    {
        if (s.SelectedRegionId is { } rid) { s.Edit("Remove subtitle region", p => p.SubtitleRegions.RemoveAll(r => r.Id == rid)); s.SelectRegion(null); return; }
        if (s.SelectedClip is not { } clip) return;
        if (P.TrackOf(clip)?.Role == TrackRole.Narration) { s.Say("Narration is edited in the Clean stage."); return; }
        var id = clip.Id;
        s.Edit("Delete", p => TimelineOps.Delete(p, [p.FindClip(id)!]));
        s.SelectedClipId = null;
    }

    public void ToggleLink()
    {
        if (s.SelectedClip is not { } clip) return;
        var id = clip.Id;
        s.Edit(clip.LinkId is null ? "Link" : "Unlink", p =>
        {
            var c = p.FindClip(id)!;
            if (c.LinkId is not null) { foreach (var l in TimelineOps.Linked(p, c)) l.LinkId = null; return; }
            var partner = p.AllClips.FirstOrDefault(x => x != c && x.MediaId == c.MediaId && x.Start == c.Start && x.LinkId is null);
            if (partner is not null) c.LinkId = partner.LinkId = Ids.New();
        });
    }

    public void Fade()
    {
        if (s.SelectedClip is not { } clip) return;
        var id = clip.Id;
        s.Edit("Fade", p =>
        {
            var c = p.FindClip(id)!;
            long f = Math.Min(Time.FromMs(500), c.Duration / 3);
            bool on = c.FadeIn == 0 && c.FadeOut == 0;
            c.FadeIn = c.FadeOut = on ? f : 0;
        });
    }

    // ---------- Placing media ----------

    public Clip? Place(Project p, MediaRef m, Track? track, long at, long? sourceIn = null, long? duration = null, string? slotId = null)
    {
        if (track is null || track.Kind == TrackKind.Subtitle || (track.Kind == TrackKind.Audio) != (m.Kind == MediaKind.Audio))
            track = m.Kind switch
            {
                MediaKind.Audio => p.Track(Time.ToSeconds(m.Duration) > 20 ? TrackRole.Music : TrackRole.Sfx),
                _ => p.Track(TrackRole.Footage),
            };
        if (track.Role == TrackRole.Narration) track = p.Track(TrackRole.Music);
        long dur = duration ?? (m.Kind == MediaKind.Image ? Time.FromSeconds(4) : m.Duration - (sourceIn ?? 0));
        if (m.Kind != MediaKind.Image) dur = Math.Min(dur, m.Duration - (sourceIn ?? 0));
        if (dur <= 0) return null;
        var clip = new Clip { MediaId = m.Id, Start = at, Duration = Time.SnapToFrame(dur), SourceIn = sourceIn ?? 0, SlotId = slotId };
        if (track.Role == TrackRole.Facecam) clip.Transform = new Transform { Scale = .28, X = .84, Y = .79, CornerRadius = 24 };
        TimelineOps.Overwrite(p, track, clip);
        if (m.Kind == MediaKind.Video && m.HasAudio && track.Kind == TrackKind.Video && track.Role != TrackRole.Facecam)
        {
            var audio = new Clip { MediaId = m.Id, Start = clip.Start, Duration = clip.Duration, SourceIn = clip.SourceIn, SlotId = slotId };
            clip.LinkId = audio.LinkId = Ids.New();
            TimelineOps.Overwrite(p, p.Track(TrackRole.Game), audio);
        }
        return clip;
    }

    public async Task DropFiles(string[] files, Track? track, long at)
    {
        using var work = s.BeginProjectWork();
        foreach (var f in files.Where(MediaProbe.IsMedia))
        {
            var m = await s.Import(f);
            s.Edit($"Place {m.Name}", p => { var c = Place(p, p.FindMedia(m.Id)!, track is null ? null : p.Tracks.First(t => t.Id == track.Id), at); if (c is not null) at = c.End; });
            s.Library.MarkUsed(f);
        }
    }

    /// <summary>F: fill the current slot from the source in-point (or position), trimmed to the slot length.</summary>
    public async Task FillSlot(bool useRange)
    {
        using var work = s.BeginProjectWork();
        var slot = s.CurrentSlot;
        if (slot is null) { s.Say("No script slot here yet. Clean the narration first."); return; }
        if (s.Source is null) { s.Say("Pick footage in the library first (Enter previews it in the source viewer)."); return; }
        var m = await s.Import(s.Source.Path);
        long from = s.SourceIn ?? s.SourcePosition;
        long length = useRange && s.SourceOut is { } o && o > from ? o - from : slot.End - slot.Start;
        var slotId = slot.Id;
        s.Edit("Fill slot", p =>
        {
            var sl = p.Slots.First(x => x.Id == slotId);
            Place(p, p.FindMedia(m.Id)!, p.Track(TrackRole.Footage), sl.Start, Math.Min(from, Math.Max(0, m.Duration - length)), length, useRange ? null : slotId);
        });
        s.Library.MarkUsed(m.Path);
        s.ShowingSource = false;
        s.SourceIn = s.SourceOut = null;
        NextEmptySlot(1);
    }

    public void NextEmptySlot(int direction)
    {
        var slots = P.Slots;
        var ordered = direction > 0 ? slots.Where(x => x.Start > s.Playhead) : slots.Where(x => x.Start < s.Playhead - Time.Frame).Reverse();
        var target = ordered.FirstOrDefault(x => Derive.StatusOf(P, x) == SlotStatus.Empty);
        if (target is null) { s.Say(direction > 0 ? "No empty slots ahead." : "No empty slots behind."); return; }
        s.ShowingSource = false;
        s.Seek(target.Start);
    }

    public void SlipSlot(int frames)
    {
        var clip = s.SelectedClip ?? P.Track(TrackRole.Footage).At(s.Playhead);
        if (clip is null) return;
        var id = clip.Id;
        s.Edit("Slip", p => { foreach (var c in TimelineOps.Linked(p, p.FindClip(id)!)) TimelineOps.Slip(c, frames * Time.Frame, p.FindMedia(c.MediaId)); });
    }

    // ---------- Cards ----------

    public Clip AddCard(CardTemplateKind kind, string? text = null, string? templateId = null)
    {
        var range = TargetRange();
        var slot = s.CurrentSlot;
        var template = s.Settings.Template(templateId ?? "", kind);
        string body = text ?? kind switch
        {
            CardTemplateKind.SectionHeader => slot?.SectionTitle ?? "Section",
            CardTemplateKind.TitleCard => P.Name,
            CardTemplateKind.EndScreen => "Thanks for watching!",
            _ => slot?.Text ?? "Type your text in the Inspector",
        };
        var clip = new Clip
        {
            Kind = ClipKind.Card, Start = range.Start, Duration = Math.Max(Time.FromSeconds(1), range.Duration),
            SlotId = s.MarkIn is null ? slot?.Id : null,
        };
        clip.Card = Cards.NewCard(kind, body, template.Id);
        clip.Card.Blur = template.Blur;
        clip.Card.Dim = template.Dim;
        clip.Card.Reveal = template.Reveal;
        if (kind is CardTemplateKind.SectionHeader or CardTemplateKind.TitleCard) { clip.Card.Heading = body; clip.Card.Lines.Clear(); }
        if (kind == CardTemplateKind.EndScreen) { clip.Card.Heading = body; clip.Card.Lines.Clear(); clip.Start = Math.Max(0, P.Duration - Time.FromSeconds(20)); clip.Duration = Time.FromSeconds(20); }
        s.Edit($"Add {template.Name}", p =>
        {
            TimelineOps.Overwrite(p, p.Track(TrackRole.Cards), clip);
            Cards.AutoTime(p, clip);
        });
        s.SelectedClipId = clip.Id;
        s.MarkIn = s.MarkOut = null;
        return clip;
    }

    public void ApplyTemplate(int index)
    {
        var templates = s.Settings.CardTemplates;
        if (index < 0 || index >= templates.Count) return;
        var t = templates[index];
        if (s.SelectedClip?.Card is not null)
        {
            var id = s.SelectedClipId!;
            s.Edit($"Use {t.Name}", p =>
            {
                var card = p.FindClip(id)!.Card!;
                card.Template = t.Kind; card.TemplateId = t.Id; card.Blur = t.Blur; card.Dim = t.Dim; card.Reveal = t.Reveal;
            });
        }
        else AddCard(t.Kind, null, t.Id);
    }

    public void ToggleReveal()
    {
        if (s.SelectedClip?.Card is null) return;
        var id = s.SelectedClipId!;
        s.Edit("Reveal mode", p => { var c = p.FindClip(id)!.Card!; c.Reveal = c.Reveal == RevealMode.LineByLine ? RevealMode.AllAtOnce : RevealMode.LineByLine; });
    }

    public void SwapContent()
    {
        if (s.SelectedClip?.Card is null) return;
        var id = s.SelectedClipId!;
        s.Edit("Card content", p =>
        {
            var c = p.FindClip(id)!.Card!;
            c.Content = c.Content switch { CardContent.Text => CardContent.Image, CardContent.Image => CardContent.Clip, _ => CardContent.Text };
            if (c.Content != CardContent.Text && s.Source is { } src)
            {
                var m = p.Media.FirstOrDefault(x => x.Path == src.Path);
                if (m is null) { m = src; p.Media.Add(m); }
                c.ContentMediaId = m.Id;
                c.ContentSourceIn = s.SourceIn ?? s.SourcePosition;
                if (m.Kind == MediaKind.Image) c.Content = CardContent.Image;
            }
        });
        s.Say($"Card shows {s.SelectedClip?.Card?.Content.ToString().ToLowerInvariant()} (pick an image or clip in the library first to fill it).");
    }

    readonly List<long> taps = [];
    public void RetimeTap()
    {
        var clip = s.SelectedClip;
        if (clip?.Card is null) { clip = P.Track(TrackRole.Cards).At(s.Playhead); if (clip is null) return; s.SelectedClipId = clip.Id; }
        if (taps.Count > 0 && (taps[^1] > s.Playhead || s.Playhead > clip.End)) taps.Clear();
        taps.Add(s.Playhead);
        var id = clip.Id;
        var snapshot = taps.ToList();
        s.Edit("Retime line", p => Cards.ApplyTaps(p.FindClip(id)!, snapshot));
        s.Say($"Line {snapshot.Count + 1} appears at {Time.Timecode(s.Playhead)}");
    }

    // ---------- Effects ----------

    public void AddEffect(EffectKind kind)
    {
        var track = P.Track(TrackRole.Footage);
        var clip = s.SelectedClip is { } sel && P.TrackOf(sel)?.Kind == TrackKind.Video ? sel : track.At(s.Playhead);
        if (clip is null) { s.Say("Put footage under the playhead first."); return; }
        long offset = Math.Clamp(s.Playhead - clip.Start, 0, Math.Max(0, clip.Duration - Time.Frame));
        long nextBoundary = (P.Slots.FirstOrDefault(x => x.Start > s.Playhead)?.Start ?? clip.End) - s.Playhead;
        var effect = new Effect
        {
            Kind = kind, Offset = offset,
            Duration = kind switch
            {
                EffectKind.ZoomToBox => Time.FromSeconds(2),
                EffectKind.FreezeOutline => Math.Clamp(nextBoundary, Time.FromSeconds(1), Time.FromSeconds(6)),
                EffectKind.Shake => Time.FromMs(600),
                EffectKind.Flash => Time.FromMs(250),
                _ => Time.FromSeconds(1),
            },
            Easing = kind == EffectKind.Shake ? Easing.Linear : Easing.Smooth,
            BoxX = .3, BoxY = .3, BoxW = .4, BoxH = .4, X1 = .1, Y1 = .9, X2 = .9, Y2 = .1,
        };
        if (kind is EffectKind.ZoomToBox or EffectKind.FreezeOutline && effect.Offset + effect.Duration > clip.Duration) effect.Duration = Math.Max(Time.Frame * 6, clip.Duration - effect.Offset);
        var id = clip.Id;
        s.Edit($"Add {kind}", p =>
        {
            var c = p.FindClip(id)!;
            c.Effects.Add(effect);
            if (kind is EffectKind.ZoomToBox or EffectKind.FreezeOutline) PairSfx(p, c, c.Start + effect.Offset, "impact");
        });
        s.SelectedClipId = id;
        s.Inspector?.FocusEffect(effect.Id);
        if (kind is EffectKind.ZoomToBox or EffectKind.FreezeOutline) s.Say("Click two corners on the preview to set the box (or use the Inspector).");
    }

    public void AddTransition(TransitionKind kind = TransitionKind.CrossDissolve)
    {
        Clip? target = null;
        s.Edit("Add transition", p =>
        {
            target = TimelineOps.AddTransition(p, s.Playhead, kind);
            if (target is not null && kind != TransitionKind.CrossDissolve) PairSfx(p, target, target.Start - Time.FromMs(150), "whoosh");
        });
        if (target is not null) { s.SelectedClipId = target.Id; s.Say("Transition added. Click it in the Inspector strip to swap the style."); }
    }

    /// <summary>Auto-paired SFX: a whoosh or impact from the asset library, rotating so it never repeats back to back.</summary>
    void PairSfx(Project p, Clip owner, long at, string tag)
    {
        if (!s.Settings.AutoSfx || !p.AutoSfx) return;
        var hits = s.Library.Search("#" + tag, LibraryRole.Asset).Where(h => h.Moment is null).ToList();
        if (hits.Count == 0) hits = s.Library.Search(tag, LibraryRole.Asset, AssetCategory.Sfx).Where(h => h.Moment is null).ToList();
        if (hits.Count == 0) return;
        var item = hits[sfxRotation++ % hits.Count].Item;
        var media = p.Media.FirstOrDefault(m => m.Path == item.Path);
        if (media is null)
        {
            media = new MediaRef { Path = item.Path, Kind = MediaKind.Audio, Duration = Time.FromSeconds(item.Duration), HasAudio = true, Fingerprint = item.Fingerprint };
            p.Media.Add(media);
            _ = s.EnsureDerivatives(media);
        }
        var sfx = new Clip { MediaId = media.Id, Start = Math.Max(0, at), Duration = Math.Min(media.Duration, Time.FromSeconds(3)), PairedWith = owner.Id, Label = tag };
        p.Track(TrackRole.Sfx).Clips.Add(sfx);
    }

    /// <summary>Two clicks on the preview set the selected effect's box (zoom target or outline bounds).</summary>
    System.Windows.Point? firstCorner;
    public void PreviewClicked(double fx, double fy)
    {
        var clip = s.SelectedClip;
        var effect = clip?.Effects.LastOrDefault(e => e.Kind is EffectKind.ZoomToBox or EffectKind.FreezeOutline);
        if (clip is null || effect is null) return;
        if (firstCorner is null) { firstCorner = new System.Windows.Point(fx, fy); s.Say("Now click the opposite corner."); return; }
        var a = firstCorner.Value;
        firstCorner = null;
        double x = Math.Min(a.X, fx), y = Math.Min(a.Y, fy), w = Math.Abs(fx - a.X), h = Math.Abs(fy - a.Y);
        if (w < .02 || h < .02) return;
        if (effect.Kind == EffectKind.ZoomToBox) { double size = Math.Max(w, h * 16 / 9.0); w = size; h = size * 9 / 16.0; }
        var (cid, eid) = (clip.Id, effect.Id);
        s.Edit("Set box", p =>
        {
            var e = p.FindClip(cid)!.Effects.First(x => x.Id == eid);
            e.BoxX = Math.Clamp(x, 0, 1 - w); e.BoxY = Math.Clamp(y, 0, 1 - h); e.BoxW = w; e.BoxH = h;
            if (e.Shape == ShapeKind.Arrow) { e.X1 = (a.X - e.BoxX) / w; e.Y1 = (a.Y - e.BoxY) / h; e.X2 = (fx - e.BoxX) / w; e.Y2 = (fy - e.BoxY) / h; }
        });
    }

    // ---------- Subtitles ----------

    public void SubtitlesOnRange()
    {
        if (s.MarkIn is not { } a || s.MarkOut is not { } b || b <= a) { s.Say("Mark in and out first (I, O), then press this again."); return; }
        s.Edit("Subtitles on range", p => p.SubtitleRegions.Add(new SubtitleRegion { Start = a, End = b, Label = "Custom", PresetId = s.Settings.SubtitlePresets[0].Id }));
        s.MarkIn = s.MarkOut = null;
    }

    public void SubtitlesWholeVideo() =>
        s.Edit("Subtitles on whole video", p => { p.SubtitleRegions.Clear(); p.SubtitleRegions.Add(new SubtitleRegion { Start = 0, End = p.Duration + Time.FromSeconds(60), Label = "Whole video", PresetId = s.Settings.SubtitlePresets[0].Id }); });

    public void SubtitlesIntroOnly() =>
        s.Edit("Subtitles on intro", p => { p.SubtitleRegions.Clear(); Derive.EnsureIntroRegion(p, s.Settings.SubtitlePresets[0].Id); });

    public void SplitCaption()
    {
        var timings = Subtitles.Timings(P);
        var c = timings.FirstOrDefault(x => s.Playhead >= x.Start && s.Playhead < x.End);
        if (c is null) return;
        var words = Derive.TimelineWords(P);
        int first = words.FindIndex(w => w.Word.Id == c.Caption.FirstWordId), last = words.FindIndex(w => w.Word.Id == c.Caption.LastWordId);
        int split = words.FindIndex(first, Math.Max(0, last - first + 1), w => w.TimeRange.Start >= s.Playhead);
        if (first < 0 || split <= first || split > last) return;
        var id = c.Caption.Id;
        s.Edit("Split caption", p =>
        {
            var cap = p.Captions.First(x => x.Id == id);
            int i = p.Captions.IndexOf(cap);
            var right = new Caption { FirstWordId = words[split].Word.Id, LastWordId = cap.LastWordId, Text = Derive.Join(words.Skip(split).Take(last - split + 1).Select(w => w.Word)) };
            cap.LastWordId = words[split - 1].Word.Id;
            cap.Text = Derive.Join(words.Skip(first).Take(split - first).Select(w => w.Word));
            p.Captions.Insert(i + 1, right);
        });
    }

    public void MergeCaption()
    {
        var timings = Subtitles.Timings(P);
        int i = timings.FindIndex(x => s.Playhead >= x.Start && s.Playhead < x.End);
        if (i < 0 || i + 1 >= timings.Count) return;
        var (a, b) = (timings[i].Caption.Id, timings[i + 1].Caption.Id);
        s.Edit("Merge captions", p =>
        {
            var ca = p.Captions.First(x => x.Id == a);
            var cb = p.Captions.First(x => x.Id == b);
            ca.Text += " " + cb.Text;
            ca.LastWordId = cb.LastWordId;
            if (cb.FixedEnd is not null) ca.FixedEnd = cb.FixedEnd;
            p.Captions.Remove(cb);
        });
    }

    // ---------- AI ----------

    public async Task PlanVisuals()
    {
        using var work = s.BeginProjectWork();
        if (!s.Ai.Enabled) { s.Say("Install and sign in to Claude Code or Codex to use AI."); return; }
        if (P.Slots.Count == 0) { s.Say("Clean the narration first so there are slots to plan."); return; }
        s.Say("Planning visuals with AI…");
        var plan = await s.Ai.PlanVisuals(P.Slots, CancellationToken.None);
        s.Edit("AI visual plan", p =>
        {
            foreach (var sug in plan)
                if (p.Slots.FirstOrDefault(x => x.Id == sug.SlotId) is { } slot)
                    slot.Suggestion = new VisualSuggestion { Kind = sug.Kind, ClipDescription = sug.ClipDescription, CardText = sug.CardText, Reason = sug.Reason };
        });
        s.Say($"AI suggested visuals for {plan.Count} slots. Ctrl+Enter applies one, Ctrl+Backspace dismisses it.");
    }

    public void ApplySuggestion(Action<string> search)
    {
        var slot = s.CurrentSlot;
        if (slot?.Suggestion is not { } sug) return;
        switch (sug.Kind)
        {
            case "clip": search(sug.ClipDescription ?? slot.Text); return;
            case "textCard": AddCard(CardTemplateKind.Blur, sug.CardText); break;
            case "backgroundCard": AddCard(CardTemplateKind.Background, sug.CardText); break;
            case "sectionHeader": AddCard(CardTemplateKind.SectionHeader, slot.SectionTitle); break;
            case "zoom": AddEffect(EffectKind.ZoomToBox); break;
            case "freezeOutline": AddEffect(EffectKind.FreezeOutline); break;
        }
        DismissSuggestion();
    }

    public void DismissSuggestion()
    {
        if (s.CurrentSlot is not { Suggestion: not null } slot) return;
        var id = slot.Id;
        s.Edit("Dismiss suggestion", p => p.Slots.First(x => x.Id == id).Suggestion = null);
    }
}
