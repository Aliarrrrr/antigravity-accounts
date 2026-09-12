using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AGProfileHub.Core;
using AGProfileHub.Gui.Views;

namespace AGProfileHub.Gui;

public partial class MainWindow : Window
{
    private readonly HubService _service;
    private readonly ObservableCollection<ProfileRow> _rows = [];
    private bool _busy;

    public MainWindow() : this(null) { }

    public MainWindow(string? dataRoot)
    {
        _service = HubBootstrap.CreateDefault(dataRoot);
        // The version is part of the window identity: the product refuses to run
        // against unverified Antigravity versions, so reports must name it.
        Title = $"Antigravity Profile Hub {HubVersion.InformationalVersion}";
        InitializeComponent();
        ProfilesGrid.ItemsSource = _rows;
        DataRootText.Text = _service.DataRoot;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var profiles = await _service.ListProfilesAsync().ConfigureAwait(true);
            var tasks = profiles.Select(async profile =>
            {
                try
                {
                    return ProfileRow.FromStatus(await _service.GetStatusAsync(profile.Id).ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    return ProfileRow.FromStatus(new ProfileStatus(profile.Id, profile.Name, RuntimeState.Unknown, null,
                        FormatException(ex), profile.WorkspacePath));
                }
            });
            var rows = await Task.WhenAll(tasks).ConfigureAwait(true);
            _rows.Clear();
            foreach (var row in rows) _rows.Add(row);
            UpdateChrome();
        }
        catch (Exception ex)
        {
            StatusText.Text = FormatException(ex);
        }
    }

    private void UpdateChrome()
    {
        var allIsolated = _rows.All(row => row.IsolationBoxName is not null);
        LaunchAllButton.IsEnabled = _rows.Count <= 1 || allIsolated;
        LaunchAllButton.ToolTip = LaunchAllButton.IsEnabled ? null :
            "Launch all requires every Profile to have a prepared isolation scope.";
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfilesGrid.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var needsFixIds = new HashSet<Guid>(_rows.Where(row => row.NeedsFix).Select(row => row.ProfileId));
        var deeplinkHintDue = needsFixIds.Count == 0 && _rows.Count >= 2 &&
            !_deeplinkHintDismissed && !_service.IsDeepLinkRegistered;
        // A dismissal only sticks while the situation it described is unchanged:
        // a different set of profiles needing fix, or a second profile joining
        // without callback routing, must surface the bar again.
        var fixSetChanged = needsFixIds.Count > 0 &&
            (_dismissedNeedsFix is null || !needsFixIds.SetEquals(_dismissedNeedsFix));
        if (needsFixIds.Count > 0 && (!_infoBarDismissed || fixSetChanged))
        {
            InfoText.Text = needsFixIds.Count == 1
                ? "1 profile has incomplete isolation. Use ⋯ → Fix isolation to repair it with one click."
                : $"{needsFixIds.Count} profiles have incomplete isolation. Use ⋯ → Fix isolation on each to repair it.";
            InfoBar.Visibility = Visibility.Visible;
            _infoMode = InfoMode.Fix;
            _infoBarDismissed = false;
            _dismissedNeedsFix = null;
        }
        else if (deeplinkHintDue)
        {
            InfoText.Text = "Two or more profiles can sign in at the same time. Register the callback routing once " +
                "with the Callbacks button so concurrent logins reach the right window; the previous handler stays restorable.";
            InfoBar.Visibility = Visibility.Visible;
            _infoMode = InfoMode.Deeplink;
        }
        else
        {
            InfoBar.Visibility = Visibility.Collapsed;
            _infoMode = InfoMode.None;
            if (needsFixIds.Count == 0) { _infoBarDismissed = false; _dismissedNeedsFix = null; }
        }
        StatusText.Text = $"{_rows.Count} profile(s)";
    }

    private enum InfoMode { None, Fix, Deeplink }
    private InfoMode _infoMode;
    private bool _infoBarDismissed;
    private HashSet<Guid>? _dismissedNeedsFix;
    private bool _deeplinkHintDismissed;

    private void DismissInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_infoMode == InfoMode.Fix)
        {
            _infoBarDismissed = true;
            _dismissedNeedsFix = new HashSet<Guid>(_rows.Where(row => row.NeedsFix).Select(row => row.ProfileId));
        }
        else if (_infoMode == InfoMode.Deeplink)
        {
            _deeplinkHintDismissed = true;
        }
        InfoBar.Visibility = Visibility.Collapsed;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, "Create profile", "Profile name (1-80 printable characters). The Hub prepares its isolation automatically.");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(async () =>
        {
            var profile = await _service.CreateProfileAsync(name);
            try
            {
                await _service.EnsureIsolationReadyAsync(profile.Id);
            }
            catch (UnsupportedCapabilityException ex)
            {
                StatusText.Text = $"Profile created, but isolation was not prepared: {ex.Message}";
            }
            await RefreshAsync();
        });
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        var name = InputDialog.Ask(this, "Rename profile", "New display name. The UUID is unchanged.");
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
        var path = InputDialog.Ask(this, "Workspace metadata", "Workspace path (leave blank to clear). Recorded as metadata only; the desktop does not open it automatically.");
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
        if (MessageBox.Show("Remove registration? Profile data and its isolation scope are kept.", "Remove profile",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunAsync(async () =>
        {
            await _service.RemoveProfileAsync(id);
            await RefreshAsync();
        });
    }

    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        var running = _rows.FirstOrDefault(row => row.ProfileId == id)?.IsRunning == true;
        await RunAsync(async () =>
        {
            if (running)
            {
                await _service.StopAsync(id);
            }
            else
            {
                await EnsureIsolationThenLaunchAsync(id);
            }
            await RefreshAsync();
        });
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private async Task EnsureIsolationThenLaunchAsync(Guid id)
    {
        try
        {
            if (await _service.DetectIsolationStageAsync(id) != IsolationStage.NotStarted)
            {
                var ready = await _service.EnsureIsolationReadyAsync(id);
                if (!ready.Ready) throw new HubException($"Isolation is not ready: {ready.Stage}.", "isolation_not_ready");
            }
        }
        catch (UnsupportedCapabilityException) { }
        await _service.LaunchAsync(id);
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileId(sender, out var id))
            await RunAsync(async () => { await EnsureIsolationThenLaunchAsync(id); await RefreshAsync(); });
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

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        await RunAsync(async () =>
        {
            var report = await _service.EnsureIsolationReadyAsync(id);
            StatusText.Text = report.Ready ? "Isolation is ready." : $"Isolation stage: {report.Stage}.";
            await RefreshAsync();
        });
    }

    private async void PrepareIsolation_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        await RunAsync(async () =>
        {
            var result = await _service.PrepareIsolationAsync(id);
            StatusText.Text = $"Isolation ready: {result.BoxName}.";
            await RefreshAsync();
        });
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

    private async void DeepLink_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(() =>
        {
            if (_service.IsDeepLinkRegistered)
            {
                var answer = MessageBox.Show(this,
                    "Sign-in callbacks are currently routed through this Hub.\n\nRestore the previous antigravity:// handler?",
                    "Deep-link routing", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                    StatusText.Text = _service.RestoreDeepLink()
                        ? "The previous antigravity:// handler was restored."
                        : "Nothing was restored: this Hub no longer owns the registration.";
            }
            else
            {
                var answer = MessageBox.Show(this,
                    "Route antigravity:// sign-in callbacks through this Hub, so concurrent logins reach the right window?\n\nThe previous handler is backed up and can be restored here or with 'agprofile configure-deeplink restore'.",
                    "Deep-link routing", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    _service.RegisterDeepLink(DesktopShortcut.FindCliExecutable(AppContext.BaseDirectory));
                    StatusText.Text = "Sign-in callbacks now route through this Hub.";
                }
            }
            return Task.CompletedTask;
        });
    }
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
        if (_busy)
        {
            // A click during a long launch must not vanish silently.
            StatusText.Text = "An operation is already in progress; wait for it to finish…";
            return;
        }
        _busy = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try { await action(); }
        catch (Exception ex)
        {
            var openLogs = MessageBox.Show(FormatException(ex) + "\n\nOpen the log folder?",
                "Operation failed", MessageBoxButton.YesNo, MessageBoxImage.Error) == MessageBoxResult.Yes;
            if (openLogs) OpenFolderSafely(_service.OpenLogsFolder);
            await RefreshAsync();
        }
        finally
        {
            _busy = false;
            Mouse.OverrideCursor = null;
        }
    }

    private static void OpenFolderSafely(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(FormatException(ex), "Open folder failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static bool TryGetProfileId(object sender, out Guid id) =>
        Guid.TryParse((sender as FrameworkElement)?.Tag?.ToString(), out id) && id != Guid.Empty;

    private static string FormatException(Exception ex) =>
        ex is HubException hub ? $"[{hub.Code}] {hub.Message}" : $"Unexpected error: {ex.Message}";

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private sealed class ProfileRow
    {
        public required Guid ProfileId { get; init; }
        public required string Name { get; init; }
        public required string StateText { get; init; }
        public required Brush StateBg { get; init; }
        public required Brush StateFg { get; init; }
        public string? Detail { get; init; }
        public int? RootProcessId { get; init; }
        public string? WorkspacePath { get; init; }
        public string Isolation { get; init; } = "";
        public string? IsolationBoxName { get; init; }
        public bool IsRunning { get; init; }

        public string PrimaryActionText => IsRunning ? "Close" : "Open";
        public string PidText => RootProcessId?.ToString() ?? "—";
        public string WorkspaceText => string.IsNullOrWhiteSpace(WorkspacePath) ? "—" : WorkspacePath!;
        public bool NeedsFix => Isolation != "Ready" && Isolation != "Direct / serial";

        public static ProfileRow FromStatus(ProfileStatus status)
        {
            var (text, bg, fg) = status.State switch
            {
                RuntimeState.Running => ("Running", "Brush.SuccessBg", "Brush.Success"),
                RuntimeState.Stopped => ("Stopped", "Brush.SurfaceAlt", "Brush.TextSecondary"),
                _ => ("Unknown", "Brush.WarningBg", "Brush.Warning")
            };
            var isolation = status.IsolationBoxName is null
                ? "Direct / serial"
                : status.IsolationReady == true ? "Ready" : "Needs fix";
            return new ProfileRow
            {
                ProfileId = status.ProfileId,
                Name = status.Name,
                StateText = text,
                StateBg = Res(bg),
                StateFg = Res(fg),
                Detail = status.Detail,
                RootProcessId = status.Root?.ProcessId,
                WorkspacePath = status.WorkspacePath,
                Isolation = isolation,
                IsolationBoxName = status.IsolationBoxName,
                IsRunning = status.State == RuntimeState.Running
            };
        }
    }
}
