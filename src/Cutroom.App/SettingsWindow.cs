using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cutroom.Core;
using Cutroom.Library;
using Cutroom.Speech;

namespace Cutroom.App;

/// <summary>Settings: shortcuts (searchable, rebindable, conflicts shown), folders, AI, narration and card look.</summary>
public sealed class SettingsWindow : Window
{
    readonly Session s;
    readonly StackPanel shortcutList = new();
    readonly TextBox filter = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBlock conflicts = Ui.Text("", 12);
    string? capturing;

    public SettingsWindow(Session session, int tab = 0)
    {
        s = session;
        Title = "Cutroom settings";
        Width = 860;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.Window(this);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Shortcuts", Content = Shortcuts() });
        tabs.Items.Add(new TabItem { Header = "Folders", Content = Page(Folders()) });
        tabs.Items.Add(new TabItem { Header = "AI", Content = Page(Ai()) });
        tabs.Items.Add(new TabItem { Header = "Narration", Content = Page(Narration()) });
        tabs.Items.Add(new TabItem { Header = "Cards", Content = Page(Cards()) });
        tabs.SelectedIndex = tab;
        Content = new Border { Padding = new Thickness(12), Child = tabs };
        PreviewKeyDown += Capture;
        Closed += (_, _) => s.Settings.Save();
    }

    static FrameworkElement Page(UIElement content) => new Border { Padding = new Thickness(6, 14, 6, 6), Child = Ui.Scroll(content) };

    // ---------- Shortcuts ----------

    FrameworkElement Shortcuts()
    {
        filter.ToolTip = "Search commands or keys";
        filter.TextChanged += (_, _) => FillShortcuts();
        var reset = Ui.Button("Reset all to defaults", () => { s.Settings.Keys.Clear(); FillShortcuts(); });
        var label = new Label { Content = "_Search shortcuts", Target = filter, Padding = new Thickness(0, 0, 0, 4) };
        var top = Ui.Stack(Ui.Text("Click a shortcut, then press the new keys. Esc cancels, Backspace clears.", 12, true), new Border { Height = 12 }, label, filter, conflicts, Ui.Row(reset));
        var dock = new DockPanel { Margin = new Thickness(6, 14, 6, 6) };
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(Ui.Scroll(shortcutList));
        FillShortcuts();
        return dock;
    }

    void FillShortcuts()
    {
        shortcutList.Children.Clear();
        var q = filter.Text.Trim();
        var conflicting = s.Settings.Conflicts().SelectMany(c => c.Commands.Select(x => x.Id)).ToHashSet();
        conflicts.Text = conflicting.Count == 0 ? "" : "⚠ Some shortcuts clash: " + string.Join("; ", s.Settings.Conflicts().Select(c => $"{c.Key} → {string.Join(", ", c.Commands.Select(x => x.Name))}"));
        conflicts.Foreground = Theme.DangerBrush;
        foreach (var group in Commands.All.Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Settings.KeyFor(c.Id).Contains(q, StringComparison.OrdinalIgnoreCase)).GroupBy(c => c.Group))
        {
            var header = Ui.Header(group.Key);
            header.Margin = new Thickness(0, 12, 0, 4);
            shortcutList.Children.Add(header);
            foreach (var c in group)
            {
                var key = s.Settings.KeyFor(c.Id);
                var button = new Button { Content = capturing == c.Id ? "Press keys…" : key.Length == 0 ? "—" : key, MinWidth = 150, Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Right };
                if (conflicting.Contains(c.Id)) button.Foreground = Theme.DangerBrush;
                var id = c.Id;
                button.Click += (_, _) => { capturing = id; FillShortcuts(); };
                var name = Ui.Text(c.Name + (key != c.DefaultKey ? $"   (default {c.DefaultKey})" : ""), 13);
                var row = Ui.Columns((name, new GridLength(1, GridUnitType.Star)), (button, GridLength.Auto));
                row.Margin = new Thickness(0, 2, 0, 2);
                shortcutList.Children.Add(row);
            }
        }
    }

    void Capture(object sender, KeyEventArgs e)
    {
        if (capturing is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        e.Handled = true;
        var id = capturing;
        capturing = null;
        if (key == Key.Escape) { FillShortcuts(); return; }
        var text = key == Key.Back && Keyboard.Modifiers == ModifierKeys.None ? "" : Shortcut.Format(key, Keyboard.Modifiers);
        if (text == Commands.Find(id)?.DefaultKey) s.Settings.Keys.Remove(id);
        else s.Settings.Keys[id] = text;
        FillShortcuts();
    }

    // ---------- Folders ----------

    FrameworkElement Folders()
    {
        var panel = new StackPanel();
        void Build()
        {
            panel.Children.Clear();
            panel.Children.Add(Ui.Text("Cutroom indexes these folders where they are; nothing is moved or copied.", 12, true));
            FolderList(panel, "Footage folders", s.Settings.FootageFolders, LibraryRole.Footage, Build);
            FolderList(panel, "Asset folders (music, SFX, images, backgrounds)", s.Settings.AssetFolders, LibraryRole.Asset, Build);
            panel.Children.Add(Ui.Header("Projects folder"));
            panel.Children.Add(Ui.Text(new ProjectCatalog(s.Settings).Root, 12, true));
            panel.Children.Add(Ui.Row(Ui.Button("Change…", () =>
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = "Where new projects are saved" };
                if (d.ShowDialog(this) == true) { s.Settings.ProjectsFolder = d.FolderName; s.Settings.Save(); Build(); }
            })));
        }
        Build();
        return panel;
    }

    void FolderList(StackPanel panel, string title, List<string> folders, LibraryRole role, Action rebuild)
    {
        var header = Ui.Header(title);
        header.Margin = new Thickness(0, 14, 0, 6);
        panel.Children.Add(header);
        if (folders.Count == 0) panel.Children.Add(Ui.Text("None yet.", 12, true));
        foreach (var f in folders.ToList())
        {
            var remove = Ui.Button("Remove", () => { folders.Remove(f); s.Settings.Save(); rebuild(); });
            panel.Children.Add(Ui.Columns((Ui.Text(f, 12), new GridLength(1, GridUnitType.Star)), (remove, GridLength.Auto)));
        }
        panel.Children.Add(Ui.Row(Ui.Button("Add folder…", () =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog();
            if (d.ShowDialog(this) != true || folders.Contains(d.FolderName, StringComparer.OrdinalIgnoreCase)) return;
            folders.Add(d.FolderName);
            s.Settings.Save();
            s.Library.Watch(d.FolderName, role);
            _ = JobQueue.Shared.Run($"Indexing {Path.GetFileName(d.FolderName)}", (p, ct) => s.Library.Scan(d.FolderName, role, p, ct), background: true);
            rebuild();
        })));
    }

    // ---------- AI ----------

    FrameworkElement Ai()
    {
        var installed = s.Ai.Available().Select(p => p.Name).ToList();
        return Ui.Stack(
            Ui.Header("AI helpers"),
            Ui.Text("Card text, visual plans and YouTube packaging run through the Claude Code or Codex app you're already signed into, on your subscription. Cutroom never uses API keys or per-use billing; if you hit your plan's limit, the AI buttons just say so.", 12, true),
            new Border { Height = 10 },
            Ui.Text("Use first", 12, true),
            Ui.Combo(new[] { "claude", "codex" }, s.Settings.AiProvider, p => p == "claude" ? "Claude Code" : "Codex", p => { s.Settings.AiProvider = p; s.Ai.Preferred = p; s.Settings.Save(); }),
            Ui.Text(installed.Count == 0 ? "Neither CLI was found. Install Claude Code or Codex and sign in, then restart Cutroom." : "Found: " + string.Join(", ", installed), 12, true));
    }

    // ---------- Narration ----------

    FrameworkElement Narration()
    {
        var models = new[] { Models.WhisperTurbo.Name, Models.WhisperBase.Name };
        return Ui.Stack(
            Ui.Header("Transcription"),
            Ui.Text("Runs on your PC. The turbo model is accurate and fast; the base model is a lighter fallback.", 12, true),
            new Border { Height = 8 },
            Ui.Combo(models, s.Settings.WhisperModel, m => m == Models.WhisperTurbo.Name ? "Large v3 turbo (recommended)" : "Base English (fast, less accurate)", m => { s.Settings.WhisperModel = m; s.Settings.Save(); }),
            Ui.Check("Use the graphics card (much faster; falls back to the CPU automatically)", s.Settings.GpuTranscription, v => { s.Settings.GpuTranscription = v; Transcriber.PreferGpu = v; s.Settings.Save(); }),
            Ui.Text(File.Exists(Tools.WhisperGpu) ? "GPU transcription is installed." : "GPU transcription isn't installed in this build; the CPU is used.", 12, true),
            Ui.Header("Noise removal for new projects"),
            new Border { Height = 6 },
            Ui.Combo(Enum.GetValues<Denoiser>(), s.Settings.Denoiser, d => d switch { Denoiser.DeepFilterNet => "DeepFilterNet (best)", Denoiser.RNNoise => "RNNoise (lighter)", _ => "Off" }, d => { s.Settings.Denoiser = d; s.Settings.Save(); }),
            Ui.Header("Sound effects"),
            new Border { Height = 6 },
            Ui.Check("Add a matching sound to zooms, flashes and transitions", s.Settings.AutoSfx, v => { s.Settings.AutoSfx = v; s.Settings.Save(); }));
    }

    // ---------- Cards ----------

    FrameworkElement Cards()
    {
        var panel = new StackPanel();
        void Build()
        {
            panel.Children.Clear();
            panel.Children.Add(Ui.Text("Each template sets the look of new cards. Pick your own notebook or background image to replace the built-in animated page.", 12, true));
            foreach (var t in s.Settings.CardTemplates)
            {
                var header = Ui.Header(t.Name);
                header.Margin = new Thickness(0, 14, 0, 4);
                panel.Children.Add(header);
                if (t.Kind is CardTemplateKind.Background or CardTemplateKind.SectionHeader or CardTemplateKind.EndScreen or CardTemplateKind.TitleCard)
                {
                    panel.Children.Add(Ui.Text("Background: " + (t.BackgroundPath ?? "built-in notebook page"), 12, true));
                    var template = t;
                    panel.Children.Add(Ui.Row(
                        Ui.Button("Choose image or video…", () =>
                        {
                            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Images and video|*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.mov;*.mkv|All files|*.*" };
                            if (d.ShowDialog(this) == true) { template.BackgroundPath = d.FileName; s.Settings.Save(); Build(); }
                        }),
                        Ui.Button("Use built-in", () => { template.BackgroundPath = null; s.Settings.Save(); Build(); })));
                }
                var accent = new TextBox { Text = t.Accent, Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
                var tpl = t;
                accent.LostFocus += (_, _) =>
                {
                    try { _ = Theme.C(accent.Text); tpl.Accent = accent.Text; s.Settings.Save(); }
                    catch (FormatException) { accent.Text = tpl.Accent; }
                };
                panel.Children.Add(Ui.Row(Ui.Text("Accent color ", 12, true), accent));
                panel.Children.Add(Ui.Combo(Enum.GetValues<RevealMode>(), t.Reveal, r => r == RevealMode.LineByLine ? "Lines appear as you say them" : "All text at once", r => { tpl.Reveal = r; s.Settings.Save(); }));
            }
        }
        Build();
        return panel;
    }
}
