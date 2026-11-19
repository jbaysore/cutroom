using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cutroom.Core;

namespace Cutroom.App;

/// <summary>Script slots: the narration split by sentence (by phrase in the intro), with fill status and AI chips.</summary>
public sealed class SlotsPanel : Border
{
    readonly Session s;
    readonly StackPanel list = new();
    readonly TextBlock summary = Ui.Text("", 12, true);
    readonly ScrollViewer scroll;
    string? current;
    int renderedRevision = -1;

    public SlotsPanel(Session session)
    {
        s = session;
        Background = Theme.PanelBrush;
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(10);
        scroll = Ui.Scroll(list);
        var header = Ui.Columns((Ui.Header("Script slots"), new GridLength(1, GridUnitType.Star)), (summary, GridLength.Auto));
        header.Margin = new Thickness(4, 2, 4, 8);
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(scroll);
        Child = dock;
        s.ProjectChanged += Refresh;
        s.PlayheadChanged += Highlight;
        Loaded += (_, _) => Refresh();
    }

    void Refresh()
    {
        if (s.Doc.Revision == renderedRevision && list.Children.Count > 0) { Highlight(); return; }
        renderedRevision = s.Doc.Revision;
        list.Children.Clear();
        var p = s.P;
        if (p.Slots.Count == 0)
        {
            list.Children.Add(Ui.Text("Slots appear here after the narration is cleaned. Each line of your script becomes a slot you fill with footage or a card.", 12, true));
            summary.Text = "";
            return;
        }
        string? section = "\u0001";
        int filled = 0;
        foreach (var slot in p.Slots)
        {
            var title = slot.IsIntro ? "Intro" : slot.SectionTitle ?? "";
            if (title != section)
            {
                section = title;
                if (title.Length > 0)
                    list.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 10.5, Foreground = Theme.MutedBrush, Margin = new Thickness(6, 12, 0, 4) });
            }
            var status = Derive.StatusOf(p, slot);
            if (status != SlotStatus.Empty) filled++;
            var color = status switch { SlotStatus.Footage => Theme.Footage, SlotStatus.Card => Theme.Card, _ => Theme.Muted };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = slot.Text, TextWrapping = TextWrapping.Wrap, Foreground = status == SlotStatus.Empty ? Theme.MutedBrush : Theme.TextBrush });
            if (slot.Suggestion is { } sug)
            {
                var chip = new Border
                {
                    BorderBrush = Theme.MutedBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1, 8, 2),
                    Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = "AI · " + Describe(sug), FontSize = 11, Foreground = Theme.MutedBrush, TextWrapping = TextWrapping.Wrap },
                    ToolTip = sug.Reason + "\nCtrl+Enter applies, Ctrl+Backspace dismisses.",
                };
                text.Children.Add(chip);
            }
            var duration = Ui.Text(Time.Short(slot.End - slot.Start), 11, true);
            duration.VerticalAlignment = VerticalAlignment.Top;
            var row = Ui.Columns((Ui.Dot(color, status != SlotStatus.Empty), GridLength.Auto), (text, new GridLength(1, GridUnitType.Star)), (duration, GridLength.Auto));
            var border = new Border { Child = row, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(8), Tag = slot.Id, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2) };
            var start = slot.Start;
            border.MouseLeftButtonDown += (_, _) => { s.ShowingSource = false; s.Seek(start); };
            list.Children.Add(border);
        }
        summary.Text = $"{filled} / {p.Slots.Count} filled";
        Highlight();
    }

    static string Describe(VisualSuggestion s) => s.Kind switch
    {
        "clip" => "clip: " + s.ClipDescription,
        "textCard" => "text card",
        "backgroundCard" => "notebook card",
        "zoom" => "zoom: " + s.Reason,
        "freezeOutline" => "freeze + outline: " + s.Reason,
        "sectionHeader" => "section header",
        _ => s.Kind,
    };

    void Highlight()
    {
        var slot = s.CurrentSlot;
        if (slot?.Id == current) return;
        current = slot?.Id;
        foreach (var child in list.Children.OfType<Border>())
        {
            bool on = (string?)child.Tag == current && current is not null;
            child.Background = on ? Theme.AccentSoft : Brushes.Transparent;
            if (on) child.BringIntoView();
        }
    }
}
