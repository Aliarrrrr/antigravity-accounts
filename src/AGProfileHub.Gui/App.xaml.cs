using System.Configuration;
using System.Data;
using System.Windows;

namespace AGProfileHub.Gui;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string? dataRoot = null, smokeImage = null;
            var sharedImage = false;
            for (var i = 0; i < e.Args.Length; i += 2)
            {
                if (i + 1 >= e.Args.Length || !System.IO.Path.IsPathFullyQualified(e.Args[i + 1])) throw new ArgumentException("Arguments require absolute paths.");
                if (e.Args[i] == "--data-root") dataRoot = e.Args[i + 1];
                else if (e.Args[i] == "--smoke-image") smokeImage = e.Args[i + 1];
                else if (e.Args[i] == "--shared-smoke-image") { smokeImage = e.Args[i + 1]; sharedImage = true; }
                else throw new ArgumentException("Unknown GUI argument.");
            }
            MainWindow = new MainWindow(dataRoot);
            MainWindow.Show();
            Window captureWindow = MainWindow;
            if (sharedImage)
            {
                captureWindow = new Views.SharedWorkspacesWindow(new AGProfileHub.Core.NextHubService(dataRoot)) { Owner = MainWindow };
                captureWindow.Show(); MainWindow.Hide();
            }
            if (smokeImage is not null)
            {
                var output = smokeImage;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                var attempts = 0;
                timer.Tick += (_, _) =>
                {
                    if ((captureWindow is MainWindow window && !window.InitialLoadComplete) ||
                        (captureWindow is Views.SharedWorkspacesWindow shared && !shared.InitialLoadComplete))
                    { if (++attempts < 10) return; timer.Stop(); Console.Error.WriteLine("GUI smoke failed: initial-load-timeout."); Shutdown(1); return; }
                    timer.Stop();
                    try
                    {
                        captureWindow.UpdateLayout();
                        var visual = (System.Windows.Media.Visual)captureWindow.Content;
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)captureWindow.ActualWidth, (int)captureWindow.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(visual);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var stream = System.IO.File.Create(output); encoder.Save(stream);
                        Shutdown(0);
                    }
                    catch (Exception ex) { Console.Error.WriteLine("GUI smoke failed: screenshot-" + ex.GetType().Name + "."); Shutdown(1); }
                };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex is AGProfileHub.Core.HubException or ArgumentException ? ex.Message : "Hub could not open its configuration.",
                "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
