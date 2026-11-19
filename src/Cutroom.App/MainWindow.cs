using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.App;

public enum Stage { Record, Clean, Edit, Export }

/// <summary>Top bar with the four stage tabs, the stage content, and one keyboard dispatcher driven by the Settings shortcuts.</summary>
public sealed class MainWindow : Window
{
    public Session Session { get; }
    readonly PreviewRenderer renderer;
    readonly RecordView record;
    readonly CleanView clean;
    readonly EditView edit;
    readonly ExportView export;
    readonly Dictionary<Stage, Button> pills = [];
    readonly TextBlock projectName = Ui.Text("", 13, false, FontWeights.SemiBold);
    readonly TextBlock status = Ui.Text("", 12, true);
    readonly TextBlock jobs = Ui.Text("", 12, true);
    readonly TextBlock ai = Ui.Text("", 12, true);
    readonly System.Windows.Threading.DispatcherTimer statusClear = new() { Interval = TimeSpan.FromSeconds(6) };
    public Stage Current { get; private set; } = Stage.Edit;

    public MainWindow(string? path)
    {
        Theme.Window(this);
        Width = 1600;
        Height = 960;
        MinWidth = 1100;
        MinHeight = 680;
        WindowState = WindowState.Maximized;
        Session = new Session(path);
        renderer = new PreviewRenderer(Session);
        record = new RecordView(Session);
        clean = new CleanView(Session);
        edit = new EditView(Session, renderer);
        export = new ExportView(Session);
        record.GoToClean += () => Show(Stage.Clean);
        clean.GoToEdit += () => Show(Stage.Edit);

        var content = new Grid { Margin = new Thickness(10, 0, 10, 10) };
        foreach (var v in new FrameworkElement[] { record, clean, edit, export }) content.Children.Add(v);
        var root = new DockPanel();
        var bar = TopBar();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(content);
        Content = root;

        Session.Status += Say;
        Session.ProjectChanged += UpdateTitle;
        Session.DocumentReplaced += UpdateTitle;
        JobQueue.Shared.Changed += () => Dispatcher.BeginInvoke(UpdateJobs);
        statusClear.Tick += (_, _) => { status.Text = ""; statusClear.Stop(); };
        PreviewKeyDown += OnKey;
        CompositionTarget.Rendering += OnFrame;
        Closing += (_, e) =>
        {
            if (record.Recording && MessageBox.Show("You're still recording. Stop and quit?", "Cutroom", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            if (record.Recording) record.ToggleRecord();
        };
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            renderer.Dispose();
            Session.Dispose();
        };
        BuildKeymap();
        UpdateTitle();
        UpdateJobs();
        UpdateAi();
        // Start where the work is: no narration yet means Record, an uncleaned one means Clean.
        var n = Session.P.Narration;
        Show(n.MediaId is null ? Stage.Record : !n.Processed ? Stage.Clean : Stage.Edit);
    }

    FrameworkElement TopBar()
    {
        var logo = Ui.Text("◆ Cutroom", 15, false, FontWeights.Bold);
        logo.Foreground = Theme.AccentBrush;
        logo.Margin = new Thickness(0, 0, 16, 0);
        projectName.Margin = new Thickness(0, 0, 20, 0);
        var file = Ui.Button("File ▾", () => { }, tip: "New, open, import");
        file.Click += (_, _) => FileMenu(file);

        var stages = new Border { Background = Theme.PanelBrush, CornerRadius = new CornerRadius(10), Padding = new Thickness(4) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (stage, n) in new[] { (Stage.Record, 1), (Stage.Clean, 2), (Stage.Edit, 3), (Stage.Export, 4) })
        {
            var pill = new Button { Content = $"{n}  {stage}", Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(2, 0, 2, 0), MinWidth = 110, ToolTip = $"{stage} (Ctrl+{n})" };
            pill.Click += (_, _) => Show(stage);
            pills[stage] = pill;
            row.Children.Add(pill);
        }
        stages.Child = row;

        var settings = Ui.Button("⚙", () => OpenSettings(0), tip: "Settings and shortcuts");
        var left = Ui.Row(logo, file, projectName);
        left.Margin = new Thickness(0);
        left.VerticalAlignment = VerticalAlignment.Center;
        var right = Ui.Row(status, Spacer(), jobs, Spacer(), ai, Spacer(), settings);
        right.Margin = new Thickness(0);
        right.VerticalAlignment = VerticalAlignment.Center;
        right.HorizontalAlignment = HorizontalAlignment.Right;
        var g = Ui.Columns((left, new GridLength(1, GridUnitType.Star)), (stages, GridLength.Auto), (right, new GridLength(1, GridUnitType.Star)));
        g.Margin = new Thickness(14, 10, 14, 10);
        return g;
    }

    static FrameworkElement Spacer() => new Border { Width = 14 };

    void FileMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        void Item(string header, Action a, string? key = null) { var i = new MenuItem { Header = header, InputGestureText = key ?? "" }; i.Click += (_, _) => a(); menu.Items.Add(i); }
        Item("New project…", NewProject);
        Item("Open project…", OpenProject);
        Item("Save", () => { Session.SaveSafely(); Say("Saved"); }, Session.Settings.KeyFor("file.save"));
        menu.Items.Add(new Separator());
        Item("Import media…", ImportMedia);
        var recent = Session.Settings.RecentProjects.Where(File.Exists).Skip(1).Take(8).ToList();
        if (recent.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var r in recent) { var path = r; Item(Path.GetFileName(Path.GetDirectoryName(path)) ?? path, () => OpenPath(path)); }
        }
        menu.Items.Add(new Separator());
        Item("Settings…", () => OpenSettings(0));
        menu.IsOpen = true;
    }

    void NewProject()
    {
        var name = Ui.Ask(this, "New project", "Video name:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        Session.New(name.Trim());
        Show(Stage.Record);
    }

    void OpenProject()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "Cutroom projects|*.cutroom.json", InitialDirectory = Session.ProjectsRoot };
        if (d.ShowDialog(this) == true) OpenPath(d.FileName);
    }

    void OpenPath(string path)
    {
        try { Session.Open(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { Say("Could not open: " + ex.Message); }
    }

    async void ImportMedia()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Media|*.mp4;*.mkv;*.mov;*.flv;*.webm;*.wav;*.mp3;*.flac;*.m4a;*.ogg;*.png;*.jpg;*.jpeg;*.webp|All files|*.*" };
        if (d.ShowDialog(this) != true) return;
        Show(Stage.Edit);
        await edit.Commands.DropFiles(d.FileNames, null, Session.Playhead);
    }

    void OpenSettings(int tab)
    {
        new SettingsWindow(Session, tab) { Owner = this }.ShowDialog();
        BuildKeymap();
        UpdateAi();
    }

    public void Show(Stage stage)
    {
        if (stage != Stage.Edit) Session.Stop();
        Current = stage;
        record.Visibility = stage == Stage.Record ? Visibility.Visible : Visibility.Collapsed;
        clean.Visibility = stage == Stage.Clean ? Visibility.Visible : Visibility.Collapsed;
        edit.Visibility = stage == Stage.Edit ? Visibility.Visible : Visibility.Collapsed;
        export.Visibility = stage == Stage.Export ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (k, p) in pills)
        {
            p.Background = k == stage ? Theme.AccentSoft : Brushes.Transparent;
            p.Foreground = k == stage ? Theme.TextBrush : Theme.MutedBrush;
            p.FontWeight = k == stage ? FontWeights.SemiBold : FontWeights.Normal;
            p.BorderThickness = new Thickness(0);
        }
        renderer.Invalidate();
    }

    void UpdateTitle()
    {
        projectName.Text = Session.P.Name;
        Title = $"{Session.P.Name} · Cutroom";
    }

    void UpdateJobs()
    {
        var list = JobQueue.Shared.Snapshot();
        var running = list.Where(j => j.Error is null).ToList();
        jobs.Text = running.Count == 0 ? "" : running.Count == 1 ? $"{running[0].Name} {running[0].Progress:0%}" : $"{running.Count} jobs · {running.Average(j => j.Progress):0%}";
        jobs.ToolTip = list.Count == 0 ? null : string.Join("\n", list.Select(j => $"{j.Name}: {(j.Error ?? j.Progress.ToString("0%"))}"));
    }

    void UpdateAi()
    {
        var available = Session.Ai.Available();
        ai.Text = available.Count == 0 ? "AI off" : "AI: " + available[0].Name;
        ai.ToolTip = available.Count == 0 ? "Install and sign in to Claude Code or Codex to turn on the AI helpers." : "Runs on your subscription through the CLI you're signed into. No API keys.";
    }

    void Say(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            status.Text = message;
            statusClear.Stop();
            statusClear.Start();
        });
    }

    void OnFrame(object? sender, EventArgs e)
    {
        Session.Tick();
        if (Current == Stage.Edit || edit.PopoutOpen) renderer.Render();
    }

    // ---------- Keyboard ----------

    void BuildKeymap()
    {
        // The same key can mean different things per stage (P in Clean vs Edit); Allowed() picks by stage.
        multi = Commands.All.Select(c => (Shortcut.Parse(Session.Settings.KeyFor(c.Id)), c)).Where(x => x.Item1 is not null)
            .GroupBy(x => x.Item1!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.c).ToList());
    }

    Dictionary<Shortcut, List<CommandInfo>> multi = [];

    bool Allowed(CommandInfo c) => c.Group switch
    {
        "General" or "Stages" => true,
        "Record" => Current == Stage.Record,
        "Clean" => Current == Stage.Clean,
        "Playback" => Current == Stage.Edit,
        _ => Current == Stage.Edit,
    };

    void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (!multi.TryGetValue(new Shortcut(key, mods), out var candidates)) return;
        var command = candidates.FirstOrDefault(Allowed);
        if (command is null) return;
        // Typing in a text box keeps its keys, except Ctrl shortcuts that aren't text editing.
        if (Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox { IsEditable: true })
        {
            if (!mods.HasFlag(ModifierKeys.Control) || command.Id is "edit.undo" or "edit.redo") return;
        }
        if (Keyboard.FocusedElement is ButtonBase && key is Key.Space or Key.Enter && command.Group is "General") return;
        e.Handled = true;
        Run(command.Id);
    }

    public void Run(string id)
    {
        var s = Session;
        var c = edit.Commands;
        switch (id)
        {
            case "stage.record": Show(Stage.Record); break;
            case "stage.clean": Show(Stage.Clean); break;
            case "stage.edit": Show(Stage.Edit); break;
            case "stage.export": Show(Stage.Export); break;
            case "file.save": s.SaveSafely(); Say("Saved"); break;
            case "edit.undo": s.Undo(); break;
            case "edit.redo": s.Redo(); break;
            case "play.toggle": s.Toggle(); break;
            case "play.reverse": s.Shuttle(-1); break;
            case "play.stop": s.Stop(); break;
            case "play.forward": s.Shuttle(1); break;
            case "nav.frameBack": s.Step(-Time.Frame); break;
            case "nav.frameForward": s.Step(Time.Frame); break;
            case "nav.secondBack": s.Step(-Time.Rate); break;
            case "nav.secondForward": s.Step(Time.Rate); break;
            case "nav.prevEdit": s.Seek(TimelineOps.PreviousEdit(s.P, s.Playhead)); break;
            case "nav.nextEdit": s.Seek(TimelineOps.NextEdit(s.P, s.Playhead)); break;
            case "nav.start": s.Seek(0); break;
            case "nav.end": s.Seek(s.ShowingSource && s.Source is not null ? s.Source.Duration : s.P.Duration); break;
            case "mark.in": c.MarkIn(); break;
            case "mark.out": c.MarkOut(); break;
            case "edit.blade": c.Blade(); break;
            case "edit.rippleDelete": c.RippleDelete(); break;
            case "edit.delete": c.Delete(); break;
            case "edit.transition": c.AddTransition(); break;
            case "edit.linkToggle": c.ToggleLink(); break;
            case "edit.fade": c.Fade(); break;
            case "view.popout": edit.TogglePopout(); break;
            case "view.zoomIn": edit.Timeline.Zoom(1.25); break;
            case "view.zoomOut": edit.Timeline.Zoom(0.8); break;
            case "slot.next": c.NextEmptySlot(1); break;
            case "slot.prev": c.NextEmptySlot(-1); break;
            case "slot.fill": _ = c.FillSlot(false); break;
            case "slot.fillRange": _ = c.FillSlot(true); break;
            case "slot.slipBack": c.SlipSlot(-1); break;
            case "slot.slipForward": c.SlipSlot(1); break;
            case "slot.applySuggestion": c.ApplySuggestion(text => { edit.ShowTab(0); edit.Footage.SearchFor(text); }); break;
            case "slot.dismissSuggestion": c.DismissSuggestion(); break;
            case "source.tag": edit.Footage.TagCurrent(this); break;
            case "source.moment": edit.Footage.SaveMoment(this); break;
            case "card.blur": c.AddCard(CardTemplateKind.Blur); break;
            case "card.background": c.AddCard(CardTemplateKind.Background); break;
            case "card.revealToggle": c.ToggleReveal(); break;
            case "card.contentSwap": c.SwapContent(); break;
            case "card.retimeTap": c.RetimeTap(); break;
            case "card.aiText": edit.ShowTab(2); edit.Inspector.DraftSelected(); break;
            case "fx.zoom": c.AddEffect(EffectKind.ZoomToBox); break;
            case "fx.freezeOutline": c.AddEffect(EffectKind.FreezeOutline); break;
            case "fx.shake": c.AddEffect(EffectKind.Shake); break;
            case "fx.flash": c.AddEffect(EffectKind.Flash); break;
            case "sub.customRegion": c.SubtitlesOnRange(); break;
            case "sub.split": c.SplitCaption(); break;
            case "sub.merge": c.MergeCaption(); break;
            case "clean.audition": clean.Audition(); break;
            case "clean.toggle": clean.Toggle(); break;
            case "clean.next": clean.Move(1); break;
            case "clean.prev": clean.Move(-1); break;
            case "clean.acceptRest": clean.AcceptRest(); break;
            case "record.toggle": record.ToggleRecord(); break;
            case "record.retake": record.MarkRetake(); break;
        }
    }
}
