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
        TextBrush = B(Text), MutedBrush = B(Muted), AccentBrush = B(Accent), AccentSoft = B(Accent, 0x30), DangerBrush = B(Danger), OkBrush = B(Ok);

    public static Color RoleColor(TrackRole role) => role switch
    {
        TrackRole.Footage => Footage, TrackRole.Cards => Card, TrackRole.Subtitles => Sub, TrackRole.Narration => Narration,
        TrackRole.Game => Game, TrackRole.Music => Music, TrackRole.Sfx => Sfx, _ => Facecam,
    };

    public static void Apply(ResourceDictionary resources)
    {
        Application.Current.ThemeMode = ThemeMode.Dark;
        resources["CutroomBg"] = BgBrush;
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
    }
}

/// <summary>Small factory helpers so views can be built in code with consistent styling.</summary>
public static class Ui
{
    public static Border Card(UIElement child, double padding = 12) => new()
    {
        Background = Theme.PanelBrush, CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), Child = child,
    };

    public static TextBlock Text(string text, double size = 13, bool muted = false, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = muted ? Theme.MutedBrush : Theme.TextBrush, FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Header(string text) => Text(text, 13, false, FontWeights.SemiBold);

    /// <summary>A button whose tooltip shows its current keyboard shortcut.</summary>
    public static Button Button(string label, Action onClick, string? command = null, bool primary = false, string? tip = null)
    {
        var b = new Button
        {
            Content = label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0), MinHeight = 30,
            Tag = command,
        };
        if (primary)
        {
            // Our own accent rather than the Windows accent color, so the look matches mock B everywhere.
            b.Background = Theme.AccentBrush;
            b.BorderBrush = Theme.AccentBrush;
            b.Foreground = Theme.BgBrush;
            b.FontWeight = FontWeights.SemiBold;
        }
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
        var t = new ToggleButton { Content = label, IsChecked = on, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6), MinHeight = 26 };
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
        var text = Text($"{label}: {value.ToString(format)}", 12, true);
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Margin = new Thickness(0, 2, 0, 10), IsSnapToTickEnabled = step > 0, TickFrequency = step > 0 ? step : 1 };
        s.ValueChanged += (_, e) => text.Text = $"{label}: {e.NewValue.ToString(format)}";
        s.PreviewMouseUp += (_, _) => commit(s.Value);
        s.KeyUp += (_, _) => commit(s.Value);
        return Stack(text, s);
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
