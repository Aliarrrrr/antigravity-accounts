using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using AGProfileHub.Core;
using AGProfileHub.Gui.Views;

namespace AGProfileHub.Gui;
public partial class MainWindow : Window
{
    private readonly NextHubService _hub;
    public bool InitialLoadComplete { get; private set; }
    public MainWindow(string? dataRoot = null)
    {
        InitializeComponent(); _hub = new NextHubService(dataRoot);
        Loaded += async (_, _) => { await RunAsync(RefreshAsync); InitialLoadComplete = true; };
    }
    private Guid Selected => ProfilesGrid.SelectedItem is ProfileStatus row ? row.ProfileId : throw new HubException("请先选择 Profile。", "profile_required");
    private async Task RefreshAsync() { ProfilesGrid.ItemsSource = await _hub.StatusAllAsync(); }
    private async Task RunAsync(Func<Task> action)
    {
        Toolbar.IsEnabled = Actions.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { Output.Text = ex is HubException ? ex.Message : "操作未完成：" + ex.GetType().Name + "。文件已保留，请检查诊断。"; }
        finally { Toolbar.IsEnabled = Actions.IsEnabled = true; }
    }
    private void Show(object value) => Output.Text = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, "创建 Profile · 1/2", "账号标签由你填写；管理器不读取登录凭据。"); if (string.IsNullOrWhiteSpace(name)) return;
        var bridge = InputDialog.Ask(this, "创建 Profile · 2/2", "默认共享 .gitconfig（include）和 .ssh（整个目录）。输入 on 或 off；不共享 .gemini。", "on");
        if (bridge is not "on" and not "off") return;
        await RunAsync(async () => { var p = await _hub.Profiles.CreateAsync(name, null, IsolationBackendKind.ProcessLocal); Show(await _hub.Bridge.ApplyAsync(p.Id, bridge == "on", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))); await RefreshAsync(); });
    }
    private async void Configure_Click(object sender, RoutedEventArgs e)
    { var path = InputDialog.Ask(this, "应用路径", "已安装的官方 Antigravity.exe 路径", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity", "Antigravity.exe")); if (path is null) return; await RunAsync(async () => { await _hub.ConfigureExecutableAsync(path); Output.Text = "路径已配置；请探测兼容性。"; }); }
    private async void Probe_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { Output.Text = "正在独立临时 Profile 中探测，最长约两分钟；不要在探针窗口登录。"; Show(await _hub.ProbeAsync()); });
    private async void Doctor_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => Show(await _hub.CompatibilityAsync()));
    private async void Launch_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { var id = Selected; Output.Text = "正在验证独立文件令牌存储…"; Show(await _hub.LaunchAsync(id)); await RefreshAsync(); });
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { Show(new { stopped = await _hub.StopAsync(Selected) }); await RefreshAsync(); });
    private async void Force_Click(object sender, RoutedEventArgs e)
    { if (MessageBox.Show(this, "强制停止可能丢失未保存工作。继续？", "强制停止", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; await RunAsync(async () => { Show(new { stopped = await _hub.StopAsync(Selected, true) }); await RefreshAsync(); }); }
    private async void Login_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { _hub.RegisterProtocol(Cli()); await _hub.BeginLoginAsync(Selected); Output.Text = "此 Profile 已获得授权等待槽。现在在它的官方窗口完成登录；另一侧可保持运行，授权请依次完成。"; });
    private async void CancelLogin_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { await _hub.EndLoginAsync(Selected); Output.Text = "已取消此 Profile 的授权等待。"; });
    private async void Rename_Click(object sender, RoutedEventArgs e)
    { var name = InputDialog.Ask(this, "账号标签", "手填标签，不从 token 读取账号。"); if (name is null) return; await RunAsync(async () => { await _hub.Profiles.RenameAsync(Selected, name); await RefreshAsync(); }); }
    private async void Workspace_Click(object sender, RoutedEventArgs e)
    { var path = InputDialog.Ask(this, "Workspace", "当前仅保存路径。2.19.1 尚无验证通过的打开接口；请在官方窗口手动打开。"); if (path is null) return; await RunAsync(async () => { await _hub.Profiles.SetWorkspaceAsync(Selected, path); await RefreshAsync(); }); }
    private async void Bridge_Click(object sender, RoutedEventArgs e)
    { var policy = InputDialog.Ask(this, "桥接范围", "on：共享宿主 .gitconfig 与整个 .ssh；off：撤销本工具的受管项，保留宿主目标。", "on"); if (policy is not "on" and not "off") return; await RunAsync(async () => Show(await _hub.Bridge.ApplyAsync(Selected, policy == "on", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))); }
    private async void Inherit_Click(object sender, RoutedEventArgs e)
    { var source = InputDialog.Ask(this, "配置继承", "源编辑器 User 目录。先预览安全允许项、冲突与跳过内容；不复制插件或任意文本。"); if (source is null) return; await RunAsync(async () =>
        { var id = Selected; var preview = await NextConfiguration.InheritAsync(_hub.Paths, id, source, false); Show(preview);
          if (preview.Allowed.Count > 0 && MessageBox.Show(this, "预览已显示。写入允许项并备份原文件？已有冲突值会保留。", "应用配置继承", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
              Show(await NextConfiguration.InheritAsync(_hub.Paths, id, source, true)); }); }
    private async void Shortcut_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => Show(new { path = DesktopShortcut.Create(await _hub.Profiles.GetAsync(Selected), _hub.Paths.DataRoot, Cli(), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) }));
    private async void Register_Click(object sender, RoutedEventArgs e) => await RunAsync(() => { _hub.RegisterProtocol(Cli()); Output.Text = "已注册回调；旧处理器已备份。"; return Task.CompletedTask; });
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RunAsync(() => { Show(new { restored = NextProtocol.Restore(_hub.Paths) }); return Task.CompletedTask; });
    private void Logs_Click(object sender, RoutedEventArgs e) { var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; info.ArgumentList.Add(_hub.Paths.LogsRoot); Process.Start(info)?.Dispose(); }
    private static string Cli()
    {
        foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "..", "cli", "agprofile-next.exe"), Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AGProfileHub.Cli", "bin", "Release", "net10.0", "agprofile-next.exe") }) if (File.Exists(path)) return Path.GetFullPath(path);
        throw new HubException("找不到 Next CLI；请保留包中的 hub 和 cli 目录。", "cli_not_found");
    }
}
