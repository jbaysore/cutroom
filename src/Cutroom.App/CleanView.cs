using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cutroom.Audio;
using Cutroom.Core;
using Cutroom.Speech;

namespace Cutroom.App;

/// <summary>
/// Clean stage: one button runs the cleanup; every decision lands in a review list with the least certain first.
/// Enter plays before/after, X toggles, N/P move, A accepts the rest.
/// </summary>
public sealed class CleanView : Grid
{
    readonly Session s;
    readonly ListBox list = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    readonly TextBlock summary = Ui.Text("", 13);
    readonly TextBlock stage = Ui.Text("", 12, true);
    readonly ProgressBar progress = new() { Height = 6, Maximum = 1, Margin = new Thickness(0, 6, 0, 6), Visibility = Visibility.Collapsed };
    readonly Button run;
    readonly Button cancel;
    readonly HashSet<string> reviewed = [];
    List<CleanupDecision> ordered = [];
    int renderedRevision = -1;

    public event Action? GoToEdit;

    public CleanView(Session session)
    {
        s = session;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32, GridUnitType.Star), MinWidth = 300 });

        run = Ui.Button("Clean narration", () => _ = Run(), primary: true, tip: "Remove dead air, breaths, mouth noises and retakes. Everything is reviewable.");
        cancel = Ui.Button("Cancel", () => s.CleanupCancel?.Cancel());
        cancel.Visibility = Visibility.Collapsed;
        var keys = Ui.Text("Enter: hear before/after   X: undo or redo this cut   N / P: next / previous   A: accept the rest", 12, true);
        var top = Ui.Stack(Ui.Row(run, cancel), progress, stage, summary, keys);
        top.Margin = new Thickness(0, 0, 0, 10);
        var left = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);
        left.Children.Add(list);
        var leftCard = Ui.Card(left, 16);
        SetColumn(leftCard, 0);
        Children.Add(leftCard);

        var right = Ui.Card(Ui.Scroll(BuildSettings()), 16);
        SetColumn(right, 2);
        Children.Add(right);

        list.MouseDoubleClick += (_, _) => Audition();
        s.ProjectChanged += Refresh;
        s.DocumentReplaced += () => { reviewed.Clear(); Refresh(); };
        Loaded += (_, _) => Refresh();
    }

    FrameworkElement BuildSettings()
    {
        var n = s.P.Narration.Settings;
        void Set(string label, Action<CleanupSettings> change) => s.Edit(label, p => change(p.Narration.Settings));
        return Ui.Stack(
            Ui.Header("Pacing"),
            Ui.Text("Tight but natural by default. Changing these re-plans instantly after the first run.", 12, true),
            new Border { Height = 10 },
            Ui.Slider("Pause between sentences (ms)", 100, 500, n.SentenceGapMs, v => { Set("Sentence pause", x => x.SentenceGapMs = v); _ = Replan(); }, "0", 10),
            Ui.Slider("Pause at paragraph breaks (ms)", 150, 900, n.ParagraphGapMs, v => { Set("Paragraph pause", x => x.ParagraphGapMs = v); _ = Replan(); }, "0", 10),
            Ui.Slider("Pause at commas (ms)", 40, 300, n.CommaGapMs, v => { Set("Comma pause", x => x.CommaGapMs = v); _ = Replan(); }, "0", 10),
            Ui.Header("Sound"),
            new Border { Height = 6 },
            Ui.Text("Background noise removal", 12, true),
            Ui.Combo(Enum.GetValues<Denoiser>(), n.Denoiser, d => d switch { Denoiser.DeepFilterNet => "DeepFilterNet (best)", Denoiser.RNNoise => "RNNoise (lighter)", _ => "Off" },
                d => { Set("Denoiser", x => x.Denoiser = d); s.Settings.Denoiser = d; s.Settings.Save(); s.Say("Run the cleanup again to apply the new noise removal."); }),
            Ui.Text($"Narration is leveled to {n.TargetLufs} LUFS; the final mix is leveled to −14 LUFS on export.", 12, true),
            new Border { Height = 16 },
            Ui.Row(Ui.Button("Next: edit →", () => GoToEdit?.Invoke(), primary: true)));
    }

    async Task Run()
    {
        if (s.P.FindMedia(s.P.Narration.MediaId) is null) { s.Say("Record or import a narration first."); return; }
        if (s.P.Script is null) s.Say("Tip: load your script in Record for retake detection and correct spellings. Continuing without it.");
        run.IsEnabled = false;
        cancel.Visibility = progress.Visibility = Visibility.Visible;
        try
        {
            await s.RunCleanup(new Progress<CleanupStage>(st => { stage.Text = st.Name + "…"; progress.Value = st.Progress; }));
            reviewed.Clear();
            stage.Text = "Done. Review the list: least certain decisions are at the top.";
        }
        catch (OperationCanceledException) { stage.Text = "Cancelled."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { stage.Text = "Cleanup failed: " + ex.Message; }
        finally
        {
            run.IsEnabled = true;
            cancel.Visibility = progress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Re-plans from cached transcription with the current pacing settings (keeps manual toggles where decisions match).</summary>
    async Task Replan()
    {
        if (!s.P.Narration.Processed) return;
        await Run();
    }

    void Refresh()
    {
        var n = s.P.Narration;
        run.Content = n.Processed ? "Run cleanup again" : "Clean narration";
        if (s.Doc.Revision == renderedRevision) return;
        renderedRevision = s.Doc.Revision;
        int selected = list.SelectedIndex;
        ordered = n.Decisions.OrderBy(d => reviewed.Contains(d.Id)).ThenBy(d => d.Confidence).ThenBy(d => d.Start).ToList();
        list.Items.Clear();
        foreach (var d in ordered) list.Items.Add(new ListBoxItem { Content = Row(d), Padding = new Thickness(6, 4, 6, 4) });
        if (ordered.Count > 0) list.SelectedIndex = Math.Clamp(selected, 0, ordered.Count - 1);
        if (!n.Processed) { summary.Text = "Nothing cleaned yet."; return; }
        long removed = n.SourceDuration - n.KeptSpans().Sum(x => x.Duration);
        summary.Text = $"Removed {Format(removed)} of {Format(n.SourceDuration)} · {n.Decisions.Count(d => d.Removes)} cuts · " +
                       $"{n.Decisions.Count(d => d.Removes && d.Kind is DecisionKind.Retake or DecisionKind.FalseStart)} retakes and false starts · " +
                       $"{n.Decisions.Count(d => d.Kind is DecisionKind.AdLibKept or DecisionKind.PossibleSpeechKept)} kept for you to check · {reviewed.Count} reviewed";
    }

    static string Format(long ticks) => TimeSpan.FromSeconds(Time.ToSeconds(ticks)).ToString(@"m\:ss");

    FrameworkElement Row(CleanupDecision d)
    {
        var (label, color) = d.Kind switch
        {
            DecisionKind.Retake => ("Retake", Theme.Card),
            DecisionKind.FalseStart => ("False start", Theme.Card),
            DecisionKind.AdLibKept => ("Ad-lib kept", Theme.Sub),
            DecisionKind.PossibleSpeechKept => ("Possible speech kept", Theme.Sub),
            DecisionKind.Breath => ("Breath", Theme.Game),
            DecisionKind.MouthNoise => ("Mouth noise", Theme.Game),
            _ => ("Pause", Theme.Muted),
        };
        var badge = new Border { Background = Theme.B(color, 60), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 10, 0), Width = 150,
            Child = new TextBlock { Text = label, Foreground = Theme.B(color) } };
        string state = d.Removes ? "CUT" : "KEPT";
        var info = Ui.Stack(
            new TextBlock { Text = d.Context.Length > 0 ? d.Context : d.Reason, Foreground = Theme.TextBrush, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = $"{Time.Timecode(d.Start)} · {d.Reason}", Foreground = Theme.MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var right = Ui.Stack(new TextBlock { Text = state, Foreground = d.Removes ? Theme.DangerBrush : Theme.OkBrush, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right },
            new TextBlock { Text = $"{d.Confidence:0%} sure" + (reviewed.Contains(d.Id) ? " · ✓" : ""), Foreground = Theme.MutedBrush, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right });
        return Ui.Columns((badge, GridLength.Auto), (info, new GridLength(1, GridUnitType.Star)), (right, new GridLength(90)));
    }

    CleanupDecision? Selected => list.SelectedIndex >= 0 && list.SelectedIndex < ordered.Count ? ordered[list.SelectedIndex] : null;

    public void Move(int delta)
    {
        if (ordered.Count == 0) return;
        if (Selected is { } d) reviewed.Add(d.Id);
        list.SelectedIndex = Math.Clamp(list.SelectedIndex + delta, 0, ordered.Count - 1);
        list.ScrollIntoView(list.SelectedItem);
    }

    public void Toggle()
    {
        if (Selected is not { } d) return;
        var id = d.Id;
        reviewed.Add(id);
        s.Edit(d.Removes ? "Keep this part" : "Cut this part", p =>
        {
            var dec = p.Narration.Decisions.First(x => x.Id == id);
            dec.Applied = !dec.Applied;
            s.AfterNarrationChange(p);
        });
    }

    public void AcceptRest()
    {
        foreach (var d in ordered) reviewed.Add(d.Id);
        renderedRevision = -1;
        Refresh();
        s.Say("All decisions accepted.");
    }

    /// <summary>Plays ~1.5 s around the decision as recorded, then with the decision applied.</summary>
    public void Audition()
    {
        if (Selected is not { } d) return;
        var media = s.P.FindMedia(s.P.Narration.MediaId);
        var path = media is null ? null : s.AudioPathFor(media);
        if (path is null) { s.Say("Audio is still being prepared."); return; }
        reviewed.Add(d.Id);
        using var source = new AudioSource(path);
        long pad = Time.FromSeconds(1.5);
        long a = Math.Max(0, d.Start - pad), b = d.End + pad;
        float gain = (float)Gain.DbToLinear(s.P.Narration.GainDb);
        var before = Read(source, a, b, gain);
        var after = Read(source, a, d.Start, gain).Concat(Read(source, d.End, b, gain)).ToArray();
        var gap = new float[(int)(Time.FromMs(450) * 2)];
        bool cutNow = d.Removes;
        // Always play "as recorded" first, then "cleaned".
        var cleaned = cutNow ? after : before;
        var recorded = cutNow ? before : after;
        s.Stop();
        s.Player.PlayBuffer([.. recorded, .. gap, .. cleaned]);
        s.Say(cutNow ? "Playing: as recorded, then with this cut." : "Playing: with this part removed, then as kept.");
    }

    static float[] Read(AudioSource source, long a, long b, float gain)
    {
        if (b <= a) return [];
        var buffer = new float[(b - a) * 2];
        source.Read(a, buffer);
        for (int i = 0; i < buffer.Length; i++) buffer[i] *= gain;
        return buffer;
    }
}
