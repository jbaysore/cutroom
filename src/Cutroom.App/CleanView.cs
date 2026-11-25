using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Cutroom.Audio;
using Cutroom.Core;
using Cutroom.Speech;

namespace Cutroom.App;

public sealed class CleanView : Grid, IDisposable
{
    readonly Session s;
    readonly Player player = new();
    readonly Mixer mixer;
    readonly ListBox list = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    readonly TextBlock summary = Ui.Text("", 14);
    readonly TextBlock stage = Ui.Text("", 12, true);
    readonly TextBlock time = Ui.Text("", 13, true);
    readonly TextBlock passage = Ui.Text("", 18);
    readonly TextBlock details = Ui.Text("", 16);
    readonly TextBlock reason = Ui.Text("", 12, true);
    readonly TextBlock playbackStatus = Ui.Text("", 12, true);
    readonly ProgressBar progress = new() { Height = 6, Maximum = 1, Margin = new Thickness(0, 6, 0, 6), Visibility = Visibility.Collapsed };
    readonly Slider seek = new()
    {
        IsMoveToPointEnabled = true, Minimum = 0, SmallChange = Time.FromSeconds(1), LargeChange = Time.FromSeconds(5),
        Margin = new Thickness(0, 2, 0, 0),
    };
    readonly Canvas markers = new() { Height = 22, ClipToBounds = true };
    readonly ContentControl settings = new();
    readonly Button run, cancel, play, review, all, sort, original, cut, toggle, scriptButton;
    readonly FrameworkElement listen, reviewPane, decisionActions;
    readonly Border scriptPane;
    readonly ColumnDefinition scriptGapColumn, scriptColumn;
    readonly FlowDocumentScrollViewer scriptReader = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        IsToolBarVisible = false,
    };
    CleanPreview? preview;
    List<CleanupDecision> visible = [];
    long position, playbackEnd;
    string? playbackPath;
    int renderedRevision = -1;
    bool allDecisions, scriptShown, updatingSeek, dragging, resumeAfterDrag, playingTake;
    (CleanPreview Preview, TimeRange Range)? nextAudition;

    public event Action? GoToEdit;

    public CleanView(Session session)
    {
        s = session;
        mixer = new Mixer(_ => playbackPath);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });

        run = Ui.Button("Clean narration", () => _ = Run());
        cancel = Ui.Button("Cancel", () => s.CleanupCancel?.Cancel());
        cancel.Visibility = Visibility.Collapsed;
        play = Ui.Button("Play cleaned narration", TogglePlayback, "clean.play", primary: true, tip: "Play or pause cleaned narration");
        review = Ui.Button("Review cuts", () => SetView(false));
        all = Ui.Button("All decisions", () => SetView(true));
        sort = Ui.Button("", ToggleSort, tip: "Order the list by time, or with the decisions cleanup was least sure about first");
        sort.Name = "ReviewSort";
        original = Ui.Button("Hear original", () => Audition(false), tip: "Hear the selected region with this cut lifted");
        cut = Ui.Button("Hear cut", () => Audition(true), tip: "Hear the selected region with this cut applied");
        toggle = Ui.Button("Restore audio", Toggle, "clean.toggle", tip: "Toggle the selected decision");
        scriptButton = Ui.Button("Show script", ToggleScript, tip: "Show or hide the loaded script while listening");
        decisionActions = Ui.Row(original, cut, toggle);
        var next = Ui.Button("Continue to Edit →", () => GoToEdit?.Invoke());
        var transport = Ui.Row(play, time);
        transport.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetName(seek, "Cleaned narration position");
        passage.MaxHeight = 90;
        passage.TextTrimming = TextTrimming.CharacterEllipsis;
        passage.Margin = new Thickness(0, 6, 0, 6);
        listen = Ui.Card(Ui.Stack(Ui.Header("Cleaned narration"), summary, transport, seek, markers, passage, playbackStatus));

        var reviewTop = Ui.Stack(Ui.Row(review, all, sort), details, reason, decisionActions);
        reviewTop.Margin = new Thickness(0, 0, 0, 8);
        reason.Margin = new Thickness(0, 4, 0, 8);
        details.MaxHeight = 85;
        details.TextTrimming = TextTrimming.CharacterEllipsis;
        var reviewPanel = new DockPanel();
        DockPanel.SetDock(reviewTop, Dock.Top);
        reviewPanel.Children.Add(reviewTop);
        reviewPanel.Children.Add(list);
        reviewPane = Ui.Card(reviewPanel);
        reviewPane.Margin = new Thickness(0, 8, 0, 0);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = Ui.Stack(Ui.Row(run, cancel, scriptButton, next), progress, stage);
        header.Margin = new Thickness(0, 0, 0, 8);
        layout.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scriptGapColumn = new ColumnDefinition { Width = new GridLength(0) };
        scriptColumn = new ColumnDefinition { Width = new GridLength(0) };
        body.ColumnDefinitions.Add(scriptGapColumn);
        body.ColumnDefinitions.Add(scriptColumn);
        var main = new DockPanel();
        DockPanel.SetDock(listen, Dock.Top);
        main.Children.Add(listen);
        main.Children.Add(reviewPane);
        body.Children.Add(main);

        var scriptContent = new DockPanel();
        var scriptHeader = Ui.Row(Ui.Header("Script"));
        DockPanel.SetDock(scriptHeader, Dock.Top);
        scriptContent.Children.Add(scriptHeader);
        scriptContent.Children.Add(scriptReader);
        scriptPane = Ui.Card(scriptContent);
        scriptPane.Visibility = Visibility.Collapsed;
        Grid.SetColumn(scriptPane, 2);
        body.Children.Add(scriptPane);
        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        Children.Add(layout);

        var side = Ui.Card(Ui.Scroll(settings));
        SetColumn(side, 2);
        Children.Add(side);
        settings.Content = BuildSettings();

        seek.ValueChanged += (_, e) =>
        {
            if (!updatingSeek) Seek((long)e.NewValue, playingTake);
        };
        seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
        {
            dragging = true;
            resumeAfterDrag = playingTake;
            StopPlayback();
        }));
        seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            dragging = false;
            if (resumeAfterDrag && IsVisible && !Busy && preview is { } take && position < take.Duration)
                StartPlayback(take, new(position, take.Duration), true, "");
        }));
        markers.SizeChanged += (_, _) => DrawMarkers();
        list.SelectionChanged += (_, _) => UpdateDecision();
        list.MouseDoubleClick += (_, _) => Audition();
        s.ProjectChanged += Refresh;
        s.DocumentReplaced += Reset;
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        Loaded += (_, _) => Refresh();
    }

    bool Busy => s.CleanupCancel is not null || !run.IsEnabled;
    CleanupDecision? Selected => (list.SelectedItem as ListBoxItem)?.Tag as CleanupDecision;

    FrameworkElement BuildSettings()
    {
        var n = s.P.Narration.Settings;
        void Set(string label, Action<CleanupSettings> change) => s.Edit(label, p => change(p.Narration.Settings));
        void Pace(string label, double value, Func<CleanupSettings, double> get, Action<CleanupSettings, double> set)
        {
            if (Busy || value == get(s.P.Narration.Settings)) return;
            Set(label, x => set(x, value));
            if (s.P.Narration.Processed) _ = Run();
        }
        return Ui.Stack(
            Ui.Header("Pacing"),
            Ui.Text("Tight but natural by default.", 12, true),
            new Border { Height = 10 },
            Ui.Slider("Pause between sentences (ms)", 100, 500, n.SentenceGapMs, v => Pace("Sentence pause", v, x => x.SentenceGapMs, (x, value) => x.SentenceGapMs = value), "0", 10),
            Ui.Slider("Pause at paragraph breaks (ms)", 150, 900, n.ParagraphGapMs, v => Pace("Paragraph pause", v, x => x.ParagraphGapMs, (x, value) => x.ParagraphGapMs = value), "0", 10),
            Ui.Slider("Pause at commas (ms)", 40, 300, n.CommaGapMs, v => Pace("Comma pause", v, x => x.CommaGapMs, (x, value) => x.CommaGapMs = value), "0", 10),
            Ui.Header("Sound"),
            new Border { Height = 6 },
            Ui.Text("Background noise removal", 12, true),
            Ui.Combo(Enum.GetValues<Denoiser>(), n.Denoiser, d => d switch { Denoiser.DeepFilterNet => "DeepFilterNet", Denoiser.RNNoise => "RNNoise", _ => "Off" },
                d => { Set("Denoiser", x => x.Denoiser = d); s.Settings.Denoiser = d; s.Settings.Save(); s.Say("Run cleanup again to apply the new noise removal."); }),
            Ui.Text($"Narration is leveled to {n.TargetLufs} LUFS. The final mix is leveled to −14 LUFS on export.", 12, true));
    }

    async Task Run()
    {
        if (Busy) return;
        if (s.P.FindMedia(s.P.Narration.MediaId) is null) { s.Say("Record or import narration first."); return; }
        if (s.P.Script is null) s.Say("Load your script in Record to help match retakes and spellings.");
        StopPlayback();
        run.IsEnabled = settings.IsEnabled = false;
        cancel.Visibility = progress.Visibility = Visibility.Visible;
        stage.Text = "Preparing narration…";
        progress.Value = 0;
        UpdateControls();
        var document = s.Doc;
        try
        {
            await s.RunCleanup(new Progress<CleanupStage>(st =>
            {
                if (document != s.Doc || run.IsEnabled) return;
                stage.Text = st.Name + "…";
                progress.Value = st.Progress;
            }));
            if (document == s.Doc)
            {
                StopPlayback();
                position = 0;
                stage.Text = "";
                SetView(false);
                UpdatePosition();
            }
        }
        catch (OperationCanceledException) { if (document == s.Doc) stage.Text = "Cleanup cancelled."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (document == s.Doc) stage.Text = "Cleanup failed: " + ex.Message; }
        finally
        {
            run.IsEnabled = settings.IsEnabled = true;
            cancel.Visibility = progress.Visibility = Visibility.Collapsed;
            UpdateControls();
        }
    }

    void Reset()
    {
        StopPlayback();
        renderedRevision = -1;
        preview = null;
        position = 0;
        stage.Text = "";
        allDecisions = false;
        list.Items.Clear();
        settings.Content = BuildSettings();
        Refresh();
    }

    void Refresh()
    {
        if (!IsVisible) return;
        var n = s.P.Narration;
        if (s.Doc.Revision == renderedRevision) return;
        renderedRevision = s.Doc.Revision;
        RefreshScript();
        long? sourcePosition = preview?.Map.ToSource(position);
        StopPlayback();
        run.Content = n.Processed ? "Run cleanup again" : "Clean narration";
        preview = n.Processed ? new CleanPreview(s.P) : null;
        listen.Visibility = reviewPane.Visibility = n.Processed ? Visibility.Visible : Visibility.Collapsed;
        if (!n.Processed && !Busy)
            stage.Text = n.MediaId is null ? "Record or import narration to get started." : "Clean the narration to trim pauses, mouth noise, and retakes.";
        if (preview is { } take)
        {
            int speechCuts = n.Decisions.Count(d => IsSpeechCut(d) && d.Removes);
            long removed = Math.Max(0, n.SourceDuration - take.Duration);
            string saved = removed < Time.FromSeconds(60) ? Time.Short(removed) : Format(removed);
            summary.Text = $"{saved} removed  ·  {speechCuts} retake or false-start {(speechCuts == 1 ? "cut" : "cuts")}";
            position = sourcePosition is { } source ? take.Map.ToTimelineNearest(source) : Math.Min(position, take.Duration);
        }
        else position = 0;
        PopulateList(Selected?.Id);
        DrawMarkers();
        UpdatePosition();
        UpdateControls();
    }

    void ToggleScript()
    {
        scriptShown = !scriptShown;
        scriptButton.Content = scriptShown ? "Hide script" : "Show script";
        scriptButton.Background = scriptShown ? Theme.AccentSoft : Brushes.Transparent;
        scriptGapColumn.Width = new GridLength(scriptShown ? 8 : 0);
        scriptColumn.Width = new GridLength(scriptShown ? 320 : 0);
        scriptPane.Visibility = scriptShown ? Visibility.Visible : Visibility.Collapsed;
    }

    void RefreshScript()
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            Foreground = Theme.TextBrush,
            Background = Theme.PanelBrush,
            PagePadding = new Thickness(4),
            LineHeight = 25,
        };
        var script = s.P.Script;
        if (script is null || !script.Sentences.Any())
        {
            doc.Blocks.Add(new Paragraph(new Run("Load a script in Record to compare it while listening."))
            {
                Foreground = Theme.MutedBrush,
                FontSize = 14,
            });
        }
        else
        {
            foreach (var section in script.Sections)
            {
                if (section.Heading is not null)
                    doc.Blocks.Add(new Paragraph(new Run(section.Heading))
                    {
                        FontWeight = FontWeights.Bold,
                        FontSize = 17,
                        Foreground = Theme.AccentBrush,
                        Margin = new Thickness(0, 10, 0, 5),
                    });
                foreach (var para in section.Paragraphs)
                    doc.Blocks.Add(new Paragraph(new Run(string.Join(" ", para.Sentences.Select(x => x.Text))))
                    {
                        Margin = new Thickness(0, 0, 0, 12),
                    });
            }
        }
        scriptReader.Document = doc;
    }

    static bool IsSpeechCut(CleanupDecision d) => d.Kind is DecisionKind.Retake or DecisionKind.FalseStart;
    static string Format(long ticks) => Time.Chapter(ticks);
    static string Label(DecisionKind kind) => kind switch
    {
        DecisionKind.Retake => "Retake",
        DecisionKind.FalseStart => "False start",
        DecisionKind.AdLibKept => "Ad-lib",
        DecisionKind.PossibleSpeechKept => "Possible speech",
        DecisionKind.Breath => "Breath",
        DecisionKind.MouthNoise => "Mouth noise",
        _ => "Pause",
    };

    void SetView(bool showAll, string? select = null)
    {
        allDecisions = showAll;
        PopulateList(select ?? Selected?.Id);
    }

    void ToggleSort()
    {
        s.Settings.ReviewLeastSureFirst = !s.Settings.ReviewLeastSureFirst;
        s.Settings.Save();
        PopulateList(Selected?.Id);
    }

    void PopulateList(string? select)
    {
        var shown = s.P.Narration.Decisions.Where(d => allDecisions || IsSpeechCut(d));
        visible = (s.Settings.ReviewLeastSureFirst ? shown.OrderBy(d => d.Confidence).ThenBy(d => d.Start) : shown.OrderBy(d => d.Start)).ToList();
        sort.Content = s.Settings.ReviewLeastSureFirst ? "Order: least sure first" : "Order: by time";
        list.Items.Clear();
        foreach (var d in visible)
        {
            var label = Ui.Text(Label(d.Kind), 13, weight: FontWeights.SemiBold);
            var context = Ui.Text(d.Context.Length == 0 ? d.Reason : d.Context, 13);
            context.TextWrapping = TextWrapping.NoWrap;
            context.TextTrimming = TextTrimming.CharacterEllipsis;
            var state = Ui.Text(d.Removes ? "Cut" : d.IsCut ? "Restored" : "Kept", 12, true);
            list.Items.Add(new ListBoxItem
            {
                Tag = d, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 7, 8, 7),
                Content = Ui.Columns((Ui.Text(Format(d.Start), 12, true), new GridLength(58)), (label, new GridLength(120)),
                    (context, new GridLength(1, GridUnitType.Star)), (state, new GridLength(70))),
            });
        }
        int selected = visible.FindIndex(d => d.Id == select);
        list.SelectedIndex = selected >= 0 ? selected : visible.Count > 0 ? 0 : -1;
        if (list.SelectedItem is { } item) list.ScrollIntoView(item);
        review.Background = allDecisions ? Brushes.Transparent : Theme.AccentSoft;
        all.Background = allDecisions ? Theme.AccentSoft : Brushes.Transparent;
        UpdateDecision();
    }

    void UpdateDecision()
    {
        var d = Selected;
        if (d is null)
        {
            details.Text = visible.Count > 0 ? "Select a decision to hear it in context."
                : allDecisions ? "No cleanup decisions." : "No retakes or false starts to review.";
            reason.Visibility = decisionActions.Visibility = Visibility.Collapsed;
            return;
        }
        details.Text = d.Context.Length == 0 ? Label(d.Kind) : d.Context;
        reason.Text = $"Original {Time.Timecode(d.Start)}  ·  {d.Reason}";
        reason.Visibility = decisionActions.Visibility = Visibility.Visible;
        toggle.Content = d.Removes ? "Restore audio" : d.IsCut ? "Reapply cut" : "Cut this audio";
    }

    void DrawMarkers()
    {
        markers.Children.Clear();
        if (preview is not { Duration: > 0 } take || markers.ActualWidth < 12) return;
        double width = markers.ActualWidth - 12;
        var groups = take.Markers.GroupBy(mark => Math.Round(mark.Position / (double)take.Duration * width / 12));
        foreach (var group in groups)
        {
            var items = group.ToList();
            var first = items[0];
            string name = items.Count == 1 ? $"{Label(first.Decision.Kind)} at {Format(first.Position)}" : $"{items.Count} cuts near {Format(first.Position)}";
            var marker = new Button
            {
                Content = items.Count > 1 ? "+" : "│", Width = 12, MinWidth = 0, Height = 20, MinHeight = 0,
                Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = items.Any(item => IsSpeechCut(item.Decision)) ? Theme.AccentBrush : Theme.MutedBrush,
                ToolTip = name,
            };
            AutomationProperties.SetName(marker, name);
            marker.Click += (_, _) =>
            {
                int current = items.FindIndex(item => item.Decision.Id == Selected?.Id);
                var pick = items[(current + 1) % items.Count];
                SetView(allDecisions || !IsSpeechCut(pick.Decision), pick.Decision.Id);
                Seek(pick.Position, false);
            };
            Canvas.SetLeft(marker, Math.Min(group.Key * 12, width));
            markers.Children.Add(marker);
        }
    }

    void UpdatePosition()
    {
        long duration = preview?.Duration ?? 0;
        position = Math.Clamp(position, 0, duration);
        updatingSeek = true;
        seek.Maximum = duration;
        seek.Value = position;
        updatingSeek = false;
        string label = $"{Format(position)} / {Format(duration)}";
        if (time.Text != label) time.Text = label;
        string text = preview?.PassageAt(position) ?? "";
        if (passage.Text != text) passage.Text = text;
    }

    void UpdateControls()
    {
        bool ready = !Busy && preview is not null;
        play.IsEnabled = seek.IsEnabled = ready && preview!.Duration > 0;
        markers.IsEnabled = reviewPane.IsEnabled = ready;
        cancel.IsEnabled = Busy;
        if (ready && preview!.Duration == 0) playbackStatus.Text = "No playable audio remains. Restore a cut to hear it here.";
    }

    public void Move(int delta)
    {
        if (Busy || visible.Count == 0) return;
        list.SelectedIndex = list.SelectedIndex < 0 && delta < 0 ? visible.Count - 1 : Math.Clamp(list.SelectedIndex + delta, 0, visible.Count - 1);
        list.ScrollIntoView(list.SelectedItem);
        if (Selected is { } decision && preview is { } take) Seek(take.Map.ToTimelineNearest(decision.Start), false);
    }

    public void Toggle()
    {
        if (Busy || Selected is not { } d) return;
        s.Edit(d.Removes ? "Restore narration audio" : "Cut narration audio", p =>
        {
            var decision = p.Narration.Decisions.First(x => x.Id == d.Id);
            decision.Applied = !decision.Applied;
            s.AfterNarrationChange(p);
        });
    }

    public void TogglePlayback()
    {
        if (Busy || preview is not { Duration: > 0 } take) return;
        if (playingTake) { StopPlayback(); return; }
        StopPlayback();
        if (position == take.Duration) position = 0;
        StartPlayback(take, new(position, take.Duration), true, "");
    }

    void Seek(long target, bool resume)
    {
        if (target == position && resume) return;
        StopPlayback();
        position = target;
        UpdatePosition();
        if (resume && !dragging && preview is { } take && position < take.Duration)
            StartPlayback(take, new(position, take.Duration), true, "");
    }

    public void Audition() => Audition(false, compare: true);

    void Audition(bool remove, bool compare = false)
    {
        if (Busy || Selected is not { } d) return;
        StopPlayback();
        var variant = new CleanPreview(s.P, d, remove);
        var range = variant.Around(d);
        if (compare)
        {
            var applied = new CleanPreview(s.P, d, true);
            nextAudition = (applied, applied.Around(d));
        }
        if (range.Duration > 0)
            StartPlayback(variant, range, false, remove ? "Playing cut" : "Playing original");
        else if (nextAudition is { } next)
        {
            nextAudition = null;
            StartPlayback(next.Preview, next.Range, false, "Playing cut");
        }
        else playbackStatus.Text = "There is no audio in this preview.";
    }

    void StartPlayback(CleanPreview take, TimeRange range, bool full, string label)
    {
        if (range.Duration <= 0)
        {
            playbackStatus.Text = "There is no audio in this preview.";
            return;
        }
        var media = take.Audio.Media.FirstOrDefault();
        playbackPath = media is null ? null : s.NarrationPreviewPathFor(media);
        if (playbackPath is null)
        {
            nextAudition = null;
            playbackStatus.Text = MediaStatus();
            return;
        }
        mixer.Prepare(take.Audio, 0);
        player.Play(mixer, range.Start, range.End);
        if (!player.Playing)
        {
            StopPlayback();
            playbackStatus.Text = "No playback device is available.";
            return;
        }
        playbackEnd = range.End;
        playingTake = full;
        play.Content = full ? "Pause" : "Play cleaned narration";
        playbackStatus.Text = label;
    }

    string MediaStatus()
    {
        var media = s.P.FindMedia(s.P.Narration.MediaId);
        return media is null || (!File.Exists(media.Path) && !File.Exists(s.P.Narration.CleanPath))
            ? "The narration recording is missing." : "Narration audio is still being prepared.";
    }

    public void Tick()
    {
        if (playbackEnd == 0) return;
        if (!player.Playing) { StopPlayback(); return; }
        if (player.Clock is not { } audible) return;
        if (playingTake)
        {
            position = Math.Min(audible, playbackEnd);
            UpdatePosition();
        }
        if (audible < playbackEnd) return;
        var next = nextAudition;
        StopPlayback();
        if (next is { } queued) StartPlayback(queued.Preview, queued.Range, false, "Playing cut");
    }

    public void StopPlayback()
    {
        player.Stop();
        mixer.Dispose();
        playingTake = false;
        playbackEnd = 0;
        nextAudition = null;
        play.Content = "Play cleaned narration";
        playbackStatus.Text = "";
    }

    public void Dispose()
    {
        player.Dispose();
        mixer.Dispose();
    }
}
