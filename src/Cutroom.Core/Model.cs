using System.Text.Json.Serialization;

namespace Cutroom.Core;

public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}

public enum MediaKind { Video, Audio, Image }

public sealed class MediaRef
{
    public string Id { get; set; } = Ids.New();
    public string Path { get; set; } = "";
    public MediaKind Kind { get; set; }
    public long Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public bool HasAudio { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
}

public enum TrackKind { Video, Audio, Subtitle }

/// <summary>What a track is for; drives colors, ducking and default routing.</summary>
public enum TrackRole { Footage, Cards, Facecam, Subtitles, Narration, Game, Music, Sfx }

public sealed class Track
{
    public string Id { get; set; } = Ids.New();
    public TrackKind Kind { get; set; }
    public TrackRole Role { get; set; }
    public string Name { get; set; } = "";
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public double VolumeDb { get; set; }
    public bool DuckEnabled { get; set; }
    public double DuckDepthDb { get; set; } = -12;
    public double DuckAttackMs { get; set; } = 150;
    public double DuckReleaseMs { get; set; } = 400;
    public List<Clip> Clips { get; set; } = [];

    public IEnumerable<Clip> Ordered => Clips.OrderBy(c => c.Start);
    public Clip? At(long t) => Clips.FirstOrDefault(c => c.TimeRange.Contains(t));
}

public enum ClipKind { Media, Card, Freeze }

public sealed class Clip
{
    public string Id { get; set; } = Ids.New();
    public ClipKind Kind { get; set; }
    public string? MediaId { get; set; }
    /// <summary>Timeline position.</summary>
    public long Start { get; set; }
    public long Duration { get; set; }
    /// <summary>Position in the source media that plays at <see cref="Start"/>.</summary>
    public long SourceIn { get; set; }
    public double GainDb { get; set; }
    public long FadeIn { get; set; }
    public long FadeOut { get; set; }
    /// <summary>Video and its game audio share a link id so they move together.</summary>
    public string? LinkId { get; set; }
    /// <summary>Set when the clip fills a script slot; it re-times with the slot.</summary>
    public string? SlotId { get; set; }
    public string Label { get; set; } = "";
    public Transition? TransitionIn { get; set; }
    public List<Effect> Effects { get; set; } = [];
    public CardData? Card { get; set; }
    public Transform Transform { get; set; } = new();
    /// <summary>Auto-paired SFX record which clip/effect created them so they can be removed together.</summary>
    public string? PairedWith { get; set; }

    [JsonIgnore] public long End => Start + Duration;
    [JsonIgnore] public TimeRange TimeRange => new(Start, End);
    public long SourceAt(long timelineTicks) => SourceIn + (timelineTicks - Start);
}

/// <summary>Static placement of a layer. 1.0 scale = fit to frame. Position is the center in 0..1 frame units.</summary>
public sealed class Transform
{
    public double X { get; set; } = .5;
    public double Y { get; set; } = .5;
    public double Scale { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public double CropLeft { get; set; }
    public double CropTop { get; set; }
    public double CropRight { get; set; }
    public double CropBottom { get; set; }
    public double CornerRadius { get; set; }
    public bool IsDefault => X == .5 && Y == .5 && Scale == 1 && Opacity == 1 && CropLeft == 0 && CropTop == 0 && CropRight == 0 && CropBottom == 0 && CornerRadius == 0;
}

public enum TransitionKind { CrossDissolve, DipToBlack, SweepLeft, SweepRight, SweepUp, SweepDown }

public sealed class Transition
{
    public TransitionKind Kind { get; set; } = TransitionKind.CrossDissolve;
    public long Duration { get; set; } = Time.FromMs(300);
}

public enum Easing { Smooth, Snappy, Bouncy, Linear }

public enum EffectKind { ZoomToBox, Shake, Flash, PopIn, SlideLeft, SlideRight, SlideUp, SlideDown, Punch, FreezeOutline }

public enum ShapeKind { Ellipse, Box, Arrow }

/// <summary>A preset effect. Times are relative to the clip start; keyframes are generated internally from these values.</summary>
public sealed class Effect
{
    public string Id { get; set; } = Ids.New();
    public EffectKind Kind { get; set; }
    public long Offset { get; set; }
    public long Duration { get; set; } = Time.FromMs(1500);
    /// <summary>Animate-in time for zoom/slide/pop presets.</summary>
    public long Ramp { get; set; } = Time.FromMs(250);
    public Easing Easing { get; set; } = Easing.Smooth;
    public double Amount { get; set; } = 1;
    /// <summary>Region in 0..1 frame units: zoom target box, or the outline shape bounds.</summary>
    public double BoxX { get; set; } = .25;
    public double BoxY { get; set; } = .25;
    public double BoxW { get; set; } = .5;
    public double BoxH { get; set; } = .5;
    public ShapeKind Shape { get; set; }
    /// <summary>Arrow tail/head in frame units.</summary>
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
}

public enum CardTemplateKind { Blur, Background, SectionHeader, TitleCard, EndScreen }
public enum RevealMode { LineByLine, AllAtOnce }
public enum CardContent { Text, Image, Clip }

public sealed class CardLine
{
    public string Text { get; set; } = "";
    /// <summary>Reveal time relative to the card start.</summary>
    public long Reveal { get; set; }
    /// <summary>True once the user retimed this line by tapping; auto matching then leaves it alone.</summary>
    public bool Manual { get; set; }
}

public sealed class CardData
{
    public CardTemplateKind Template { get; set; }
    public string TemplateId { get; set; } = "";
    public string Heading { get; set; } = "";
    public List<CardLine> Lines { get; set; } = [];
    public RevealMode Reveal { get; set; } = RevealMode.LineByLine;
    public CardContent Content { get; set; } = CardContent.Text;
    public string? ContentMediaId { get; set; }
    public long ContentSourceIn { get; set; }
    public double Blur { get; set; } = .7;
    public double Dim { get; set; } = .45;
    public string? BackgroundMediaId { get; set; }
    public string AnimationIn { get; set; } = "Fade";

    [JsonIgnore] public string FullText => string.Join("\n", Lines.Select(l => l.Text));
}

public sealed class Marker
{
    public string Id { get; set; } = Ids.New();
    public long Time { get; set; }
    public string Label { get; set; } = "";
    public bool IsSection { get; set; }
}

public sealed class SubtitleRegion
{
    public string Id { get; set; } = Ids.New();
    public long Start { get; set; }
    public long End { get; set; }
    public string PresetId { get; set; } = SubtitlePreset.DefaultId;
    public bool ShowOverCards { get; set; }
    public string Label { get; set; } = "";
    public bool IsIntro { get; set; }
}

public sealed class Caption
{
    public string Id { get; set; } = Ids.New();
    /// <summary>Anchored to narration word ids so captions follow narration edits.</summary>
    public string FirstWordId { get; set; } = "";
    public string LastWordId { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Only used for imported SRT captions that have no word anchors.</summary>
    public long? FixedStart { get; set; }
    public long? FixedEnd { get; set; }
}

public enum SlotStatus { Empty, Footage, Card }

public sealed class Slot
{
    public string Id { get; set; } = Ids.New();
    public string Text { get; set; } = "";
    public string FirstWordId { get; set; } = "";
    public string LastWordId { get; set; } = "";
    public string? SectionTitle { get; set; }
    public bool IsIntro { get; set; }
    public VisualSuggestion? Suggestion { get; set; }
    [JsonIgnore] public long Start { get; set; }
    [JsonIgnore] public long End { get; set; }
}

public sealed class VisualSuggestion
{
    public string Kind { get; set; } = "";
    public string? ClipDescription { get; set; }
    public string? CardText { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class NoDuckRange
{
    public long Start { get; set; }
    public long End { get; set; }
}

public sealed class Project
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Untitled";
    public List<MediaRef> Media { get; set; } = [];
    public Script? Script { get; set; }
    public Narration Narration { get; set; } = new();
    public List<Track> Tracks { get; set; } = [];
    public List<Marker> Markers { get; set; } = [];
    public List<SubtitleRegion> SubtitleRegions { get; set; } = [];
    public List<Caption> Captions { get; set; } = [];
    public List<Slot> Slots { get; set; } = [];
    public List<NoDuckRange> NoDuck { get; set; } = [];
    public bool AutoSfx { get; set; } = true;
    public bool SlotsPerParagraph { get; set; }
    public string? Package { get; set; }

    public static Project CreateDefault(string name)
    {
        var p = new Project { Name = name };
        p.Tracks.AddRange([
            new() { Kind = TrackKind.Video, Role = TrackRole.Facecam, Name = "Facecam" },
            new() { Kind = TrackKind.Video, Role = TrackRole.Cards, Name = "Cards" },
            new() { Kind = TrackKind.Video, Role = TrackRole.Footage, Name = "Footage" },
            new() { Kind = TrackKind.Subtitle, Role = TrackRole.Subtitles, Name = "Subtitles" },
            new() { Kind = TrackKind.Audio, Role = TrackRole.Narration, Name = "Narration" },
            new() { Kind = TrackKind.Audio, Role = TrackRole.Game, Name = "Game", DuckEnabled = true, VolumeDb = -6 },
            new() { Kind = TrackKind.Audio, Role = TrackRole.Music, Name = "Music", DuckEnabled = true, VolumeDb = -10 },
            new() { Kind = TrackKind.Audio, Role = TrackRole.Sfx, Name = "SFX" },
        ]);
        return p;
    }

    public MediaRef? FindMedia(string? id) => id is null ? null : Media.FirstOrDefault(m => m.Id == id);
    public Track Track(TrackRole role) => Tracks.First(t => t.Role == role);
    public Track? TrackOf(Clip clip) => Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
    public Clip? FindClip(string id) => Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == id);
    public IEnumerable<Clip> AllClips => Tracks.SelectMany(t => t.Clips);

    public long Duration => Tracks.SelectMany(t => t.Clips).Select(c => c.End).DefaultIfEmpty(0).Max();
}
