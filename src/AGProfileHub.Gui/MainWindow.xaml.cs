using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AGProfileHub.Core;

namespace AGProfileHub.Gui;

public partial class MainWindow : Window
{
    private readonly HubService _service;
    private readonly ObservableCollection<ProfileRow> _rows = [];
    private bool _isBusy;
    private bool _profilesLoaded;

    public MainWindow() : this(null) { }

    public MainWindow(string? dataRoot)
    {
        _service = HubBootstrap.CreateDefault(dataRoot);
        InitializeComponent();
        Title = $"Antigravity 配置中心 · {HubVersion.Version}";
        ProfilesGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var selectedId = (ProfilesGrid.SelectedItem as ProfileRow)?.ProfileId;
        try
        {
            var profiles = await _service.ListProfilesAsync();
            Dictionary<Guid, ProfileStatus> statuses = [];
            string? statusError = null;
            try
            {
                statuses = (await _service.GetAllStatusesAsync()).ToDictionary(status => status.ProfileId);
            }
            catch (Exception ex)
            {
                statusError = $"状态读取失败：{FormatException(ex)}";
            }

            _rows.Clear();
            foreach (var profile in profiles)
            {
                var status = statuses.GetValueOrDefault(profile.Id)
                    ?? new ProfileStatus(profile.Id, profile.Name, RuntimeState.Unknown, null,
                        statusError ?? "未返回该配置的状态，请刷新或运行诊断。", profile.WorkspacePath);
                _rows.Add(new ProfileRow(profile.Id, profile.Name, profile.Backend, status.State,
                    status.Root?.ProcessId, profile.WorkspacePath, status.Detail));
            }
            _profilesLoaded = true;
            EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"共 {_rows.Count} 个配置 · 数据目录：{_service.DataRoot}"
                + (statusError is null ? string.Empty : $" · {statusError}（所有配置状态标记为未知）");
        }
        catch (Exception ex)
        {
            _profilesLoaded = false;
            var detail = $"配置列表读取失败：{FormatException(ex)}";
            for (var i = 0; i < _rows.Count; i++)
                _rows[i] = _rows[i] with { State = RuntimeState.Unknown, RootProcessId = null, Detail = detail };
            EmptyState.Visibility = Visibility.Collapsed;
            StatusText.Text = $"{detail}。已保留上次列表，请刷新或运行诊断。";
        }
        finally
        {
            ProfilesGrid.SelectedItem = selectedId.HasValue
                ? _rows.FirstOrDefault(row => row.ProfileId == selectedId.Value)
                : null;
            UpdateBatchActions();
        }
    }

    private bool CanLaunchAll => _profilesLoaded && (_rows.Count == 1
        || (_rows.Count > 1 && _rows.All(row => row.Backend == ProfileBackend.Sandboxie)));

    private void UpdateBatchActions()
    {
        var canLaunchAll = CanLaunchAll;
        LaunchAllMenuItem.IsEnabled = canLaunchAll;
        StopAllMenuItem.IsEnabled = ForceStopAllMenuItem.IsEnabled = _profilesLoaded && _rows.Count > 0;
        LaunchAllHint.Text = !_profilesLoaded ? "批量操作不可用：请先成功刷新配置列表。"
            : _rows.Count == 0 ? "先新建配置；普通 Direct 无需 Sandboxie。"
            : !canLaunchAll ? "启动全部已禁用：多个 Direct 或 Direct / Sandboxie 混合配置不支持批量启动。仅单个配置或全部 Sandboxie 可用。"
            : _rows.Count == 1 ? "可启动当前配置；普通停止会等待应用正常退出，请留意保存提示。"
            : "可批量启动全部 Sandboxie 配置；此后端为实验功能，多账号隔离需人工登录验证。";
        LaunchAllMenuItem.ToolTip = LaunchAllHint.Text;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var input = PromptCreateProfile();
            if (input is not { } request) return;
            var profile = await _service.CreateProfileAsync(request.Name, workspacePath: null, backend: request.Backend);
            await RefreshAsync();
            ProfilesGrid.SelectedItem = _rows.FirstOrDefault(row => row.ProfileId == profile.Id);
        });
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        await RunAsync(async () =>
        {
            var name = Prompt("新配置名称：", "重命名配置", FindRow(id)?.Name ?? string.Empty);
            if (string.IsNullOrWhiteSpace(name)) return;
            await _service.RenameProfileAsync(id, name.Trim());
            await RefreshAsync();
        });
    }

    private async void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        await RunAsync(async () =>
        {
            var path = Prompt("工作区路径（留空清除）。仅保存元数据，不会随启动传递给 Antigravity：",
                "工作区元数据", FindRow(id)?.WorkspacePath ?? string.Empty);
            if (path is null) return;
            await _service.SetWorkspaceAsync(id, string.IsNullOrWhiteSpace(path) ? null : path.Trim());
            await RefreshAsync();
        });
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        await RunAsync(async () =>
        {
            if (MessageBox.Show(this, $"移除配置「{FindRow(id)?.Name ?? id.ToString()}」的注册信息？\n配置数据将保留；运行中的配置需先停止。",
                    "移除配置", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            await _service.RemoveProfileAsync(id);
            await RefreshAsync();
        });
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
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
        if (!TryGetProfileId(sender, out var id)) return;
        await RunAsync(async () =>
        {
            if (!ConfirmForce("强制停止", id)) return;
            var result = await _service.StopAsync(id, force: true);
            await RefreshAsync();
            EnsureStopped(result);
        });
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileId(sender, out var id))
            await RunAsync(async () => { await _service.RestartAsync(id); await RefreshAsync(); });
    }

    private async void ForceRestart_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        await RunAsync(async () =>
        {
            if (!ConfirmForce("强制重启", id)) return;
            await _service.RestartAsync(id, force: true);
            await RefreshAsync();
        });
    }

    private bool ConfirmForce(string action, Guid? id = null)
    {
        var target = id.HasValue ? $"配置「{FindRow(id.Value)?.Name ?? id.Value.ToString()}」" : "全部配置";
        return MessageBox.Show(this, $"确定{action}{target}？\n\n未保存的数据可能丢失。请先保存工作；此操作不会等待应用的保存提示。",
            action, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async void LaunchAll_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if (!CanLaunchAll) return;
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
            if (!ConfirmForce("强制停止")) return;
            var results = new List<StopResult>();
            foreach (var profile in await _service.ListProfilesAsync()) results.Add(await _service.StopAsync(profile.Id, force: true));
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
                .AppendLine($"可执行文件：{(report.ExecutableFound ? report.ExecutablePath : "未找到")}")
                .AppendLine($"版本：{report.ExecutableVersion ?? "未知"}")
                .AppendLine($"符合实验启动条件：{(report.CompatibleForExperimentalLaunch ? "是" : "否")}");
            foreach (var capability in report.Capabilities)
                text.AppendLine($"{capability.Name}：{capability.State} — {capability.Detail}");
            foreach (var warning in report.Warnings)
                text.AppendLine($"警告：{warning}");
            MessageBox.Show(this, text.ToString(), "诊断报告", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolderSafely(() => _service.OpenDataFolder());

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var settings = await _service.GetSettingsAsync();
            var executable = settings.AntigravityExecutablePath;
            var sandboxieHome = settings.SandboxieHome;
            var window = CreateDialog("设置", 560);
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "Antigravity 可执行文件", FontWeight = FontWeights.SemiBold });
            var executableBox = new TextBox
            {
                Text = executable ?? "自动检测（尚未手动指定）", IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8)
            };
            panel.Children.Add(executableBox);
            var chooseExecutable = new Button { Content = "选择 exe…", HorizontalAlignment = HorizontalAlignment.Left };
            chooseExecutable.Click += (_, _) =>
            {
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择 Antigravity 可执行文件", Filter = "可执行文件 (*.exe)|*.exe", CheckFileExists = true
                };
                if (picker.ShowDialog(window) != true) return;
                executable = picker.FileName;
                executableBox.Text = executable;
            };
            panel.Children.Add(chooseExecutable);
            panel.Children.Add(new TextBlock
            {
                Text = "Sandboxie 安装目录（可选）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 0)
            });
            var sandboxieBox = new TextBox
            {
                Text = sandboxieHome ?? "自动检测（未指定目录）", IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8)
            };
            panel.Children.Add(sandboxieBox);
            var sandboxieActions = new WrapPanel();
            var chooseSandboxie = new Button { Content = "选择安装目录…", Margin = new Thickness(0, 0, 8, 0) };
            chooseSandboxie.Click += (_, _) =>
            {
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择 Sandboxie 安装目录", Multiselect = false };
                if (picker.ShowDialog(window) != true) return;
                sandboxieHome = picker.FolderName;
                sandboxieBox.Text = sandboxieHome;
            };
            var resetSandboxie = new Button { Content = "恢复自动检测" };
            resetSandboxie.Click += (_, _) =>
            {
                sandboxieHome = null;
                sandboxieBox.Text = "自动检测（未指定目录）";
            };
            sandboxieActions.Children.Add(chooseSandboxie);
            sandboxieActions.Children.Add(resetSandboxie);
            panel.Children.Add(sandboxieActions);
            panel.Children.Add(new TextBlock
            {
                Text = "普通 Direct 无需安装 Sandboxie。仅实验性 Sandboxie 后端需要本机安装 Sandboxie；留空将自动检测。点击保存后生效。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
            });
            AddDialogButtons(window, panel, "保存");
            window.Content = panel;
            if (window.ShowDialog() != true) return;
            if (executable is not null && !string.Equals(executable, settings.AntigravityExecutablePath, StringComparison.OrdinalIgnoreCase))
                await _service.ConfigureExecutableAsync(executable);
            if (!string.Equals(sandboxieHome, settings.SandboxieHome, StringComparison.OrdinalIgnoreCase))
                await _service.ConfigureSandboxieAsync(sandboxieHome);
            await RefreshAsync();
        });
    }

    private async void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProfileId(sender, out var id)) return;
        await RunAsync(async () =>
        {
            var path = await _service.CreateShortcutAsync(id, DesktopShortcut.FindCliExecutable(AppContext.BaseDirectory));
            StatusText.Text = $"已创建快捷方式：{path}";
        });
    }

    private static void EnsureStopped(StopResult result)
    {
        if (result.Status.State != RuntimeState.Stopped)
            throw new HubException(result.Status.Detail ?? "配置尚未停止，请检查应用中的保存提示。", "graceful_close_pending");
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolderSafely(_service.OpenLogsFolder);

    private void Data_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileId(sender, out var id)) OpenFolderSafely(() => _service.OpenDataFolder(id));
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_isBusy) return;
        _isBusy = true;
        MainPanel.IsEnabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, FormatException(ex), "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            await RefreshAsync();
        }
        finally
        {
            MainPanel.IsEnabled = true;
            _isBusy = false;
        }
    }

    private void OpenFolderSafely(Action action)
    {
        if (_isBusy) return;
        try { action(); }
        catch (Exception ex) { MessageBox.Show(this, FormatException(ex), "打开目录失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private Window CreateDialog(string title, double width) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
        ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        Background = Background, Foreground = Foreground, FontFamily = FontFamily, FontSize = FontSize,
        Resources = Resources
    };

    private static Button AddDialogButtons(Window window, Panel panel, string confirmText)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 76, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = confirmText, IsDefault = true, MinWidth = 76 };
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        return ok;
    }

    private (string Name, ProfileBackend Backend)? PromptCreateProfile()
    {
        var window = CreateDialog("新建配置", 520);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "配置名称", FontWeight = FontWeights.SemiBold });
        var nameBox = new TextBox { Margin = new Thickness(0, 8, 0, 16), Padding = new Thickness(8, 5, 8, 5) };
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "启动后端", FontWeight = FontWeights.SemiBold });
        var backendBox = new ComboBox { Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(8, 5, 8, 5) };
        backendBox.Items.Add(new ComboBoxItem { Content = "普通 Direct（默认）", Tag = ProfileBackend.Direct });
        backendBox.Items.Add(new ComboBoxItem { Content = "Sandboxie（实验）", Tag = ProfileBackend.Sandboxie });
        backendBox.SelectedIndex = 0;
        panel.Children.Add(backendBox);
        panel.Children.Add(new TextBlock
        {
            Text = "普通 Direct 无需 Sandboxie，不支持多个配置并发账号隔离。\n\nSandboxie 为可选实验后端，需本机安装，可在设置中指定安装目录或自动检测。\n\n登录始终通过 Antigravity / Google 官方流程；多账号隔离需人工登录并验证互不干扰，不能视为已验证能力。",
            TextWrapping = TextWrapping.Wrap
        });
        var ok = AddDialogButtons(window, panel, "创建");
        ok.IsEnabled = false;
        nameBox.TextChanged += (_, _) => ok.IsEnabled = !string.IsNullOrWhiteSpace(nameBox.Text);
        window.Loaded += (_, _) => nameBox.Focus();
        window.Content = panel;
        return window.ShowDialog() == true
            ? (nameBox.Text.Trim(), (ProfileBackend)((ComboBoxItem)backendBox.SelectedItem).Tag)
            : null;
    }

    private string? Prompt(string text, string title, string initialValue = "")
    {
        var window = CreateDialog(title, 480);
        var panel = new StackPanel { Margin = new Thickness(20) };
        var box = new TextBox { Text = initialValue, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 8, 0, 0) };
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        AddDialogButtons(window, panel, "保存");
        window.Content = panel;
        return window.ShowDialog() == true ? box.Text : null;
    }

    private ProfileRow? FindRow(Guid id) => _rows.FirstOrDefault(row => row.ProfileId == id);

    private static bool TryGetProfileId(object sender, out Guid id)
    {
        var tag = sender switch
        {
            MenuItem menuItem => menuItem.Tag,
            Button button => button.Tag,
            _ => null
        };
        return Guid.TryParse(tag?.ToString(), out id) && id != Guid.Empty;
    }

    private static string FormatException(Exception ex) =>
        ex is HubException hub ? $"[{hub.Code}] {hub.Message}" : "发生意外错误，请查看日志或运行诊断。";

    private sealed record ProfileRow(Guid ProfileId, string Name, ProfileBackend Backend, RuntimeState State,
        int? RootProcessId, string? WorkspacePath, string? Detail)
    {
        public string BackendText => Backend switch
        {
            ProfileBackend.Direct => "普通 Direct",
            ProfileBackend.Sandboxie => "Sandboxie（实验）",
            _ => "未知后端"
        };

        public string StateText => State switch
        {
            RuntimeState.Running => "运行中",
            RuntimeState.Stopped => "已停止",
            _ => "未知"
        };
    }
}
