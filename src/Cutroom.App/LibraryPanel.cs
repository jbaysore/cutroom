using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.App;

/// <summary>
/// Footage and asset libraries over Joshua's own folders (indexed in place): search-as-you-type, #tags,
/// favorites, recents, type filters and hover previews.
/// </summary>
public sealed class LibraryPanel : DockPanel
{
    readonly Session s;
    readonly LibraryRole role;
    readonly TextBox search = new() { Name = "LibrarySearch", Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBlock partialNotice = Ui.Text("Partial matches", 12, true);
    readonly WrapPanel filters = new();
    readonly ListBox results = new() { Name = "LibraryResults", Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer hover = new() { Interval = TimeSpan.FromMilliseconds(450) };
    AssetCategory? category;
    bool favorites, recent;
    List<SearchHit> hits = [];
    SearchHit? hovered;

    public LibraryPanel(Session session, LibraryRole role)
    {
        s = session;
        this.role = role;
        LastChildFill = true;
        search.ToolTip = "Search names, folders, notes and #tags";
        // Placeholder text over the empty search box.
        var hint = Ui.Text(role == LibraryRole.Footage ? "⌕  Search footage, notes or #tags" : "⌕  Search music, SFX, images or #tags", 13, true);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(12, 0, 0, 8);
        search.TextChanged += (_, _) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var searchBox = new Grid { Children = { search, hint } };
        SetDock(searchBox, Dock.Top);
        Children.Add(searchBox);
        SetDock(filters, Dock.Top);
        Children.Add(filters);
        if (role == LibraryRole.Asset)
            foreach (var c in new[] { AssetCategory.Music, AssetCategory.Sfx, AssetCategory.Image, AssetCategory.Background, AssetCategory.Video })
            {
                var cat = c;
                ToggleButton? chip = null;
                chip = Ui.Chip(c == AssetCategory.Sfx ? "SFX" : c.ToString(), false, on =>
                {
                    category = on ? cat : null;
                    foreach (var other in filters.Children.OfType<ToggleButton>().Where(t => t != chip && t.Tag is AssetCategory)) other.IsChecked = false;
                    Refresh();
                });
                chip.Tag = c;
                filters.Children.Add(chip);
            }
        filters.Children.Add(Ui.Chip("★ Favorites", false, on => { favorites = on; Refresh(); }));
        filters.Children.Add(Ui.Chip("Recent", false, on => { recent = on; Refresh(); }));
        var add = Ui.Button(role == LibraryRole.Footage ? "Add footage folder…" : "Add asset folder…", AddFolder);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 0, 0, 8);
        SetDock(add, Dock.Bottom);
        Children.Add(add);
        partialNotice.Visibility = Visibility.Collapsed;
        SetDock(partialNotice, Dock.Top);
        Children.Add(partialNotice);
        Children.Add(results);
        results.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);

        search.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        debounce.Tick += (_, _) => Refresh();
        search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Down)
            {
                if (debounce.IsEnabled) Refresh();
                e.Handled = true;
                if (hits.Count == 0) return;
                if (e.Key == Key.Enter) _ = Activate(hits[0]);
                else
                {
                    results.SelectedIndex = 0;
                    results.ScrollIntoView(results.Items[0]);
                    results.UpdateLayout();
                    (results.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
                }
            }
            if (e.Key == Key.Escape) { Keyboard.ClearFocus(); e.Handled = true; }
        };
        results.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && results.SelectedIndex >= 0) { _ = Activate(hits[results.SelectedIndex]); e.Handled = true; }
        };
        results.MouseDoubleClick += (_, _) => { if (results.SelectedIndex >= 0) _ = Activate(hits[results.SelectedIndex]); };
        hover.Tick += (_, _) => { hover.Stop(); if (hovered is { Item.Kind: MediaKind.Audio } h) _ = PreviewAudio(h.Item); };
        s.Library.Changed += () => Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        Loaded += (_, _) => Refresh();
    }

    public void SearchFor(string text)
    {
        search.Text = text;
        search.Focus();
        search.CaretIndex = text.Length;
    }

    void Refresh()
    {
        debounce.Stop();
        hits = s.Library.Search(search.Text, out bool partialMatches, role, category, favorites, recent, limit: 150).ToList();
        partialNotice.Visibility = partialMatches ? Visibility.Visible : Visibility.Collapsed;
        results.Items.Clear();
        if (hits.Count == 0)
        {
            var folders = role == LibraryRole.Footage ? s.Settings.FootageFolders : s.Settings.AssetFolders;
            results.Items.Add(new ListBoxItem
            {
                IsEnabled = false,
                Content = Ui.Text(folders.Count == 0 ? "Add the folders where your recordings live. Nothing gets moved or renamed." : "No matches. Try fewer words, or a #tag.", 12, true),
            });
            return;
        }
        foreach (var hit in hits) results.Items.Add(new ListBoxItem { Content = Row(hit), Padding = new Thickness(4) });
    }

    FrameworkElement Row(SearchHit hit)
    {
        var item = hit.Item;
        var thumb = new Border { Width = 96, Height = 54, CornerRadius = new CornerRadius(6), Background = Theme.Panel3Brush, Margin = new Thickness(0, 0, 10, 0), ClipToBounds = true };
        var strip = Path.Combine(Tools.CacheDir("thumbs"), item.Fingerprint + ".jpg");
        if (item.Kind == MediaKind.Video && File.Exists(strip))
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit(); img.UriSource = new Uri(strip); img.CacheOption = BitmapCacheOption.OnLoad; img.DecodePixelWidth = 960; img.EndInit();
                int frame = hit.Moment is { } m && item.Duration > 0 ? (int)Math.Clamp(m.In / item.Duration * 10, 0, 9) : 1;
                int w = img.PixelWidth / 10;
                thumb.Child = new Image { Source = new CroppedBitmap(img, new Int32Rect(frame * w, 0, w, img.PixelHeight)), Stretch = Stretch.UniformToFill };
                thumb.ToolTip = new Image { Source = img, Width = 960 };
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException) { }
        }
        else if (item.Kind == MediaKind.Image)
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit(); img.UriSource = new Uri(item.Path); img.CacheOption = BitmapCacheOption.OnLoad; img.DecodePixelWidth = 192; img.EndInit();
                thumb.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException) { }
        }
        else thumb.Child = new TextBlock { Text = item.Category == AssetCategory.Sfx ? "SFX" : item.Kind == MediaKind.Audio ? "♪" : "▶", Foreground = Theme.MutedBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };

        var name = hit.Moment is { } mo ? $"{(mo.Note.Length > 0 ? mo.Note : "Moment")} · {item.Name}" : item.Name;
        var length = hit.Moment is { } mm ? mm.Out - mm.In : item.Duration;
        var tags = (hit.Moment?.Tags ?? []).Concat(item.Tags).Distinct().Select(t => "#" + t);
        var info = Ui.Stack(new TextBlock { Text = name, Foreground = Theme.TextBrush, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = $"{TimeSpan.FromSeconds(length):m\\:ss}  {string.Join(" ", tags)}", Foreground = Theme.MutedBrush, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        var star = new Button { Content = item.Favorite ? "★" : "☆", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = item.Favorite ? Theme.B(Theme.Sub) : Theme.MutedBrush, ToolTip = "Favorite" };
        star.Click += (_, _) => s.Library.SetFavorite(item.Id, !item.Favorite);
        var row = Ui.Columns((thumb, GridLength.Auto), (info, new GridLength(1, GridUnitType.Star)), (star, GridLength.Auto));
        row.MouseEnter += (_, _) => { hovered = hit; hover.Stop(); hover.Start(); };
        row.MouseLeave += (_, _) => { if (hovered == hit) { hovered = null; hover.Stop(); if (!s.Playing) s.Player.Stop(); } };
        row.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragDrop.DoDragDrop(row, new DataObject(DataFormats.FileDrop, new[] { item.Path }), DragDropEffects.Copy);
        };
        return row;
    }

    async Task PreviewAudio(LibraryItem item)
    {
        if (s.Playing) return;
        try
        {
            var raw = await Tools.Run(Tools.Ffmpeg, [.. Tools.FfmpegBase, "-t", "8", "-i", item.Path, "-ac", "2", "-ar", "48000", "-f", "f32le", Path.Combine(Path.GetTempPath(), "cutroom-hover.f32")]);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Path.GetTempPath(), "cutroom-hover.f32"));
            if (hovered?.Item.Id != item.Id) return;
            s.Player.PlayBuffer(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray());
        }
        catch (ToolException) { }
    }

    /// <summary>Footage opens in the source viewer; assets go straight onto the timeline at the playhead.</summary>
    async Task Activate(SearchHit hit)
    {
        using var work = s.BeginProjectWork();
        s.Player.Stop();
        if (role == LibraryRole.Footage || hit.Item.Category == AssetCategory.Video)
        {
            await s.LoadSource(hit.Item, hit.Moment);
            s.Say("Source: " + hit.Item.Name + "   I/O to mark, F to fill the slot, T to tag");
            return;
        }
        var m = await s.Import(hit.Item.Path);
        if (hit.Item.Category == AssetCategory.Background && s.SelectedClip?.Card is not null)
        {
            var id = s.SelectedClipId!;
            s.Edit("Card background", p => p.FindClip(id)!.Card!.BackgroundMediaId = m.Id);
            return;
        }
        var track = hit.Item.Category switch { AssetCategory.Music => TrackRole.Music, AssetCategory.Sfx => TrackRole.Sfx, _ => TrackRole.Footage };
        var cmds = new EditCommands(s);
        s.Edit($"Place {m.Name}", p => cmds.Place(p, p.FindMedia(m.Id)!, p.Track(track), s.Playhead));
        s.Library.MarkUsed(hit.Item.Path);
    }

    void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = role == LibraryRole.Footage ? "Choose a footage folder" : "Choose an asset folder (music, SFX, images, backgrounds)" };
        if (dialog.ShowDialog() != true) return;
        var list = role == LibraryRole.Footage ? s.Settings.FootageFolders : s.Settings.AssetFolders;
        if (!list.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) list.Add(dialog.FolderName);
        s.Settings.Save();
        s.Library.Watch(dialog.FolderName, role);
        _ = JobQueue.Shared.Run($"Indexing {Path.GetFileName(dialog.FolderName)}", (p, ct) => s.Library.Scan(dialog.FolderName, role, p, ct), background: true);
    }

    /// <summary>T: tag the current source moment (or the whole file) with comma-separated tags.</summary>
    public void TagCurrent(Window owner)
    {
        if (s.LibraryItemId is not { } id) { s.Say("Open footage in the source viewer first."); return; }
        var known = string.Join(" ", s.Library.AllTags().Take(20).Select(t => "#" + t));
        var answer = Ui.Ask(owner, "Tag", $"Tags for this {(s.SourceIn is not null ? "moment" : "file")}, comma separated.\nExisting: {known}");
        if (string.IsNullOrWhiteSpace(answer)) return;
        var tags = answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (s.SourceIn is { } a)
        {
            long b = s.SourceOut ?? a + Time.FromSeconds(5);
            s.Library.AddMoment(id, Time.ToSeconds(a), Time.ToSeconds(b), "", tags);
        }
        else foreach (var t in tags) s.Library.Tag(id, t);
        s.Say("Tagged " + string.Join(", ", tags));
    }

    /// <summary>Ctrl+M: save the source in/out as a named moment.</summary>
    public void SaveMoment(Window owner)
    {
        if (s.LibraryItemId is not { } id || s.SourceIn is not { } a) { s.Say("Mark an in point in the source viewer first."); return; }
        long b = s.SourceOut ?? a + Time.FromSeconds(5);
        var note = Ui.Ask(owner, "Save moment", "Describe this moment (searchable later):");
        if (note is null) return;
        s.Library.AddMoment(id, Time.ToSeconds(a), Time.ToSeconds(b), note);
        s.Say("Moment saved");
    }
}
