using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Net.Sockets;

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
        var window = new MainWindow(e.Args.FirstOrDefault(a => a.EndsWith(".cutroom.json", StringComparison.OrdinalIgnoreCase)));
        MainWindow = window;
        try
        {
            operatorServer = new LocalOperatorServer(window);
            operatorServer.Start();
        }
        catch (SocketException ex)
        {
            operatorServer?.Dispose();
            operatorServer = null;
            MessageBox.Show($"Cutroom opened, but its local Project Hub operator could not start on port {LocalOperatorServer.Port}.\n\n{ex.Message}", "Cutroom", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        window.Show();
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
            var window = new MainWindow(project)
            {
                WindowState = WindowState.Normal, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            };
            MainWindow = window;
            window.Show();
            foreach (var (label, w, h) in new[] { ("1440p", 2560, 1400), ("1080p", 1920, 1040) })
            {
                window.Width = w;
                window.Height = h;
                foreach (var stage in Enum.GetValues<Stage>())
                {
                    window.Show(stage);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    await Task.Delay(300);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var root = (FrameworkElement)window.Content;
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    var bg = new DrawingVisual();
                    using (var dc = bg.RenderOpen()) dc.DrawRectangle(Theme.BgBrush, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                    bitmap.Render(bg);
                    bitmap.Render(root);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    await using var file = File.Create(Path.Combine(outDir, $"{label}-{stage}.png"));
                    png.Save(file);
                }
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
}
