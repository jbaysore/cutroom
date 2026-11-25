using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.App;

/// <summary>The Edit stage (mock B): slots left, preview center, Footage / Assets / Inspector right, timeline below.</summary>
public sealed class EditView : Grid
{
    readonly Session s;
    public EditCommands Commands { get; }
    public TimelineView Timeline { get; }
    public LibraryPanel Footage { get; }
    public LibraryPanel Assets { get; }
    public InspectorPanel Inspector { get; }
    readonly TabControl tabs = new();
    readonly PreviewHost preview = new();
    readonly TextBlock timecode = Ui.Text("", 13).Tabular();
    readonly TextBlock viewTitle = Ui.Header("Timeline preview");
    readonly TextBlock viewLabel = Ui.Text("", 12, true);
    Button timelinePill = null!, sourcePill = null!;
    readonly PreviewRenderer renderer;
    Window? popout;

    public EditView(Session session, PreviewRenderer renderer)
    {
        s = session;
        this.renderer = renderer;
        Commands = new EditCommands(s);
        Timeline = new TimelineView(s);
        Footage = new LibraryPanel(s, LibraryRole.Footage);
        Assets = new LibraryPanel(s, LibraryRole.Asset);
        Inspector = new InspectorPanel(s, Commands);
        renderer.Attach(preview);
        preview.Clicked += pt =>
        {
            // Map the click into frame coordinates (the frame is letterboxed inside the host).
            double w = preview.ActualWidth, h = preview.ActualHeight, fw = w, fh = w * 9 / 16;
            if (fh > h) { fh = h; fw = h * 16 / 9; }
            double fx = (pt.X - (w - fw) / 2) / fw, fy = (pt.Y - (h - fh) / 2) / fh;
            if (fx is >= 0 and <= 1 && fy is >= 0 and <= 1) Commands.PreviewClicked(fx, fy);
        };

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(62, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(38, GridUnitType.Star), MinHeight = 200 });

        var slots = new SlotsPanel(s);
        var center = BuildCenter();
        tabs.Items.Add(new TabItem { Header = "Footage", Content = Pad(Footage) });
        tabs.Items.Add(new TabItem { Header = "Assets", Content = Pad(Assets) });
        tabs.Items.Add(new TabItem { Header = "Inspector", Content = Pad(Inspector) });
        var right = new Border { Background = Theme.PanelBrush, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 12, 10, 10), Child = tabs };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22, GridUnitType.Star), MinWidth = 220 });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24, GridUnitType.Star), MinWidth = 280 });
        Place(top, slots, 0); Place(top, center, 2); Place(top, right, 4);
        Place(this, top, 0, row: true);
        var toolbar = BuildToolbar();
        Place(this, toolbar, 2, row: true);
        var timelineCard = new Border { Background = Theme.PanelBrush, CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = Timeline };
        Place(this, timelineCard, 3, row: true);

        Timeline.FilesDropped += (files, track, at) => _ = Commands.DropFiles(files, track, at);
        s.SelectionChanged += () => { if (s.SelectedClipId is not null || s.SelectedRegionId is not null) tabs.SelectedIndex = 2; };
        s.PlayheadChanged += UpdateTimecode;
        s.SourceChanged += UpdateTimecode;
        UpdateTimecode();
    }

    static Border Pad(UIElement e) => new() { Padding = new Thickness(4, 0, 4, 4), Child = e };

    static void Place(Grid g, UIElement e, int index, bool row = false)
    {
        if (row) SetRow(e, index); else SetColumn(e, index);
        g.Children.Add(e);
    }

    FrameworkElement BuildCenter()
    {
        // Header: what the viewer shows, with context (current slot, or the source clip and its marks) on the right.
        viewLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        viewLabel.TextWrapping = TextWrapping.NoWrap;
        viewLabel.TextAlignment = TextAlignment.Right;
        viewLabel.Margin = new Thickness(16, 0, 2, 0);
        var header = Ui.Columns((viewTitle, GridLength.Auto), (viewLabel, new GridLength(1, GridUnitType.Star)));
        header.Margin = new Thickness(4, 0, 0, 10);

        // Transport: timecode left, round buttons centered, view switch and pop-out right (mock B).
        Button Round(string glyph, Action a, string cmd, bool play = false)
        {
            var b = Ui.Button(glyph, a, cmd, kind: play ? ButtonKind.IconPrimary : ButtonKind.Icon);
            b.Margin = new Thickness(3, 0, 3, 0);
            b.FontSize = play ? 15 : 12;
            return b;
        }
        var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        transport.Children.Add(Round("⏮", () => s.Seek(TimelineOps.PreviousEdit(s.P, s.Playhead)), "nav.prevEdit"));
        transport.Children.Add(Round("◀◀", () => s.Shuttle(-1), "play.reverse"));
        transport.Children.Add(Round("▶", s.Toggle, "play.toggle", play: true));
        transport.Children.Add(Round("▶▶", () => s.Shuttle(1), "play.forward"));
        transport.Children.Add(Round("⏭", () => s.Seek(TimelineOps.NextEdit(s.P, s.Playhead)), "nav.nextEdit"));
        timecode.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        timecode.Margin = new Thickness(6, 0, 0, 0);
        timelinePill = Ui.Button("Timeline", () => { if (s.ShowingSource) s.ToggleSourceView(); }, tip: "Show the edited timeline", kind: ButtonKind.Pill);
        sourcePill = Ui.Button("Source", () => { if (!s.ShowingSource) s.ToggleSourceView(); }, tip: "Show the footage you picked in the library", kind: ButtonKind.Pill);
        timelinePill.Padding = sourcePill.Padding = new Thickness(12, 3, 12, 3);
        timelinePill.MinHeight = sourcePill.MinHeight = 26;
        var view = Ui.Segmented(timelinePill, sourcePill);
        view.Background = Theme.Panel2Brush;
        var pop = Ui.Button("⧉", TogglePopout, "view.popout", tip: "Show the preview on your other monitor", kind: ButtonKind.Icon);
        pop.Margin = new Thickness(8, 0, 0, 0);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Children = { view, pop } };
        var bar = Ui.Columns((timecode, new GridLength(1, GridUnitType.Star)), (transport, GridLength.Auto), (tools, new GridLength(1, GridUnitType.Star)));
        bar.Margin = new Thickness(0, 10, 0, 0);

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(bar, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(bar);
        dock.Children.Add(new Border { Background = System.Windows.Media.Brushes.Black, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = preview });
        return Ui.Card(dock);
    }

    FrameworkElement BuildToolbar()
    {
        Button Tool(string label, Action a, string? cmd, string? tip = null) => Ui.Button(label, a, cmd, tip: tip, kind: ButtonKind.Ghost);
        var groups = Ui.Toolbar(
            [
                Tool("Fill slot", () => _ = Commands.FillSlot(false), "slot.fill", "Put the picked footage into the current slot, trimmed to fit"),
                Tool("Next empty", () => Commands.NextEmptySlot(1), "slot.next"),
            ],
            [
                Tool("Blur card", () => Commands.AddCard(CardTemplateKind.Blur), "card.blur", "Blur the footage and put text on top"),
                Tool("Notebook card", () => Commands.AddCard(CardTemplateKind.Background), "card.background", "Cut to the notebook page with text"),
                Tool("Section", () => Commands.AddCard(CardTemplateKind.SectionHeader), null, "Section header card"),
                Tool("Title", () => Commands.AddCard(CardTemplateKind.TitleCard), null, "Title card"),
                Tool("End screen", () => Commands.AddCard(CardTemplateKind.EndScreen), null, "End screen for YouTube end-screen elements"),
            ],
            [
                Tool("Zoom", () => Commands.AddEffect(EffectKind.ZoomToBox), "fx.zoom", "Zoom into a box, then back out"),
                Tool("Freeze + outline", () => Commands.AddEffect(EffectKind.FreezeOutline), "fx.freezeOutline", "Freeze the frame and circle something"),
                Tool("Shake", () => Commands.AddEffect(EffectKind.Shake), "fx.shake"),
                Tool("Flash", () => Commands.AddEffect(EffectKind.Flash), "fx.flash"),
            ],
            [
                Tool("Transition", () => Commands.AddTransition(), "edit.transition", "Fade at the nearest cut (swap the style in the Inspector)"),
                Tool("Blade", Commands.Blade, "edit.blade"),
            ]);
        var fit = Ui.Toolbar([Tool("Fit timeline", Timeline.ZoomToFit, null, "Show the whole timeline")]);
        fit.Children[0].SetValue(MarginProperty, new Thickness(8, 0, 0, 8));
        var dock = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(fit, Dock.Right);
        dock.Children.Add(fit);
        dock.Children.Add(groups);
        return dock;
    }

    void UpdateTimecode()
    {
        if (s.ShowingSource && s.Source is { } src)
        {
            timecode.Text = Time.Timecode(s.SourcePosition);
            viewTitle.Text = "Source";
            viewLabel.Text = src.Name + (s.SourceIn is { } i ? $"  ·  in {Time.Timecode(i)}" : "") + (s.SourceOut is { } o ? $"  ·  out {Time.Timecode(o)}" : "");
        }
        else
        {
            timecode.Text = Time.Timecode(s.Playhead);
            viewTitle.Text = "Timeline preview";
            var slot = s.CurrentSlot;
            viewLabel.Text = slot?.Suggestion is { } sug ? $"AI · {sug.Kind} {sug.ClipDescription}" : slot is null ? "" : slot.Text;
        }
        Ui.SetPill(timelinePill, !s.ShowingSource);
        Ui.SetPill(sourcePill, s.ShowingSource);
    }

    public bool PopoutOpen => popout is not null;

    public void ShowTab(int index) => tabs.SelectedIndex = index;

    public void TogglePopout()
    {
        if (popout is not null) { popout.Close(); return; }
        var host = new PreviewHost();
        renderer.Attach(host);
        popout = new Window { Title = "Cutroom preview", Width = 1280, Height = 760, Content = host, Background = System.Windows.Media.Brushes.Black };
        // Prefer the monitor the main window is not on.
        var main = Window.GetWindow(this)!;
        double left = main.Left + main.ActualWidth / 2 < SystemParameters.PrimaryScreenWidth ? SystemParameters.PrimaryScreenWidth + 40 : 40;
        if (SystemParameters.VirtualScreenWidth > SystemParameters.PrimaryScreenWidth + 100) { popout.Left = left; popout.Top = 60; popout.WindowStartupLocation = WindowStartupLocation.Manual; }
        popout.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) popout.WindowState = popout.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else main.RaiseEvent(new KeyEventArgs(e.KeyboardDevice, e.InputSource, e.Timestamp, e.Key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        };
        popout.Closed += (_, _) => popout = null;
        popout.Show();
        renderer.Invalidate();
    }
}
