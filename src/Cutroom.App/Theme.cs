using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Cutroom.Core;

namespace Cutroom.App;

/// <summary>The chosen "modern minimal dark" look (mock B): near-black background, rounded floating panels, one accent.</summary>
public static class Theme
{
    public static readonly Color Bg = C("#0B0C0F"), Panel = C("#14161B"), Panel2 = C("#1B1E25"), Panel3 = C("#232733"),
        Text = C("#E8E9EE"), Muted = C("#7D8291"), Accent = C("#7C8CFF"), Danger = C("#FF6B6B"), Ok = C("#3DDC97");

    public static readonly Color Footage = C("#4A7DFF"), Card = C("#A46CFF"), Sub = C("#F2C94C"), Narration = C("#3DDC97"),
        Game = C("#2CC5C5"), Music = C("#6C8CFF"), Sfx = C("#FF7A59"), Facecam = C("#E86FB7");

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    public static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    public static SolidColorBrush B(Color c, byte alpha) => B(Color.FromArgb(alpha, c.R, c.G, c.B));

    public static readonly SolidColorBrush BgBrush = B(Bg), PanelBrush = B(Panel), Panel2Brush = B(Panel2), Panel3Brush = B(Panel3),
        TextBrush = B(Text), MutedBrush = B(Muted), AccentBrush = B(Accent), AccentSoft = B(Accent, 0x30), DangerBrush = B(Danger), OkBrush = B(Ok),
        WarnBrush = B(C("#F2C94C"));

    public const double Body = 13, Title = 15;

    /// <summary>Control templates (Styles.xaml), merged into every window after the Fluent dictionary so they win.</summary>
    static ResourceDictionary? styles;
    static ResourceDictionary Styles => styles ??= new() { Source = new Uri("pack://application:,,,/Cutroom;component/Styles.xaml") };

    static void UseStyles(ResourceDictionary resources)
    {
        resources.MergedDictionaries.Remove(Styles);
        resources.MergedDictionaries.Add(Styles);
    }

    public static Color RoleColor(TrackRole role) => role switch
    {
        TrackRole.Footage => Footage, TrackRole.Cards => Card, TrackRole.Subtitles => Sub, TrackRole.Narration => Narration,
        TrackRole.Game => Game, TrackRole.Music => Music, TrackRole.Sfx => Sfx, _ => Facecam,
    };

    public static void Apply(ResourceDictionary resources)
    {
        Application.Current.ThemeMode = ThemeMode.Dark;
        resources["CutroomBg"] = BgBrush;
        UseStyles(resources);
        var font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        foreach (var type in new[] { typeof(Window), typeof(UserControl) })
        {
            var s = new Style(type);
            s.Setters.Add(new Setter(Control.FontFamilyProperty, font));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 13d));
            resources[type] = s;
        }
    }

    public static void Window(Window w)
    {
        w.Background = BgBrush;
        w.Foreground = TextBrush;
        w.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        w.FontSize = 13;
        w.UseLayoutRounding = true;
        w.ThemeMode = ThemeMode.Dark;
        // Fluent may add its dictionary when the window loads; keep ours last either way.
        UseStyles(w.Resources);
        w.SourceInitialized += (_, _) => UseStyles(w.Resources);
        w.Loaded += (_, _) => UseStyles(w.Resources);
    }
}

public enum ButtonKind { Secondary, Primary, Ghost, Icon, IconPrimary, Pill }

/// <summary>Small factory helpers so views can be built in code with consistent styling.</summary>
public static class Ui
{
    public static Border Card(UIElement child, double padding = 14) => new()
    {
        Background = Theme.PanelBrush, CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), Child = child,
    };

    public static TextBlock Text(string text, double size = 13, bool muted = false, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = muted ? Theme.MutedBrush : Theme.TextBrush, FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Header(string text) => Text(text, 14, false, FontWeights.SemiBold);

    /// <summary>Small uppercase muted label that names a group of controls or a section of a list.</summary>
    public static TextBlock SectionLabel(string text)
    {
        var t = Text(text.ToUpperInvariant(), 10.5, true, FontWeights.SemiBold);
        t.Margin = new Thickness(0, 14, 0, 6);
        return t;
    }

    /// <summary>The top of a stage: title and one line of explanation on the left, the stage's actions on the right.</summary>
    public static Grid PageHeader(string title, string subtitle, params UIElement[] actions)
    {
        var heading = Stack(Text(title, Theme.Title + 3, false, FontWeights.SemiBold), Text(subtitle, 12.5, true));
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var a in actions) right.Children.Add(a);
        var g = Columns((heading, new GridLength(1, GridUnitType.Star)), (right, GridLength.Auto));
        g.Margin = new Thickness(4, 2, 0, 12);
        return g;
    }

    /// <summary>A calm placeholder for a panel with nothing in it yet: what goes here and how to get it.</summary>
    public static FrameworkElement EmptyState(string title, string body, UIElement? action = null)
    {
        var t = Text(title, Theme.Title, false, FontWeights.SemiBold);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        var b = Text(body, Theme.Body, true);
        b.TextAlignment = TextAlignment.Center;
        b.MaxWidth = 420;
        b.Margin = new Thickness(0, 6, 0, 14);
        var s = Stack(t, b);
        if (action is FrameworkElement a) { a.HorizontalAlignment = HorizontalAlignment.Center; s.Children.Add(a); }
        s.HorizontalAlignment = HorizontalAlignment.Center;
        s.VerticalAlignment = VerticalAlignment.Center;
        s.Margin = new Thickness(24, 48, 24, 48);
        return s;
    }

    /// <summary>Groups of related tool buttons, each group on its own soft tile, wrapping when space runs out.</summary>
    public static WrapPanel Toolbar(params UIElement[][] groups)
    {
        var bar = new WrapPanel();
        foreach (var group in groups)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var item in group)
            {
                if (item is FrameworkElement f) f.Margin = new Thickness(0, 0, 2, 0);
                row.Children.Add(item);
            }
            bar.Children.Add(new Border { Background = Theme.PanelBrush, CornerRadius = new CornerRadius(10), Padding = new Thickness(3), Margin = new Thickness(0, 0, 8, 8), Child = row });
        }
        return bar;
    }

    /// <summary>A rounded track holding pill buttons, for stage tabs and two-way switches.</summary>
    public static Border Segmented(params Button[] pills)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var p in pills) { p.Margin = new Thickness(0, 0, 2, 0); row.Children.Add(p); }
        return new Border { Background = Theme.PanelBrush, CornerRadius = new CornerRadius(18), Padding = new Thickness(3), Child = row };
    }

    /// <summary>Marks a pill as the selected one ("on") or the highlighted one ("accent").</summary>
    public static void SetPill(Button pill, bool on, bool accent = false) => pill.Tag = on ? accent ? "accent" : "on" : null;

    public static void Use(FrameworkElement e, ButtonKind kind) => e.SetResourceReference(FrameworkElement.StyleProperty, kind switch
    {
        ButtonKind.Primary => "Button.Primary",
        ButtonKind.Ghost => "Button.Ghost",
        ButtonKind.Icon => "Button.Icon",
        ButtonKind.IconPrimary => "Button.IconPrimary",
        ButtonKind.Pill => "Button.Pill",
        _ => typeof(Button),
    });

    /// <summary>Tabular figures, so times and numbers don't jitter as they change.</summary>
    public static T Tabular<T>(this T t) where T : TextBlock
    {
        System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    /// <summary>A button whose tooltip shows its current keyboard shortcut.</summary>
    public static Button Button(string label, Action onClick, string? command = null, bool primary = false, string? tip = null, ButtonKind kind = ButtonKind.Secondary)
    {
        var b = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0) };
        // Pills use Tag for their selected state.
        if (kind != ButtonKind.Pill) b.Tag = command;
        Use(b, primary ? ButtonKind.Primary : kind);
        b.Click += (_, _) => onClick();
        b.ToolTipOpening += (_, _) =>
        {
            var key = command is null ? null : Session.Current?.Settings.KeyFor(command);
            b.ToolTip = (tip ?? label) + (string.IsNullOrEmpty(key) ? "" : $"   ({key})");
        };
        b.ToolTip = tip ?? label;
        return b;
    }

    public static ToggleButton Chip(string label, bool on, Action<bool> changed)
    {
        var t = new ToggleButton { Content = label, IsChecked = on, Margin = new Thickness(0, 0, 6, 6) };
        t.Checked += (_, _) => changed(true);
        t.Unchecked += (_, _) => changed(false);
        return t;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel Stack(params UIElement[] children)
    {
        var p = new StackPanel();
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>A labeled slider that reports changes when the user releases it.</summary>
    public static FrameworkElement Slider(string label, double min, double max, double value, Action<double> commit, string format = "0.##", double step = 0)
    {
        var name = Text(label, 12.5, true);
        var shown = Text(value.ToString(format), 12.5).Tabular();
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Margin = new Thickness(0, 2, 0, 12), IsSnapToTickEnabled = step > 0, TickFrequency = step > 0 ? step : 1 };
        s.ValueChanged += (_, e) => shown.Text = e.NewValue.ToString(format);
        s.PreviewMouseUp += (_, _) => commit(s.Value);
        s.KeyUp += (_, _) => commit(s.Value);
        return Stack(Columns((name, new GridLength(1, GridUnitType.Star)), (shown, GridLength.Auto)), s);
    }

    public static ComboBox Combo<T>(IEnumerable<T> items, T selected, Func<T, string> name, Action<T> changed)
    {
        var list = items.ToList();
        var c = new ComboBox { Margin = new Thickness(0, 0, 0, 10), MinWidth = 160 };
        foreach (var i in list) c.Items.Add(name(i));
        c.SelectedIndex = Math.Max(0, list.IndexOf(selected));
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(list[c.SelectedIndex]); };
        return c;
    }

    public static CheckBox Check(string label, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 0, 0, 8) };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    public static ScrollViewer Scroll(UIElement child) => new() { Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    public static Border Dot(Color color, bool filled = true) => new()
    {
        Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = filled ? Theme.B(color) : Brushes.Transparent,
        BorderBrush = Theme.B(color), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top,
    };

    public static Grid Columns(params (UIElement Element, GridLength Width)[] columns)
    {
        var g = new Grid();
        for (int i = 0; i < columns.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = columns[i].Width });
            Grid.SetColumn(columns[i].Element, i);
            g.Children.Add(columns[i].Element);
        }
        return g;
    }

    public static string? Ask(Window owner, string title, string prompt, string initial = "", bool multiline = false)
    {
        var box = new TextBox { Text = initial, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, Height = multiline ? 180 : double.NaN, MinWidth = 420, Margin = new Thickness(0, 8, 0, 12) };
        var dialog = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        Theme.Window(dialog);
        string? result = null;
        var ok = Button("OK", () => { result = box.Text; dialog.Close(); }, primary: true);
        ok.IsDefault = !multiline;
        var cancel = Button("Cancel", dialog.Close);
        cancel.IsCancel = true;
        dialog.Content = new Border { Padding = new Thickness(18), Child = Stack(Text(prompt), box, Row(ok, cancel)) };
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        dialog.ShowDialog();
        return result;
    }
}

/// <summary>Parses shortcut strings like "Ctrl+Shift+Z" or "OemPeriod" and matches key events.</summary>
public readonly record struct Shortcut(Key Key, ModifierKeys Modifiers)
{
    public static Shortcut? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = ModifierKeys.None;
        Key key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                default:
                    if (!Enum.TryParse(part, true, out key)) return null;
                    break;
            }
        }
        return key == Key.None ? null : new Shortcut(key, mods);
    }

    public static string Format(Key key, ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
