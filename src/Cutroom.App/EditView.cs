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
    readonly TextBlock timecode = Ui.Text("", 13);
    readonly TextBlock viewLabel = Ui.Text("", 12, true);
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
        var right = new Border { Background = Theme.PanelBrush, CornerRadius = new CornerRadius(12), Padding = new Thickness(8), Child = tabs };
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

    static Border Pad(UIElement e) => new() { Padding = new Thickness(4, 10, 4, 4), Child = e };

    static void Place(Grid g, UIElement e, int index, bool row = false)
    {
        if (row) SetRow(e, index); else SetColumn(e, index);
        g.Children.Add(e);
    }

    FrameworkElement BuildCenter()
    {
        var transport = Ui.Row(
            Ui.Button("⏮", () => s.Seek(TimelineOps.PreviousEdit(s.P, s.Playhead)), "nav.prevEdit"),
            Ui.Button("◀◀", () => s.Shuttle(-1), "play.reverse"),
            Ui.Button("▶ / ❚❚", s.Toggle, "play.toggle", primary: true),
            Ui.Button("▶▶", () => s.Shuttle(1), "play.forward"),
            Ui.Button("⏭", () => s.Seek(TimelineOps.NextEdit(s.P, s.Playhead)), "nav.nextEdit"));
        transport.Margin = new Thickness(0);
        timecode.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        timecode.Margin = new Thickness(12, 0, 12, 0);
        var toggle = Ui.Button("Timeline / Source", s.ToggleSourceView, tip: "Switch the viewer between the timeline and the footage you picked");
        var pop = Ui.Button("Pop out", TogglePopout, "view.popout", tip: "Show the preview on your other monitor");
        var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        bar.Children.Add(transport);
        bar.Children.Add(timecode);
        bar.Children.Add(viewLabel);
        DockPanel.SetDock(pop, Dock.Right);
        DockPanel.SetDock(toggle, Dock.Right);
        bar.Children.Add(pop);
        bar.Children.Add(toggle);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        dock.Children.Add(bar);
        dock.Children.Add(new Border { Background = System.Windows.Media.Brushes.Black, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = preview });
        return Ui.Card(dock, 10);
    }

    FrameworkElement BuildToolbar()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Add(string label, Action a, string? cmd, string? tip = null) => bar.Children.Add(Ui.Button(label, a, cmd, tip: tip));
        Add("Fill slot", () => _ = Commands.FillSlot(false), "slot.fill", "Put the picked footage into the current slot, trimmed to fit");
        Add("Next empty", () => Commands.NextEmptySlot(1), "slot.next");
        Add("Blur card", () => Commands.AddCard(CardTemplateKind.Blur), "card.blur", "Blur the footage and put text on top");
        Add("Notebook card", () => Commands.AddCard(CardTemplateKind.Background), "card.background", "Cut to the notebook page with text");
        Add("Section", () => Commands.AddCard(CardTemplateKind.SectionHeader), null, "Section header card");
        Add("Title", () => Commands.AddCard(CardTemplateKind.TitleCard), null, "Title card");
        Add("End screen", () => Commands.AddCard(CardTemplateKind.EndScreen), null, "End screen for YouTube end-screen elements");
        Add("Zoom", () => Commands.AddEffect(EffectKind.ZoomToBox), "fx.zoom", "Zoom into a box, then back out");
        Add("Freeze + outline", () => Commands.AddEffect(EffectKind.FreezeOutline), "fx.freezeOutline", "Freeze the frame and circle something");
        Add("Shake", () => Commands.AddEffect(EffectKind.Shake), "fx.shake");
        Add("Flash", () => Commands.AddEffect(EffectKind.Flash), "fx.flash");
        Add("Transition", () => Commands.AddTransition(), "edit.transition", "Fade at the nearest cut (swap the style in the Inspector)");
        Add("Blade", Commands.Blade, "edit.blade");
        Add("Fit timeline", Timeline.ZoomToFit, null);
        return bar;
    }

    void UpdateTimecode()
    {
        if (s.ShowingSource && s.Source is { } src)
        {
            timecode.Text = Time.Timecode(s.SourcePosition);
            viewLabel.Text = $"SOURCE · {src.Name}" + (s.SourceIn is { } i ? $"  in {Time.Timecode(i)}" : "") + (s.SourceOut is { } o ? $"  out {Time.Timecode(o)}" : "");
        }
        else
        {
            timecode.Text = Time.Timecode(s.Playhead);
            var slot = s.CurrentSlot;
            viewLabel.Text = "TIMELINE" + (slot?.Suggestion is { } sug ? $" · AI: {sug.Kind} {sug.ClipDescription}" : "");
        }
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
