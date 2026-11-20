using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Cutroom.AI;
using Cutroom.Core;
using Cutroom.Engine;

namespace Cutroom.App;

/// <summary>Export stage: one YouTube preset, pre-flight checks, background export with ETA, and AI packaging.</summary>
public sealed class ExportView : Grid
{
    readonly Session s;
    readonly TextBox fileName = new() { Margin = new Thickness(0, 0, 0, 10) };
    readonly StackPanel issues = new();
    readonly ProgressBar bar = new() { Height = 8, Maximum = 1, Margin = new Thickness(0, 10, 0, 6) };
    readonly TextBlock status = Ui.Text("", 12, true);
    readonly Button export;
    readonly Button cancelButton;
    readonly Button reveal;
    readonly StackPanel package = new();
    CancellationTokenSource? cancel;
    string? lastOutput;
    public event Action<long>? GoToVisualGap;

    public ExportView(Session session)
    {
        s = session;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) });

        export = Ui.Button("Export video", () => _ = Export(), primary: true);
        cancelButton = Ui.Button("Cancel", () => cancel?.Cancel());
        cancelButton.Visibility = Visibility.Collapsed;
        reveal = Ui.Button("Show in Explorer", () => { if (lastOutput is not null) Process.Start("explorer.exe", $"/select,\"{lastOutput}\""); });
        reveal.Visibility = Visibility.Collapsed;
        var left = Ui.Stack(
            Ui.Header("YouTube 1080p60"),
            Ui.Text("H.264 on your AMD GPU (40 Mbps), AAC 320 kbps, loudness −14 LUFS with a −1 dBTP ceiling. Captions (.srt) and chapters are saved next to the video.", 12, true),
            new Border { Height = 12 },
            Ui.Text("File name", 12, true), fileName,
            issues,
            Ui.Row(export, cancelButton, reveal),
            bar, status);
        var leftCard = Ui.Card(Ui.Scroll(left), 18);
        SetColumn(leftCard, 0);
        Children.Add(leftCard);
        var rightCard = Ui.Card(Ui.Scroll(package), 18);
        SetColumn(rightCard, 2);
        Children.Add(rightCard);
        IsVisibleChanged += (_, _) => { if (IsVisible) { RefreshIssues(); BuildPackage(); } };
        s.DocumentReplaced += () =>
        {
            lastOutput = null;
            bar.Value = 0;
            status.Text = "";
            reveal.Visibility = Visibility.Collapsed;
            if (IsVisible) { RefreshIssues(); BuildPackage(); }
        };
    }

    string ExportFolder => Path.Combine(s.ProjectFolder, "exports");

    void RefreshIssues()
    {
        if (fileName.Text.Length == 0 || lastOutput is null) fileName.Text = Derive.SafeFileName(s.P.Name) + ".mp4";
        issues.Children.Clear();
        var found = Exporter.PreFlight(s.P, s.Settings, out var firstVisualGap);
        if (found.Count == 0) { issues.Children.Add(Ui.Text("✓ Ready to export.", 12)); return; }
        issues.Children.Add(Ui.Text("Before you export:", 12, true));
        foreach (var i in found) issues.Children.Add(new TextBlock { Text = "• " + i, Foreground = Theme.B(Theme.Sub), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
        if (firstVisualGap is { } frame)
        {
            var go = Ui.Button("Go to first gap", () => GoToVisualGap?.Invoke(frame));
            go.Name = "GoToVisualGap";
            issues.Children.Add(Ui.Row(go));
        }
        issues.Children.Add(new Border { Height = 8 });
    }

    async Task Export()
    {
        using var work = s.BeginProjectWork();
        var output = Path.Combine(ExportFolder, Derive.SafeFileName(Path.GetFileNameWithoutExtension(fileName.Text)) + ".mp4");
        if (File.Exists(output) && MessageBox.Show($"{Path.GetFileName(output)} exists. Replace it?", "Export", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        s.Stop();
        s.SaveSafely();
        cancel = new CancellationTokenSource();
        export.IsEnabled = false;
        cancelButton.Visibility = Visibility.Visible;
        reveal.Visibility = Visibility.Collapsed;
        // Export works on a snapshot so you can keep editing while it renders.
        var snapshot = ProjectJson.Deserialize(ProjectJson.Serialize(s.P));
        try
        {
            var result = await Task.Run(() => Exporter.Run(snapshot, s.Settings, output, s.AudioPathFor,
                new Progress<ExportProgress>(p =>
                {
                    bar.Value = p.Fraction;
                    status.Text = $"{p.Stage} · {p.Fraction:0%}" + (p.Remaining is { } r && p.Fraction > .02 ? $" · about {r:m\\:ss} left" : "");
                }), cancel.Token));
            lastOutput = result.VideoPath;
            reveal.Visibility = Visibility.Visible;
            double lufs = await Exporter.MeasureFile(result.VideoPath);
            status.Text = $"Exported {Path.GetFileName(result.VideoPath)} with {result.Encoder} · {lufs:0.0} LUFS" + (result.SrtPath is not null ? " · captions saved" : "") + (result.ChaptersPath is not null ? " · chapters saved" : "");
            if (s.P.Package is { Length: > 0 } pkg) await File.WriteAllTextAsync(Path.ChangeExtension(result.VideoPath, ".package.txt"), pkg);
        }
        catch (OperationCanceledException) { status.Text = "Export cancelled."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = "Export failed: " + ex.Message; }
        finally
        {
            cancel.Dispose();
            cancel = null;
            export.IsEnabled = true;
            cancelButton.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- Packaging ----------

    void BuildPackage()
    {
        package.Children.Clear();
        var profile = s.Settings.Channel;
        package.Children.Add(Ui.Header("YouTube packaging"));
        package.Children.Add(Ui.Text("Titles, description and tags drafted in your channel's style. Your links and sign-off are added word for word, then the chapters.", 12, true));
        package.Children.Add(new Border { Height = 10 });
        if (!s.Ai.Enabled) package.Children.Add(Ui.Text("AI is off: install and sign in to Claude Code or Codex to draft packaging. Chapters still work below.", 12, true));

        package.Children.Add(Ui.Text(profile.PastTitles.Count == 0 ? "Channel style: not set up yet." : $"Learned from {profile.PastTitles.Count} past videos.", 12, true));
        package.Children.Add(Ui.Row(
            Ui.Button("Learn from my channel…", () => _ = LearnChannel()),
            Ui.Button("Edit style and fixed text…", EditProfile)));

        var draft = Ui.Button("Draft package", () => _ = Draft(), primary: true);
        draft.IsEnabled = s.Ai.Enabled && s.P.Script is not null;
        package.Children.Add(Ui.Row(draft));

        var chapters = Derive.Chapters(s.P);
        if (s.P.Package is { Length: > 0 } text)
        {
            var box = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 320, Margin = new Thickness(0, 6, 0, 8) };
            box.LostFocus += (_, _) => { if (box.Text != s.P.Package) s.Edit("Edit package", p => p.Package = box.Text); };
            package.Children.Add(box);
            package.Children.Add(Ui.Row(Ui.Button("Copy all", () => Clipboard.SetText(box.Text))));
        }
        package.Children.Add(Ui.Header("Chapters"));
        package.Children.Add(new TextBox { Text = chapters.Length > 0 ? chapters : "Add headings to your script to get chapters.", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
        if (chapters.Length > 0) package.Children.Add(Ui.Row(Ui.Button("Copy chapters", () => Clipboard.SetText(chapters))));
    }

    async Task LearnChannel()
    {
        var input = Ui.Ask(Window.GetWindow(this)!, "Your channel", "Paste your YouTube channel URL or @handle:", s.Settings.Channel.ChannelId);
        if (string.IsNullOrWhiteSpace(input)) return;
        try
        {
            s.Say("Reading your recent videos…");
            var (id, titles, descriptions) = await AiService.FetchChannel(input, CancellationToken.None);
            var profile = s.Settings.Channel;
            profile.ChannelId = id;
            profile.PastTitles = titles;
            profile.PastDescriptions = descriptions;
            profile.FixedBlock = AiService.FixedBlock(descriptions);
            if (s.Ai.Enabled) profile.StyleNotes = await s.Ai.LearnStyle(profile, CancellationToken.None);
            s.Settings.Save();
            s.Say($"Learned from {titles.Count} videos.");
        }
        catch (Exception ex) when (ex is AiException or System.Net.Http.HttpRequestException or System.Xml.XmlException or TaskCanceledException) { s.Say(ex.Message); }
        BuildPackage();
    }

    void EditProfile()
    {
        var profile = s.Settings.Channel;
        var style = Ui.Ask(Window.GetWindow(this)!, "Channel style", "Style notes (the AI follows these):", profile.StyleNotes, multiline: true);
        if (style is not null) profile.StyleNotes = style;
        var fixedText = Ui.Ask(Window.GetWindow(this)!, "Fixed text", "Links, socials and sign-off (added word for word at the end of every description):", profile.FixedBlock, multiline: true);
        if (fixedText is not null) profile.FixedBlock = fixedText;
        s.Settings.Save();
        BuildPackage();
    }

    async Task Draft()
    {
        using var work = s.BeginProjectWork();
        if (s.P.Script is null) return;
        s.Say("Drafting titles, description and tags…");
        try
        {
            var d = await s.Ai.DraftPackage(s.P.Script, s.Settings.Channel, CancellationToken.None);
            var text = "TITLES\n" + string.Join("\n", d.Titles.Select((t, i) => $"{i + 1}. {t}")) +
                       "\n\nDESCRIPTION\n" + AiService.AssembleDescription(d.Description, Derive.Chapters(s.P), s.Settings.Channel.FixedBlock) +
                       "\n\nTAGS\n" + string.Join(", ", d.Tags);
            s.Edit("Draft package", p => p.Package = text);
            s.Say("Package drafted. Edit anything, then copy.");
        }
        catch (AiException ex) { s.Say(ex.Message); }
        BuildPackage();
    }
}
