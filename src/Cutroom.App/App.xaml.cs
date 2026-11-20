using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Net.Sockets;
using Cutroom.Core;
using Cutroom.Library;

namespace Cutroom.App;

public partial class App : Application
{
    LocalOperatorServer? operatorServer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.Apply(Resources);
        int smoke = Array.IndexOf(e.Args, "--smoke");
        if (smoke >= 0 && smoke + 1 < e.Args.Length) { ShutdownMode = ShutdownMode.OnExplicitShutdown; _ = Smoke(e.Args[smoke + 1], e.Args.Skip(smoke + 2).FirstOrDefault()); return; }
        DispatcherUnhandledException += OnUnhandled;
        var settings = AppSettings.Load();
        var path = e.Args.FirstOrDefault(a => a.EndsWith(ProjectCatalog.Extension, StringComparison.OrdinalIgnoreCase));
        string? startupError = null;
        Document? document = null;
        if (path is not null)
        {
            try { document = Document.Open(Path.GetFullPath(path)); }
            catch (Exception ex) when (ProjectCatalog.IsProjectError(ex))
            {
                startupError = "Could not open the project: " + ex.Message;
            }
        }
        if (document is null)
        {
            MainWindow = new ProjectsWindow(settings, selected => OpenEditor(settings, selected), startupError: startupError);
            MainWindow.Show();
        }
        else OpenEditor(settings, document);

        try
        {
            operatorServer = new LocalOperatorServer(Dispatcher, () =>
            {
                if (MainWindow is MainWindow editor) editor.CloseFromOperator();
                else MainWindow?.Close();
            });
            operatorServer.Start();
        }
        catch (SocketException ex)
        {
            operatorServer?.Dispose();
            operatorServer = null;
            MessageBox.Show(MainWindow, $"Cutroom opened, but its local Project Hub operator could not start on port {LocalOperatorServer.Port}.\n\n{ex.Message}", "Cutroom", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OpenEditor(AppSettings settings, Document document)
    {
        var window = new MainWindow(settings, document);
        MainWindow = window;
        window.Show();
        window.Session.RememberProject();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        operatorServer?.Dispose();
        base.OnExit(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the editor alive: autosave has the project, and the user sees what went wrong.
        e.Handled = true;
        try { (Current.MainWindow as MainWindow)?.Session.SaveSafely(); } catch (Exception) { }
        MessageBox.Show(e.Exception.Message, "Cutroom hit a problem", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// UI smoke: renders every stage off-screen at 1440p and 1080p window sizes to PNGs, then exits (code 1 on any error).
    /// Run with CUTROOM_DATA pointing at a scratch folder so real settings and projects are untouched.
    /// </summary>
    async Task Smoke(string outDir, string? project)
    {
        int code = 0;
        try
        {
            Directory.CreateDirectory(outDir);
            var settings = AppSettings.Load();
            var catalog = new ProjectCatalog(settings);
            var document = project is null ? catalog.Create("Sample edit") : Document.Open(Path.GetFullPath(project));
            var next = project is null ? catalog.Create("Next video") : null;
            if (project is not null) catalog.Remember(document.Path!);
            var menu = new ProjectsWindow(settings, _ => { });
            ConfigureSmokeWindow(menu);
            menu.Show();
            await Capture(menu, Path.Combine(outDir, "projects.png"));
            menu.Close();

            var window = new MainWindow(settings, document);
            ConfigureSmokeWindow(window);
            MainWindow = window;
            window.Show();
            foreach (var (label, w, h) in new[] { ("1440p", 2560, 1400), ("1080p", 1920, 1040) })
            {
                window.Width = w;
                window.Height = h;
                foreach (var stage in Enum.GetValues<Stage>())
                {
                    window.Show(stage);
                    await Capture(window, Path.Combine(outDir, $"{label}-{stage}.png"));
                }
            }
            if (next is not null)
            {
                await CheckEditingWorkflow(window, Path.Combine(outDir, "projects.png"));
                CheckProjectSwitch(window, next);
            }
            window.Close();
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(outDir, "smoke-error.txt"), ex.ToString());
            code = 1;
        }
        Shutdown(code);
    }

    static async Task CheckEditingWorkflow(MainWindow window, string sampleImage)
    {
        static T Find<T>(DependencyObject root, string name) where T : FrameworkElement =>
            (T)LogicalTreeHelper.FindLogicalNode(root, name);

        static void Press(FrameworkElement control, Key key) =>
            control.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control)!, 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var session = window.Session;
        window.Show(Stage.Edit);
        var edit = (EditView)LogicalTreeHelper.FindLogicalNode(window, "EditStage");
        foreach (var (panel, role, tab) in new[]
        {
            (edit.Footage, LibraryRole.Footage, 0),
            (edit.Assets, LibraryRole.Asset, 1)
        })
        {
            async Task<MediaRef> Add(string name)
            {
                string path = Path.Combine(session.ProjectFolder, $"{name}-{role}.png");
                File.Copy(sampleImage, path);
                await session.Library.AddOrUpdate(path, role);
                return await session.Import(path);
            }

            await Add("oldquery");
            var destination = await Add("newquery");
            edit.ShowTab(tab);
            window.UpdateLayout();
            var search = Find<TextBox>(panel, "LibrarySearch");
            var results = Find<ListBox>(panel, "LibraryResults");
            search.Text = "oldquery";
            Press(search, Key.Down);
            var sourceBefore = session.Source;
            search.Text = "missingquery";
            Press(search, Key.Enter);
            if (results.SelectedIndex != -1 || session.Source != sourceBefore || session.P.Track(TrackRole.Footage).Clips.Count > 0)
                throw new InvalidOperationException("Library Enter reused a result from the old query.");
            search.Text = "newquery";
            Press(search, Key.Down);
            if (results.SelectedIndex != 0)
                throw new InvalidOperationException($"{role} library Down did not select the new query result.");
            search.Text = "oldquery";
            Press(search, Key.Down);
            search.Text = "newquery";
            Press(search, Key.Enter);
            if (role == LibraryRole.Footage ? session.Source?.Id != destination.Id
                : session.P.Track(TrackRole.Footage).At(0)?.MediaId != destination.Id)
                throw new InvalidOperationException("Library Enter did not activate the new query result.");
            session.SeekTimeline(0);
        }

        var region = new SubtitleRegion { Start = 0, End = Time.FromSeconds(3) };
        session.Edit("Smoke captions and gaps", project =>
        {
            project.Slots.Add(new Slot { Start = 0, End = Time.FromSeconds(5) });
            project.Track(TrackRole.Narration).Clips.Add(new Clip { Duration = Time.FromSeconds(5) });
            project.SubtitleRegions.Add(region);
            project.Captions.Add(new Caption { Text = "First", FixedStart = 0, FixedEnd = Time.FromSeconds(1) });
            project.Captions.Add(new Caption { Text = "Second", FixedStart = Time.FromSeconds(1), FixedEnd = Time.FromSeconds(2) });
        });
        window.Show(Stage.Export);
        session.ShowingSource = true;
        Click(Find<Button>(window, "GoToVisualGap"));
        if (window.Current != Stage.Edit || session.ShowingSource || session.Playhead != Time.FromSeconds(4))
            throw new InvalidOperationException("Export did not jump to the first uncovered frame.");
        session.Seek(0);
        session.SelectRegion(region.Id);
        window.UpdateLayout();
        var caption = Find<TextBox>(edit.Inspector, "CaptionText");
        caption.Text = "First corrected";
        Press(caption, Key.Enter);
        if (caption.Text != "Second" || session.Playhead != Time.FromSeconds(1) || session.P.Captions[0].Text != "First corrected")
            throw new InvalidOperationException("Caption Enter did not save and move forward.");
        caption.Text = "Second corrected";
        Press(caption, Key.Enter);
        if (session.P.Captions[1].Text != "Second corrected" || session.Playhead != Time.FromSeconds(1))
            throw new InvalidOperationException("Caption Enter did not save at the region boundary.");
        Click(Find<Button>(edit.Inspector, "PreviousCaption"));
        if (caption.Text != "First corrected" || session.Playhead != 0)
            throw new InvalidOperationException("Previous caption did not return to the first caption.");
        session.Undo();
        if (session.P.Captions[0].Text != "First corrected" || session.P.Captions[1].Text != "Second")
            throw new InvalidOperationException("Undo did not revert just the last caption edit.");
    }

    static void CheckProjectSwitch(MainWindow window, Document next)
    {
        var previous = window.Session.Doc;
        previous.Edit("Smoke edit", project => project.Name = "Unsaved smoke edit");
        var menu = window.CreateProjectsWindow();
        ConfigureSmokeWindow(menu);
        menu.Owner = window;
        menu.Show();
        var search = (TextBox)LogicalTreeHelper.FindLogicalNode(menu, "ProjectSearch");
        var list = (ListBox)LogicalTreeHelper.FindLogicalNode(menu, "ProjectList");
        var open = (Button)LogicalTreeHelper.FindLogicalNode(menu, "OpenProject");
        search.Text = next.Project.Name;
        if (list.Items.Count != 1)
            throw new InvalidOperationException("Project search did not narrow the list to the destination.");
        using (window.Session.BeginProjectWork())
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (window.Session.Doc != previous || !menu.IsVisible)
            throw new InvalidOperationException("The editor switched while project work was active.");

        var blocker = previous.Path + ".partial";
        Directory.CreateDirectory(blocker);
        try { open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        finally { Directory.Delete(blocker); }
        if (window.Session.Doc != previous || !previous.Dirty || !menu.IsVisible)
            throw new InvalidOperationException("The editor switched after the current project failed to save.");

        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (window.Session.Doc.Path != next.Path || window.Current != Stage.Record || menu.IsVisible ||
            Document.Open(previous.Path!).Project.Name != "Unsaved smoke edit")
            throw new InvalidOperationException("Project switch did not save the previous edit and open the destination.");
    }

    static void ConfigureSmokeWindow(Window window)
    {
        window.WindowState = WindowState.Normal;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -20000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    async Task Capture(Window window, string path)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen()) dc.DrawRectangle(Theme.BgBrush, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }
}
