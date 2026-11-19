using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Cutroom.Audio;
using Cutroom.Core;

namespace Cutroom.App;

/// <summary>Record stage: load the script, read it from a panel you scroll yourself, and record narration to a clean WAV.</summary>
public sealed class RecordView : Grid
{
    readonly Session s;
    readonly Recorder recorder = new();
    readonly FlowDocumentScrollViewer reader = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsToolBarVisible = false };
    readonly TextBlock elapsed = Ui.Text("0:00", 40, false, FontWeights.SemiBold);
    readonly ProgressBar level = new() { Height = 12, Maximum = 1, Margin = new Thickness(0, 6, 0, 4) };
    readonly TextBlock levelNote = Ui.Text("", 12, true);
    readonly TextBlock narrationInfo = Ui.Text("", 12, true);
    readonly TextBlock marksInfo = Ui.Text("", 12, true);
    readonly Button recordButton;
    readonly ComboBox mics = new() { Margin = new Thickness(0, 0, 0, 12) };
    readonly DispatcherTimer meter = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly List<long> marks = [];
    double fontSize = 26;
    IReadOnlyList<InputDevice> devices = [];

    public bool Recording => recorder.Recording;
    public event Action? GoToClean;

    public RecordView(Session session)
    {
        s = session;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36, GridUnitType.Star), MinWidth = 320 });

        var scriptBar = Ui.Row(
            Ui.Button("Paste script", Paste, tip: "Paste from Google Docs (headings are kept)"),
            Ui.Button("Open .docx / .txt…", OpenScript),
            Ui.Button("A−", () => { fontSize = Math.Max(14, fontSize - 2); ShowScript(); }),
            Ui.Button("A+", () => { fontSize = Math.Min(48, fontSize + 2); ShowScript(); }));
        var left = new DockPanel();
        DockPanel.SetDock(scriptBar, Dock.Top);
        left.Children.Add(scriptBar);
        left.Children.Add(reader);
        var leftCard = Ui.Card(left, 16);
        SetColumn(leftCard, 0);
        Children.Add(leftCard);

        recordButton = Ui.Button("● Record", ToggleRecord, "record.toggle", primary: true);
        recordButton.FontSize = 16;
        recordButton.Padding = new Thickness(20, 10, 20, 10);
        mics.SelectionChanged += (_, _) =>
        {
            if (mics.SelectedIndex >= 0 && mics.SelectedIndex < devices.Count) { s.Settings.MicrophoneId = devices[mics.SelectedIndex].Id; s.Settings.Save(); }
        };
        var right = Ui.Stack(
            Ui.Header("Narration"),
            Spacer(8),
            Ui.Text("Microphone", 12, true), mics,
            elapsed,
            level, levelNote,
            Spacer(10),
            Ui.Row(recordButton, Ui.Button("Pause", () => { recorder.Paused = !recorder.Paused; Update(); }), Ui.Button("Mark retake", MarkRetake, "record.retake", tip: "Press right after you flub a line. Optional: cleanup finds retakes either way.")),
            marksInfo,
            Spacer(16),
            Ui.Header("Or use an existing recording"),
            Ui.Text("OBS and Streamlabs files work too; only the audio is used.", 12, true),
            Ui.Row(Ui.Button("Import recording…", ImportRecording)),
            Spacer(8),
            narrationInfo,
            Spacer(12),
            Ui.Row(Ui.Button("Next: clean it →", () => GoToClean?.Invoke(), primary: true)));
        var rightCard = Ui.Card(Ui.Scroll(right), 18);
        SetColumn(rightCard, 2);
        Children.Add(rightCard);

        meter.Tick += (_, _) => Update();
        recorder.Failed += msg => Dispatcher.BeginInvoke(() => { s.Say("Recording stopped: " + msg); StopRecording(); });
        s.ProjectChanged += () => { ShowScript(); ShowNarration(); };
        s.DocumentReplaced += () => { ShowScript(); ShowNarration(); };
        Loaded += (_, _) => { LoadDevices(); ShowScript(); ShowNarration(); };
    }

    static FrameworkElement Spacer(double h) => new Border { Height = h };

    void LoadDevices()
    {
        try { devices = Recorder.Devices(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException) { devices = []; }
        mics.Items.Clear();
        foreach (var d in devices) mics.Items.Add(d.Name);
        int index = devices.ToList().FindIndex(d => d.Id == s.Settings.MicrophoneId);
        mics.SelectedIndex = index >= 0 ? index : devices.Count > 0 ? 0 : -1;
        if (devices.Count == 0) mics.Items.Add("No microphone found");
    }

    void Paste()
    {
        Script? script = null;
        if (Clipboard.ContainsData(DataFormats.Html) && Clipboard.GetData(DataFormats.Html) is string html) script = Script.FromHtml(html);
        if ((script is null || !script.Sentences.Any()) && Clipboard.ContainsText()) script = Script.FromText(Clipboard.GetText());
        if (script is null || !script.Sentences.Any()) { s.Say("The clipboard has no text. Copy your script in Google Docs first."); return; }
        s.SetScript(script);
        s.Say($"Script loaded: {script.Sentences.Count()} sentences, {script.Sections.Count(x => x.Heading is not null)} sections.");
    }

    void OpenScript()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Scripts|*.docx;*.txt;*.md;*.html|All files|*.*", Title = "Open your script (File > Download > .docx in Google Docs)" };
        if (dialog.ShowDialog() != true) return;
        try { s.SetScript(Script.Load(dialog.FileName)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException) { s.Say("Could not read the script: " + ex.Message); }
    }

    void ShowScript()
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = fontSize, Foreground = Theme.TextBrush, Background = Theme.PanelBrush, PagePadding = new Thickness(8), LineHeight = fontSize * 1.5 };
        var script = s.P.Script;
        if (script is null || !script.Sentences.Any())
        {
            doc.Blocks.Add(new Paragraph(new Run("Paste or open your script. Use headings in Google Docs to mark sections; Cutroom turns them into section headers and YouTube chapters.")) { Foreground = Theme.MutedBrush, FontSize = 18 });
        }
        else
        {
            foreach (var section in script.Sections)
            {
                if (section.Heading is not null)
                    doc.Blocks.Add(new Paragraph(new Run(section.Heading)) { FontWeight = FontWeights.Bold, FontSize = fontSize * 1.05, Foreground = Theme.AccentBrush, Margin = new Thickness(0, fontSize, 0, fontSize * .4) });
                foreach (var para in section.Paragraphs)
                    doc.Blocks.Add(new Paragraph(new Run(string.Join(" ", para.Sentences.Select(x => x.Text)))) { Margin = new Thickness(0, 0, 0, fontSize * .8) });
            }
        }
        reader.Document = doc;
    }

    void ShowNarration()
    {
        var m = s.P.FindMedia(s.P.Narration.MediaId);
        narrationInfo.Text = m is null ? "No narration yet." : $"Narration: {m.Name} · {TimeSpan.FromSeconds(Time.ToSeconds(m.Duration)):h\\:mm\\:ss}" +
            (s.P.Narration.Processed ? " · cleaned" : " · not cleaned yet");
    }

    public void ToggleRecord() { if (recorder.Recording) StopRecording(); else StartRecording(); }

    void StartRecording()
    {
        if (devices.Count == 0) { s.Say("No microphone found."); return; }
        s.Stop();
        var path = Path.Combine(s.ProjectFolder, "narration", $"take-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        try { recorder.Start(path, s.Settings.MicrophoneId); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or ArgumentException)
        {
            s.Say("Could not start the microphone: " + ex.Message);
            return;
        }
        marks.Clear();
        meter.Start();
        Update();
    }

    async void StopRecording()
    {
        using var work = s.BeginProjectWork();
        var path = recorder.Stop();
        meter.Stop();
        Update();
        if (path is null || !File.Exists(path)) return;
        await s.SetNarration(path);
        var snapshot = marks.ToList();
        s.Edit("Retake marks", p => p.Narration.RetakeMarks = snapshot);
        s.Say($"Saved {Path.GetFileName(path)}" + (snapshot.Count > 0 ? $" with {snapshot.Count} retake marks." : "."));
    }

    public void MarkRetake()
    {
        if (!recorder.Recording) return;
        marks.Add(recorder.ElapsedTicks);
        Update();
    }

    async void ImportRecording()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Recordings|*.wav;*.mp3;*.flac;*.m4a;*.mp4;*.mkv;*.mov;*.flv|All files|*.*", Title = "Choose your narration recording" };
        if (dialog.ShowDialog() != true) return;
        using var work = s.BeginProjectWork();
        await s.SetNarration(dialog.FileName);
        s.Say("Narration set. Next: clean it.");
    }

    void Update()
    {
        recordButton.Content = recorder.Recording ? "■ Stop" : "● Record";
        elapsed.Text = recorder.Elapsed.ToString(recorder.Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
        level.Value = Math.Sqrt(recorder.Peak);
        level.Foreground = recorder.Peak >= .99f ? Theme.DangerBrush : recorder.Peak > .7f ? Theme.B(Theme.Sub) : Theme.OkBrush;
        levelNote.Text = !recorder.Recording ? "" : recorder.Paused ? "Paused" : recorder.Clipped ? "Too loud: it clipped. Move back a little or lower the mic gain." : "Recording · saved continuously";
        marksInfo.Text = marks.Count == 0 ? "" : $"{marks.Count} retake mark{(marks.Count == 1 ? "" : "s")}";
    }
}
