using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Cutroom.AI;
using Cutroom.Audio;
using Cutroom.Core;
using Cutroom.Library;
using Cutroom.Speech;

namespace Cutroom.App;

/// <summary>
/// Everything about the open project shared by the stages: document and undo, settings, library, playback transport,
/// selection, marks and the source viewer. Every project change goes through <see cref="Edit"/>.
/// </summary>
public sealed class Session : IDisposable
{
    public static Session? Current { get; private set; }

    public AppSettings Settings { get; }
    public Document Doc { get; private set; }
    public Project P => Doc.Project;
    public LibraryIndex Library { get; }
    public AiService Ai { get; }
    public Player Player { get; } = new();
    public Mixer Mixer { get; }
    public InspectorPanel? Inspector { get; set; }
    readonly DispatcherTimer autosave;
    readonly Dictionary<string, string> fingerprints = [];

    public event Action? ProjectChanged;
    public event Action? PlayheadChanged;
    public event Action? SelectionChanged;
    public event Action? DocumentReplaced;
    public event Action<string>? Status;

    public Session(AppSettings settings, Document document)
    {
        Current = this;
        Settings = settings;
        Transcriber.PreferGpu = Settings.GpuTranscription;
        Library = new LibraryIndex();
        Ai = new AiService(Settings.AiProvider);
        Mixer = new Mixer(AudioPathFor);
        Doc = document;
        Hook();
        autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        autosave.Tick += (_, _) => SaveSafely(force: false);
        autosave.Start();
        foreach (var f in Settings.FootageFolders) Library.Watch(f, LibraryRole.Footage);
        foreach (var f in Settings.AssetFolders) Library.Watch(f, LibraryRole.Asset);
        _ = RescanLibrary();
        _ = EnsureAllDerivatives();
    }

    public string ProjectFolder => Path.GetDirectoryName(Doc.Path!)!;

    void Hook()
    {
        Doc.Changed += () =>
        {
            Mixer.Prepare(P, Doc.Revision);
            ProjectChanged?.Invoke();
        };
        Mixer.Prepare(P, Doc.Revision);
    }

    public void RememberProject()
    {
        try { new ProjectCatalog(Settings).Remember(Doc.Path!); }
        catch (Exception ex) when (ProjectCatalog.IsProjectError(ex)) { Say("Could not remember the project: " + ex.Message); }
    }

    public void Replace(Document doc)
    {
        Stop();
        Doc = doc;
        Hook();
        Playhead = 0;
        SelectedClipId = null;
        MarkIn = MarkOut = null;
        Source = null;
        LibraryItemId = null;
        ShowingSource = false;
        SourcePosition = 0;
        SourceIn = SourceOut = null;
        SourceChanged?.Invoke();
        DocumentReplaced?.Invoke();
        ProjectChanged?.Invoke();
        RememberProject();
        _ = EnsureAllDerivatives();
    }

    int projectWork;
    public bool ProjectWorkInProgress => projectWork > 0;

    public IDisposable BeginProjectWork()
    {
        projectWork++;
        return new ProjectWork(this);
    }

    sealed class ProjectWork(Session session) : IDisposable
    {
        public void Dispose() => session.projectWork--;
    }

    public void SaveSafely(bool force = true)
    {
        try
        {
            if (force) { if (Doc.Dirty) Doc.Save(); }
            else if (Doc.AutoSave()) Status?.Invoke("Saved");
        }
        catch (IOException ex) { Status?.Invoke("Could not save: " + ex.Message); }
    }

    public void Edit(string label, Action<Project> change)
    {
        Doc.Edit(label, change);
    }

    public void Undo() { if (Doc.Undo()) Say("Undid " + (Doc.RedoLabel ?? "")); }
    public void Redo() { if (Doc.Redo()) Say("Redid " + (Doc.UndoLabel ?? "")); }
    public void Say(string message) => Status?.Invoke(message);

    // ---------- Selection, marks, playhead ----------

    long playhead;
    public long Playhead
    {
        get => playhead;
        set
        {
            var v = Math.Clamp(Time.FloorFrame(value), 0, Math.Max(0, P.Duration + Time.FromSeconds(10)));
            if (v == playhead) return;
            playhead = v;
            PlayheadChanged?.Invoke();
        }
    }

    public long? MarkIn { get; set; }
    public long? MarkOut { get; set; }

    string? selectedClipId;
    public string? SelectedClipId
    {
        get => selectedClipId;
        set { selectedClipId = value; SelectedRegionId = null; SelectionChanged?.Invoke(); }
    }
    public Clip? SelectedClip => SelectedClipId is null ? null : P.FindClip(SelectedClipId);
    public string? SelectedRegionId { get; private set; }
    public void SelectRegion(string? id) { selectedClipId = null; SelectedRegionId = id; SelectionChanged?.Invoke(); }
    public string? SelectedTrackId { get; set; }

    public Slot? CurrentSlot => P.Slots.FirstOrDefault(s => Playhead >= s.Start && Playhead < s.End) ?? P.Slots.LastOrDefault(s => s.Start <= Playhead);

    // ---------- Source viewer ----------

    public MediaRef? Source { get; private set; }
    public long SourcePosition { get; set; }
    public long? SourceIn { get; set; }
    public long? SourceOut { get; set; }
    public bool ShowingSource { get; set; }
    public long? LibraryItemId { get; private set; }
    public event Action? SourceChanged;

    public async Task LoadSource(LibraryItem item, Moment? moment = null)
    {
        using var work = BeginProjectWork();
        var media = P.Media.FirstOrDefault(m => m.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)) ?? await MediaProbe.Probe(item.Path);
        Source = media;
        LibraryItemId = item.Id;
        SourcePosition = moment is null ? 0 : Time.FromSeconds(moment.In);
        SourceIn = moment is null ? null : Time.FromSeconds(moment.In);
        SourceOut = moment is null ? null : Time.FromSeconds(moment.Out);
        ShowingSource = true;
        _ = EnsureDerivatives(media);
        SourceChanged?.Invoke();
    }

    public void ToggleSourceView()
    {
        ShowingSource = !ShowingSource && Source is not null;
        SourceChanged?.Invoke();
    }

    // ---------- Media paths and derivatives ----------

    string FingerprintOf(string path)
    {
        if (!fingerprints.TryGetValue(path, out var fp)) fingerprints[path] = fp = Tools.Fingerprint(path);
        return fp;
    }

    /// <summary>Cached decoded audio for the mixer; narration plays its cleaned, denoised derivative.</summary>
    public string? AudioPathFor(MediaRef m)
    {
        var n = P.Narration;
        if (m.Id == n.MediaId && n.CleanPath is { } clean && File.Exists(clean))
        {
            var p = Derivatives.AudioPath(FingerprintOf(clean));
            if (File.Exists(p)) return p;
        }
        var path = Derivatives.AudioPath(m.Fingerprint);
        return File.Exists(path) ? path : null;
    }

    internal string? NarrationPreviewPathFor(MediaRef media)
    {
        if (P.Narration.CleanPath is { } clean && File.Exists(clean))
        {
            var path = Derivatives.AudioPath(FingerprintOf(clean));
            return File.Exists(path) ? path : null;
        }
        return AudioPathFor(media);
    }

    public string PreviewPathFor(MediaRef m) => Derivatives.PreviewPath(m);

    public byte[] PeaksFor(MediaRef m)
    {
        var n = P.Narration;
        if (m.Id == n.MediaId && n.CleanPath is { } clean && File.Exists(clean)) return Derivatives.LoadPeaks(FingerprintOf(clean));
        return Derivatives.LoadPeaks(m.Fingerprint);
    }

    readonly HashSet<string> deriving = [];

    public async Task EnsureDerivatives(MediaRef m)
    {
        if (!deriving.Add(m.Id) || !File.Exists(m.Path)) return;
        try
        {
            if (m.Kind != MediaKind.Image && (m.HasAudio || m.Kind == MediaKind.Audio))
            {
                var audio = await JobQueue.Shared.Run<string>($"Audio: {m.Name}", (p, ct) => Derivatives.EnsureAudio(m.Path, m.Fingerprint, Time.ToSeconds(m.Duration), p, ct), background: true);
                await Task.Run(() => Derivatives.EnsurePeaks(audio, m.Fingerprint));
                Application.Current.Dispatcher.Invoke(() => { Mixer.Dispose(); Mixer.Prepare(P, -1); ProjectChanged?.Invoke(); });
            }
            if (m.Kind == MediaKind.Video)
            {
                await JobQueue.Shared.Run($"Proxy: {m.Name}", (p, ct) => Derivatives.EnsureProxy(m, p, ct), background: true);
                await JobQueue.Shared.Run($"Thumbnails: {m.Name}", (_, ct) => Derivatives.EnsureStrip(m, ct), background: true);
                Application.Current.Dispatcher.Invoke(() => ProjectChanged?.Invoke());
            }
        }
        catch (Exception ex) when (ex is ToolException or IOException) { Say($"{m.Name}: {ex.Message}"); }
        finally { deriving.Remove(m.Id); }
    }

    async Task EnsureAllDerivatives()
    {
        var document = Doc;
        foreach (var m in document.Project.Media.ToList())
        {
            if (document != Doc) return;
            await EnsureDerivatives(m);
        }
        if (document == Doc && P.Narration.CleanPath is { } clean && File.Exists(clean)) await EnsureCleanAudio(clean);
    }

    async Task EnsureCleanAudio(string clean)
    {
        var fp = FingerprintOf(clean);
        var audio = await Derivatives.EnsureAudio(clean, fp, 0, null, CancellationToken.None);
        await Task.Run(() => Derivatives.EnsurePeaks(audio, fp));
        Mixer.Dispose();
        Mixer.Prepare(P, -1);
    }

    /// <summary>Adds a file to the project (once) and starts its proxies/audio in the background.</summary>
    public async Task<MediaRef> Import(string path)
    {
        var existing = P.Media.FirstOrDefault(m => m.Path.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var media = await MediaProbe.Probe(path);
        Edit($"Import {media.Name}", p => p.Media.Add(media));
        _ = EnsureDerivatives(media);
        return P.FindMedia(media.Id)!;
    }

    public async Task RescanLibrary()
    {
        foreach (var f in Settings.FootageFolders) await JobQueue.Shared.Run($"Indexing {Path.GetFileName(f)}", (p, ct) => Library.Scan(f, LibraryRole.Footage, p, ct), background: true);
        foreach (var f in Settings.AssetFolders) await JobQueue.Shared.Run($"Indexing {Path.GetFileName(f)}", (p, ct) => Library.Scan(f, LibraryRole.Asset, p, ct), background: true);
    }

    // ---------- Narration ----------

    public void SetScript(Script script)
    {
        Edit("Load script", p =>
        {
            p.Script = script;
            if (p.Narration.Processed) Say("Script loaded. Run cleanup again to match it.");
        });
    }

    public async Task SetNarration(string path)
    {
        var media = await Import(path);
        Edit("Set narration", p =>
        {
            p.Narration = new Narration { MediaId = media.Id, Settings = p.Narration.Settings, RetakeMarks = p.Narration.RetakeMarks, SourceDuration = media.Duration };
            p.Narration.Settings.Denoiser = Settings.Denoiser;
            var track = p.Track(TrackRole.Narration);
            track.Clips.RemoveAll(c => c.Label == "Narration");
            track.Clips.Add(new Clip { MediaId = media.Id, Start = 0, Duration = media.Duration, Label = "Narration" });
        });
    }

    public CancellationTokenSource? CleanupCancel { get; private set; }

    public async Task RunCleanup(IProgress<CleanupStage> progress)
    {
        var media = P.FindMedia(P.Narration.MediaId) ?? throw new InvalidOperationException("Record or import a narration first.");
        var document = Doc;
        var script = P.Script;
        CleanupCancel = new CancellationTokenSource();
        var token = CleanupCancel.Token;
        var settings = P.Narration.Settings;
        var marks = P.Narration.RetakeMarks.ToList();
        try
        {
            var result = await Task.Run(() => CleanupEngine.Run(media, script, marks, settings, Settings.WhisperModel, progress, token));
            token.ThrowIfCancellationRequested();
            await EnsureCleanAudio(result.CleanPath);
            token.ThrowIfCancellationRequested();
            if (document != Doc || P.Narration.MediaId != media.Id) return;
            ApplyCleanup(result);
        }
        finally { CleanupCancel.Dispose(); CleanupCancel = null; }
    }

    void ApplyCleanup(CleanupResult r)
    {
        Edit("Clean narration", p =>
        {
            var n = p.Narration;
            n.CleanPath = r.CleanPath;
            n.GainDb = r.GainDb;
            n.Words = r.Words;
            n.Decisions = r.Decisions;
            n.SourceDuration = r.Duration;
            n.Processed = true;
            Derive.RebuildNarration(p);
            Derive.GenerateSlots(p);
            RegenerateCaptions(p);
            Derive.EnsureIntroRegion(p, Settings.SubtitlePresets[0].Id);
            Derive.RefreshIntroRegion(p);
            if (p.Script?.HasHeadings == true) Derive.PlaceSectionHeaders(p, Time.FromSeconds(2.5));
        });
    }

    public void RegenerateCaptions(Project p)
    {
        var preset = Settings.Preset(p.SubtitleRegions.FirstOrDefault()?.PresetId ?? SubtitlePreset.DefaultId);
        p.Captions = Subtitles.Chunk(Derive.TimelineWords(p), new Subtitles.ChunkOptions(MaxChars: preset.MaxChars), p.Script?.Vocabulary());
    }

    /// <summary>After narration cuts change: rebuild A1, re-time slots, and refresh captions unless they were hand-edited.</summary>
    public void AfterNarrationChange(Project p)
    {
        Derive.RebuildNarration(p);
        if (!p.Captions.Any(c => c.FixedStart is not null)) RegenerateCaptions(p);
    }

    // ---------- Transport ----------

    public double Speed { get; private set; }
    public bool Playing => Speed != 0;
    readonly Stopwatch clock = new();
    long clockStart;

    public void Play(double speed = 1)
    {
        Stop();
        if (ShowingSource && Source is not null)
        {
            Speed = speed;
            clockStart = SourcePosition;
            clock.Restart();
            return;
        }
        Speed = speed;
        clockStart = Playhead;
        clock.Restart();
        if (speed == 1)
        {
            Mixer.Prepare(P, Doc.Revision);
            Player.Play(Mixer, Playhead);
        }
    }

    public void Toggle() { if (Playing) Stop(); else Play(); }

    public void Stop()
    {
        Speed = 0;
        Player.Stop();
        clock.Stop();
    }

    /// <summary>Called every rendered frame: advances the playhead from the audio clock (or wall clock at other speeds).</summary>
    public bool Tick()
    {
        if (!Playing) return false;
        long elapsed = (long)(clock.Elapsed.TotalSeconds * Time.Rate * Speed);
        if (ShowingSource && Source is not null)
        {
            SourcePosition = Math.Clamp(clockStart + elapsed, 0, Source.Duration);
            if (SourcePosition >= Source.Duration || SourcePosition <= 0 && Speed < 0) Stop();
            SourceChanged?.Invoke();
            return true;
        }
        long t = Speed == 1 && Player.Clock is { } audio ? audio : clockStart + elapsed;
        if (t >= P.Duration && Speed > 0 || t <= 0 && Speed < 0) { Stop(); t = Math.Clamp(t, 0, P.Duration); }
        Playhead = t;
        return true;
    }

    /// <summary>J/K/L shuttle: repeated L/J presses double the speed in that direction.</summary>
    public void Shuttle(int direction)
    {
        if (direction == 0) { Stop(); return; }
        double next = Speed * direction > 0 ? Math.Min(8, Math.Abs(Speed) * 2) * direction : direction;
        Play(next);
    }

    public void Seek(long t)
    {
        bool was = Playing && Speed == 1;
        if (ShowingSource && Source is not null) { SourcePosition = Math.Clamp(t, 0, Source.Duration); SourceChanged?.Invoke(); return; }
        Playhead = t;
        if (was) Play();
    }

    public void SeekTimeline(long t)
    {
        Stop();
        bool wasShowingSource = ShowingSource;
        ShowingSource = false;
        Seek(t);
        if (wasShowingSource) SourceChanged?.Invoke();
    }

    public void Step(long delta)
    {
        if (ShowingSource && Source is not null) Seek(SourcePosition + delta);
        else Seek(Playhead + delta);
    }

    public void Dispose()
    {
        autosave.Stop();
        SaveSafely();
        Settings.Save();
        Player.Dispose();
        Mixer.Dispose();
        Library.Dispose();
    }
}
