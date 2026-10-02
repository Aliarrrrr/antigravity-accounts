using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AGProfileHub.Core;

namespace AGProfileHub.Gui;

public partial class MainWindow : Window
{
    private readonly HubService _service;
    private readonly ObservableCollection<ProfileRow> _rows = [];

    public MainWindow() : this(null) { }

    public MainWindow(string? dataRoot)
    {
        _service = HubBootstrap.CreateDefault(dataRoot);
        InitializeComponent();
        ProfilesGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var profiles = await _service.ListProfilesAsync().ConfigureAwait(true);
            _rows.Clear();
            foreach (var profile in profiles)
            {
                ProfileStatus status;
                try
                {
                    status = await _service.GetStatusAsync(profile.Id).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    status = new ProfileStatus(profile.Id, profile.Name, RuntimeState.Unknown, null,
                        FormatException(ex), profile.WorkspacePath);
                }
                _rows.Add(new ProfileRow(status));
            }
            LaunchAllButton.IsEnabled = _rows.Count <= 1;
            LaunchAllButton.ToolTip = _rows.Count <= 1 ? null :
                "Disabled because Desktop 2.12.2 cannot safely run multiple Google-account profiles concurrently.";
            StatusText.Text = $"{_rows.Count} profile(s) · Data: {_service.DataRoot}";
        }
        catch (Exception ex)
        {
            StatusText.Text = FormatException(ex);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("Profile name:", "Create profile");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(async () =>
        {
            await _service.CreateProfileAsync(name);
            await RefreshAsync();
        });
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        var name = Prompt("New profile name:", "Rename profile");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(async () =>
        {
            await _service.RenameProfileAsync(id, name);
            await RefreshAsync();
        });
    }

    private async void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        var path = Prompt("Workspace path (leave blank to clear metadata):", "Workspace metadata");
        if (path is null) return;
        await RunAsync(async () =>
        {
            await _service.SetWorkspaceAsync(id, string.IsNullOrWhiteSpace(path) ? null : path);
            await RefreshAsync();
        });
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        if (MessageBox.Show("Remove registration? Profile data is kept.", "Remove profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunAsync(async () =>
        {
            await _service.RemoveProfileAsync(id);
            await RefreshAsync();
        });
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileId(sender, out var id))
            await RunAsync(async () => { await _service.LaunchAsync(id); await RefreshAsync(); });
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileId(sender, out var id))
            await RunAsync(async () => { var result = await _service.StopAsync(id); await RefreshAsync(); EnsureStopped(result); });
    }

    private async void ForceStop_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileId(sender, out var id))
            await RunAsync(async () => { await _service.StopAsync(id, force: true); await RefreshAsync(); });
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileId(sender, out var id))
            await RunAsync(async () => { await _service.RestartAsync(id); await RefreshAsync(); });
    }

    private async void ForceRestart_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileId(sender, out var id))
            await RunAsync(async () => { await _service.RestartAsync(id, force: true); await RefreshAsync(); });
    }

    private async void LaunchAll_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            await _service.LaunchAllAsync();
            await RefreshAsync();
        });
    }

    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var results = new List<StopResult>();
            foreach (var profile in await _service.ListProfilesAsync()) results.Add(await _service.StopAsync(profile.Id));
            await RefreshAsync();
            foreach (var result in results) EnsureStopped(result);
        });
    }

    private async void ForceStopAll_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            foreach (var profile in await _service.ListProfilesAsync()) await _service.StopAsync(profile.Id, force: true);
            await RefreshAsync();
        });
    }

    private async void Doctor_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var report = await _service.DoctorAsync();
            var text = new StringBuilder()
                .AppendLine($"Executable: {(report.ExecutableFound ? report.ExecutablePath : "not found")}")
                .AppendLine($"Version: {report.ExecutableVersion ?? "unknown"}")
                .AppendLine($"Experimental launch: {report.CompatibleForExperimentalLaunch}");
            foreach (var capability in report.Capabilities)
                text.AppendLine($"{capability.Name}: {capability.State} — {capability.Detail}");
            foreach (var warning in report.Warnings)
                text.AppendLine($"Warning: {warning}");
            MessageBox.Show(text.ToString(), "Doctor", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolderSafely(() => _service.OpenDataFolder());
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Select Antigravity executable", Filter = "Antigravity executable (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        await RunAsync(async () => { await _service.ConfigureExecutableAsync(picker.FileName); await RefreshAsync(); });
    }

    private async void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        await RunAsync(async () =>
        {
            var path = await _service.CreateShortcutAsync(id, DesktopShortcut.FindCliExecutable(AppContext.BaseDirectory));
            StatusText.Text = $"Shortcut created: {path}";
        });
    }

    private static void EnsureStopped(StopResult result)
    {
        if (result.Status.State != RuntimeState.Stopped)
            throw new HubException(result.Status.Detail ?? "The profile is still running.", "graceful_close_pending");
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolderSafely(_service.OpenLogsFolder);

    private void Data_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileId(sender, out var id)) OpenFolderSafely(() => _service.OpenDataFolder(id));
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (!MainPanel.IsEnabled) return;
        MainPanel.IsEnabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            MessageBox.Show(FormatException(ex), "Operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
            await RefreshAsync();
        }
        finally { MainPanel.IsEnabled = true; }
    }

    private static void OpenFolderSafely(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(FormatException(ex), "Open folder failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static string? Prompt(string text, string title)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        var box = new TextBox();
        window.Loaded += (_, _) => box.Focus();
        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            Width = 70,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        ok.Click += (_, _) => window.DialogResult = true;
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(ok);
        window.Content = panel;
        return window.ShowDialog() == true ? box.Text : null;
    }

    private static bool TryGetProfileId(object sender, out Guid id) =>
        Guid.TryParse((sender as Button)?.Tag?.ToString(), out id) && id != Guid.Empty;

    private static string FormatException(Exception ex) =>
        ex is HubException hub ? $"[{hub.Code}] {hub.Message}" : "Unexpected error.";

    private sealed record ProfileRow(Guid ProfileId, string Name, RuntimeState State, int? RootProcessId, string? WorkspacePath, string? Detail)
    {
        public ProfileRow(ProfileStatus status) : this(status.ProfileId, status.Name, status.State, status.Root?.ProcessId, status.WorkspacePath, status.Detail) { }
    }
}
