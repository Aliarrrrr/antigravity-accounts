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
            string? dataRoot = null;
            if (e.Args.Length != 0)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--data-root" || !System.IO.Path.IsPathFullyQualified(e.Args[1]))
                    throw new ArgumentException("Usage: AGProfileHub.Gui.exe [--data-root <absolute-path>]");
                dataRoot = e.Args[1];
            }
            MainWindow = new MainWindow(dataRoot);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex is AGProfileHub.Core.HubException or ArgumentException ? ex.Message : "Hub could not open its configuration.",
                "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

