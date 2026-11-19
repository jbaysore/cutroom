using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Cutroom.Core;

namespace Cutroom.App;

internal sealed class ProjectsWindow : Window
{
    readonly ProjectCatalog catalog;
    readonly Action<Document> openDocument;
    readonly Func<Document>? currentDocument;
    readonly Action? prepareSwitch;
    readonly TextBox search = new() { Name = "ProjectSearch", MinWidth = 220 };
    readonly ListBox projects = new() { Name = "ProjectList", Background = Theme.PanelBrush, BorderThickness = new Thickness(0) };
    readonly TextBlock empty = Ui.Text("", 14, true);
    readonly TextBlock status = Ui.Text("", 13);
    readonly Button open;
    readonly Button rename;
    IReadOnlyList<ProjectEntry> entries = [];

    public ProjectsWindow(AppSettings settings, Action<Document> openDocument, Func<Document>? currentDocument = null,
        Action? prepareSwitch = null, string? startupError = null)
    {
        catalog = new ProjectCatalog(settings);
        this.openDocument = openDocument;
        this.currentDocument = currentDocument;
        this.prepareSwitch = prepareSwitch;
        Theme.Window(this);
        Title = "Projects · Cutroom";
        Width = 850;
        Height = 570;
        MinWidth = 600;
        MinHeight = 400;
        WindowStartupLocation = currentDocument is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = currentDocument is null;

        var title = Ui.Text("Projects", 30, false, FontWeights.SemiBold);
        var actions = Ui.Row(Ui.Button("New Project…", Create, primary: true), Ui.Button("Browse…", Browse));
        actions.Margin = new Thickness(0);
        var header = Ui.Columns((title, new GridLength(1, GridUnitType.Star)), (actions, GridLength.Auto));
        header.Margin = new Thickness(0, 0, 0, 18);
        var searchLabel = new Label { Content = "_Search projects", Target = search, Padding = new Thickness(0, 0, 0, 4) };
        AutomationProperties.SetName(search, "Search projects");
        AutomationProperties.SetName(projects, "Projects");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.VerticalAlignment = VerticalAlignment.Center;
        var listing = new Grid();
        listing.Children.Add(projects);
        listing.Children.Add(empty);
        ScrollViewer.SetHorizontalScrollBarVisibility(projects, ScrollBarVisibility.Disabled);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        projects.ItemContainerStyle = itemStyle;

        open = Ui.Button("Open", OpenSelected, primary: true);
        open.Name = "OpenProject";
        rename = Ui.Button("Rename…", Rename);
        var footer = currentDocument is null ? Ui.Row(open, rename) : Ui.Row(open, rename, Ui.Button("Back to Editor", Close));
        footer.Margin = new Thickness(0, 12, 0, 0);
        status.Margin = new Thickness(0, 8, 0, 0);
        var top = Ui.Stack(header, searchLabel, search);
        top.Margin = new Thickness(0, 0, 0, 12);
        var bottom = Ui.Stack(footer, status);
        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(bottom);
        layout.Children.Add(Ui.Card(listing, 6));
        Content = new Border { Padding = new Thickness(26), Child = layout };

        projects.SelectionChanged += (_, _) => UpdateActions();
        projects.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(projects, source) is ListBoxItem item &&
                item.Tag is ProjectEntry entry)
                OpenPath(entry.Path);
        };
        PreviewKeyDown += HandleKey;
        search.TextChanged += (_, _) => Filter();
        Loaded += (_, _) => search.Focus();
        Refresh(currentDocument?.Invoke().Path);
        if (startupError is not null) SetStatus(startupError, error: true);
    }

    ProjectEntry? Selected => (projects.SelectedItem as ListBoxItem)?.Tag as ProjectEntry;

    void Refresh(string? selected = null)
    {
        entries = catalog.Read(out int unavailable);
        Filter(selected);
        SetStatus(unavailable > 0 ? "Some project locations could not be read." : "", error: unavailable > 0);
    }

    void Filter(string? selected = null)
    {
        selected ??= Selected?.Path;
        var query = search.Text.Trim();
        projects.Items.Clear();
        foreach (var project in entries.Where(p => p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                     p.Path.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var detail = SamePath(project.Path, currentDocument?.Invoke().Path) ? "Current project" :
                project.Error is not null ? "Could not read project" : "";
            var name = Ui.Text(project.Name, 15, false, FontWeights.SemiBold);
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var location = Ui.Text(project.Path, 12, true);
            location.TextWrapping = TextWrapping.NoWrap;
            location.TextTrimming = TextTrimming.CharacterEllipsis;
            var saved = Ui.Text(project.SavedAt == DateTime.MinValue ? "" : project.SavedAt.ToString("g"), 12, true);
            saved.HorizontalAlignment = HorizontalAlignment.Right;
            var state = Ui.Text(detail, 12, true);
            state.HorizontalAlignment = HorizontalAlignment.Right;
            var row = Ui.Columns((Ui.Stack(name, location), new GridLength(1, GridUnitType.Star)),
                (Ui.Stack(saved, state), new GridLength(148)));
            row.Margin = new Thickness(9, 8, 9, 8);
            var item = new ListBoxItem { Tag = project, Content = row, ToolTip = project.Error ?? project.Path };
            AutomationProperties.SetName(item, string.Join(", ", new[] { project.Name, detail, saved.Text, project.Path }.Where(text => text.Length > 0)));
            projects.Items.Add(item);
            if (SamePath(project.Path, selected)) projects.SelectedItem = item;
        }
        if (projects.SelectedIndex < 0 && projects.Items.Count > 0) projects.SelectedIndex = 0;
        empty.Text = entries.Count == 0 ? "No projects yet. Create one or browse for an existing project." : "No matching projects.";
        empty.Visibility = projects.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions();
    }

    void UpdateActions()
    {
        open.IsEnabled = Selected is not null;
        rename.IsEnabled = Selected is { Error: null };
    }

    void HandleKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && currentDocument is not null)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && (search.IsKeyboardFocusWithin || projects.IsKeyboardFocusWithin))
        {
            e.Handled = true;
            OpenSelected();
        }
        else if (e.Key == Key.Down && search.IsKeyboardFocusWithin && projects.SelectedItem is ListBoxItem item)
        {
            e.Handled = true;
            projects.ScrollIntoView(item);
            item.Focus();
        }
    }

    void Create()
    {
        var name = Ui.Ask(this, "New Project", "Project name", "");
        if (name is null) return;
        Run(() =>
        {
            prepareSwitch?.Invoke();
            OpenDocument(catalog.Create(name));
        });
    }

    void Browse() => Run(() =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a Cutroom project",
            Filter = "Cutroom projects|*.cutroom.json",
            InitialDirectory = catalog.Root,
        };
        if (dialog.ShowDialog(this) == true) OpenPath(dialog.FileName);
    });

    void OpenSelected()
    {
        if (Selected is { } entry) OpenPath(entry.Path);
    }

    void OpenPath(string path) => Run(() =>
    {
        if (SamePath(path, currentDocument?.Invoke().Path))
        {
            Close();
            return;
        }
        prepareSwitch?.Invoke();
        OpenDocument(Document.Open(path));
    });

    void OpenDocument(Document document)
    {
        openDocument(document);
        Close();
    }

    void Rename()
    {
        if (Selected is not { } entry) return;
        var name = Ui.Ask(this, "Rename Project", "Project name", entry.Name);
        if (name is null) return;
        Run(() =>
        {
            var active = currentDocument?.Invoke();
            var document = SamePath(entry.Path, active?.Path) ? active! : Document.Open(entry.Path);
            document.Rename(name);
            if (!document.Project.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) &&
                !entry.Path.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                search.Clear();
            Refresh(entry.Path);
        });
    }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ProjectCatalog.IsProjectError(ex)) { SetStatus(ex.Message, error: true); }
    }

    void SetStatus(string message, bool error)
    {
        status.Text = message;
        status.Foreground = error ? Theme.DangerBrush : Theme.MutedBrush;
        status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static bool SamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
