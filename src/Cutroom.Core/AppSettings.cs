using System.Text.Json;

namespace Cutroom.Core;

public sealed record CommandInfo(string Id, string Name, string Group, string DefaultKey);

/// <summary>Every rebindable command. Keys use WPF gesture syntax ("Ctrl+Shift+Z").</summary>
public static class Commands
{
    public static readonly IReadOnlyList<CommandInfo> All =
    [
        new("stage.record", "Go to Record", "Stages", "Ctrl+1"),
        new("stage.clean", "Go to Clean", "Stages", "Ctrl+2"),
        new("stage.edit", "Go to Edit", "Stages", "Ctrl+3"),
        new("stage.export", "Go to Export", "Stages", "Ctrl+4"),
        new("file.save", "Save project", "General", "Ctrl+S"),
        new("edit.undo", "Undo", "General", "Ctrl+Z"),
        new("edit.redo", "Redo", "General", "Ctrl+Shift+Z"),
        new("play.toggle", "Play / pause", "Playback", "Space"),
        new("play.reverse", "Play reverse (J)", "Playback", "J"),
        new("play.stop", "Stop (K)", "Playback", "K"),
        new("play.forward", "Play forward (L)", "Playback", "L"),
        new("nav.frameBack", "Previous frame", "Playback", "Left"),
        new("nav.frameForward", "Next frame", "Playback", "Right"),
        new("nav.secondBack", "Back 1 second", "Playback", "Shift+Left"),
        new("nav.secondForward", "Forward 1 second", "Playback", "Shift+Right"),
        new("nav.prevEdit", "Previous edit", "Playback", "Up"),
        new("nav.nextEdit", "Next edit", "Playback", "Down"),
        new("nav.start", "Go to start", "Playback", "Home"),
        new("nav.end", "Go to end", "Playback", "End"),
        new("mark.in", "Mark in", "Edit", "I"),
        new("mark.out", "Mark out", "Edit", "O"),
        new("edit.blade", "Blade at playhead", "Edit", "Ctrl+B"),
        new("edit.rippleDelete", "Ripple delete", "Edit", "Shift+Delete"),
        new("edit.delete", "Delete", "Edit", "Delete"),
        new("edit.transition", "Add transition", "Edit", "Ctrl+T"),
        new("edit.linkToggle", "Link / unlink audio", "Edit", "Ctrl+L"),
        new("edit.fade", "Fade selected audio", "Edit", "Alt+F"),
        new("view.popout", "Pop out preview", "Edit", "Ctrl+Shift+P"),
        new("view.zoomIn", "Zoom timeline in", "Edit", "OemPlus"),
        new("view.zoomOut", "Zoom timeline out", "Edit", "OemMinus"),
        new("slot.next", "Next empty slot", "Slots", "Ctrl+Down"),
        new("slot.prev", "Previous empty slot", "Slots", "Ctrl+Up"),
        new("slot.fill", "Fill slot from source in-point", "Slots", "F"),
        new("slot.fillRange", "Fill slot with source in/out", "Slots", "Shift+F"),
        new("slot.slipBack", "Slip source back a frame", "Slots", "Alt+Left"),
        new("slot.slipForward", "Slip source forward a frame", "Slots", "Alt+Right"),
        new("slot.applySuggestion", "Apply AI suggestion", "Slots", "Ctrl+Enter"),
        new("slot.dismissSuggestion", "Dismiss AI suggestion", "Slots", "Ctrl+Back"),
        new("source.tag", "Tag current moment", "Library", "T"),
        new("source.moment", "Save in/out as moment", "Library", "Ctrl+M"),
        new("card.blur", "Blur text card", "Cards", "C"),
        new("card.background", "Background card", "Cards", "Shift+C"),
        new("card.revealToggle", "Line reveal / fade all", "Cards", "Tab"),
        new("card.contentSwap", "Swap card content (text / image / clip)", "Cards", "Shift+Tab"),
        new("card.retimeTap", "Retime next line (tap)", "Cards", "OemPeriod"),
        new("card.aiText", "AI card text", "Cards", "Ctrl+G"),
        new("fx.zoom", "Zoom to box", "Effects", "Z"),
        new("fx.freezeOutline", "Freeze frame + outline", "Effects", "Shift+O"),
        new("fx.shake", "Shake", "Effects", "Shift+K"),
        new("fx.flash", "Flash", "Effects", "Shift+X"),
        new("sub.customRegion", "Subtitles on in/out range", "Subtitles", "Alt+S"),
        new("sub.split", "Split caption", "Subtitles", "Ctrl+Shift+S"),
        new("sub.merge", "Merge caption with next", "Subtitles", "Ctrl+Shift+M"),
        new("clean.audition", "Play before/after", "Clean", "Enter"),
        new("clean.toggle", "Toggle decision", "Clean", "X"),
        new("clean.next", "Next decision", "Clean", "N"),
        new("clean.prev", "Previous decision", "Clean", "P"),
        new("clean.acceptRest", "Accept remaining decisions", "Clean", "A"),
        new("record.toggle", "Record / stop", "Record", "Ctrl+R"),
        new("record.retake", "Mark retake", "Record", "R"),
    ];

    public static CommandInfo? Find(string id) => All.FirstOrDefault(c => c.Id == id);
}

public sealed class CardTemplate
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public CardTemplateKind Kind { get; set; }
    public double Blur { get; set; } = .7;
    public double Dim { get; set; } = .45;
    public string? BackgroundPath { get; set; }
    public RevealMode Reveal { get; set; } = RevealMode.LineByLine;
    public string HeadingFont { get; set; } = "Montserrat ExtraBold";
    public string BodyFont { get; set; } = "Montserrat SemiBold";
    public string Accent { get; set; } = "#7C8CFF";
    public string AnimationIn { get; set; } = "Fade";

    public static List<CardTemplate> Defaults() =>
    [
        new() { Id = "blur", Name = "Blur card", Kind = CardTemplateKind.Blur },
        new() { Id = "blur-heavy", Name = "Blur card (heavy)", Kind = CardTemplateKind.Blur, Blur = 1, Dim = .6 },
        new() { Id = "notebook", Name = "Notebook page", Kind = CardTemplateKind.Background },
        new() { Id = "section", Name = "Section header", Kind = CardTemplateKind.SectionHeader, Reveal = RevealMode.AllAtOnce, AnimationIn = "Pop" },
        new() { Id = "title", Name = "Title card", Kind = CardTemplateKind.TitleCard, Reveal = RevealMode.AllAtOnce, AnimationIn = "Pop" },
        new() { Id = "end", Name = "End screen", Kind = CardTemplateKind.EndScreen, Reveal = RevealMode.AllAtOnce },
    ];
}

public sealed class ChannelProfile
{
    public string ChannelId { get; set; } = "";
    public List<string> PastTitles { get; set; } = [];
    public List<string> PastDescriptions { get; set; } = [];
    public string StyleNotes { get; set; } = "";
    /// <summary>Links, socials, sign-off: inserted word for word by Cutroom, never by AI.</summary>
    public string FixedBlock { get; set; } = "";
}

public sealed class AppSettings
{
    public Dictionary<string, string> Keys { get; set; } = [];
    public List<SubtitlePreset> SubtitlePresets { get; set; } = [new()];
    public List<CardTemplate> CardTemplates { get; set; } = CardTemplate.Defaults();
    public List<string> FootageFolders { get; set; } = [];
    public List<string> AssetFolders { get; set; } = [];
    public string AiProvider { get; set; } = "claude";
    public Denoiser Denoiser { get; set; } = Denoiser.DeepFilterNet;
    public string? MicrophoneId { get; set; }
    public List<string> RecentProjects { get; set; } = [];
    public string? ProjectsFolder { get; set; }
    public ChannelProfile Channel { get; set; } = new();
    public bool AutoSfx { get; set; } = true;
    public string WhisperModel { get; set; } = "ggml-large-v3-turbo.bin";

    public string KeyFor(string command) => Keys.TryGetValue(command, out var k) ? k : Commands.Find(command)?.DefaultKey ?? "";

    public SubtitlePreset Preset(string id) => SubtitlePresets.FirstOrDefault(p => p.Id == id) ?? SubtitlePresets.FirstOrDefault() ?? new();
    public CardTemplate Template(string id, CardTemplateKind kind) =>
        CardTemplates.FirstOrDefault(t => t.Id == id) ?? CardTemplates.FirstOrDefault(t => t.Kind == kind) ?? new CardTemplate { Kind = kind };

    /// <summary>Commands sharing the same key within overlapping groups.</summary>
    public IEnumerable<(string Key, List<CommandInfo> Commands)> Conflicts() =>
        Commands.All.GroupBy(c => KeyFor(c.Id), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .Select(g => (g.Key, g.ToList()))
            .Where(x => x.Item2.Select(c => c.Group).Distinct().Count() < x.Item2.Count || x.Item2.Any(c => c.Group is "General" or "Stages"));

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cutroom");
    public static string DefaultPath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), ProjectJson.Options) ?? new();
                if (s.SubtitlePresets.Count == 0) s.SubtitlePresets.Add(new());
                foreach (var d in CardTemplate.Defaults().Where(d => s.CardTemplates.All(t => t.Id != d.Id))) s.CardTemplates.Add(d);
                return s;
            }
        }
        catch (JsonException) { File.Copy(path, path + ".corrupt", true); }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".partial";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions(ProjectJson.Options) { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
