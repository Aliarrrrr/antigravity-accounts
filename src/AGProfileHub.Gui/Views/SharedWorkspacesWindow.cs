using System.Windows;
using System.Windows.Controls;
using AGProfileHub.Core;

namespace AGProfileHub.Gui.Views;

public sealed class SharedWorkspacesWindow : Window
{
    private readonly NextHubService _hub;
    private readonly SharedWorkspaceService _groups;
    private readonly AccountSwitchCoordinator _switches;
    private readonly ComboBox _group = new() { DisplayMemberPath = "Name", MinWidth = 220 };
    private readonly ComboBox _account = new() { DisplayMemberPath = "Name", MinWidth = 220 };
    private readonly ListBox _members = new() { DisplayMemberPath = "Name", MinHeight = 90 };
    private readonly StackPanel _actions = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
    private readonly TextBlock _identity = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _output = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 140 };
    private readonly Button _switch = Button("切换到所选账号");
    private readonly Button _recover = Button("恢复未完成的切换");
    private readonly Button _cancel = Button("取消等待");
    private CancellationTokenSource? _waiting;
    private Guid? _preview;
    private Guid? _lastImport;
    private Guid? _receiptGroup;
    private bool _busy;
    public bool InitialLoadComplete { get; private set; }

    public SharedWorkspacesWindow(NextHubService hub)
    {
        _hub = hub; _groups = new(hub.Paths, hub.Repository); _switches = new(hub.Paths);
        Title = "共享工作空间"; Width = 900; Height = 760; MinWidth = 720; MinHeight = 640;
        Background = System.Windows.Media.Brushes.White;
        var panel = new DockPanel { Margin = new Thickness(24), Background = System.Windows.Media.Brushes.White }; Content = panel;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "共享工作空间", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Hub 仅保存手填标签与选中的工作内容，不读取或保存账号凭据。登录状态由官方客户端管理。", Margin = new Thickness(0, 8, 0, 16), TextWrapping = TextWrapping.Wrap });
        _cancel.IsEnabled = false; _cancel.HorizontalAlignment = HorizontalAlignment.Left;
        _cancel.Click += (_, _) => _waiting?.Cancel(); heading.Children.Add(_cancel);
        var output = new Grid { Margin = new Thickness(0, 12, 0, 0), Height = 190 }; output.Children.Add(_output);
        DockPanel.SetDock(output, Dock.Bottom); panel.Children.Add(output); panel.Children.Add(_actions);
        var groupRow = new WrapPanel(); groupRow.Children.Add(Label("共享组")); groupRow.Children.Add(_group);
        Add(groupRow, "新建组", CreateAsync); Add(groupRow, "刷新", RefreshAsync); _actions.Children.Add(groupRow);
        _actions.Children.Add(_status);
        _actions.Children.Add(_identity);
        _actions.Children.Add(new TextBlock { Text = "组内账号", FontWeight = FontWeights.SemiBold }); _actions.Children.Add(_members);
        var accountRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; accountRow.Children.Add(Label("账号")); accountRow.Children.Add(_account);
        Add(accountRow, "加入组", AddAccountAsync); Add(accountRow, "移出组", RemoveAccountAsync); _actions.Children.Add(accountRow);
        var switchRow = new WrapPanel(); switchRow.Children.Add(_switch); _switch.Click += async (_, _) => await RunAsync(SwitchAsync);
        _switch.IsEnabled = _recover.IsEnabled = false;
        switchRow.Children.Add(_recover); _recover.Click += async (_, _) => await RunAsync(RecoverAsync); _actions.Children.Add(switchRow);
        var importRow = new WrapPanel(); Add(importRow, "选择来源内容…", PreviewAsync); Add(importRow, "应用这次预览", ApplyAsync); Add(importRow, "撤销本次导入", RestoreAsync); _actions.Children.Add(importRow);
        _group.SelectionChanged += async (_, _) => { if (!_busy) await RunAsync(ReadSelectedAsync); };
        Loaded += async (_, _) => { await RunAsync(RefreshAsync); InitialLoadComplete = true; };
        Closing += (_, e) => { if (_busy) { e.Cancel = true; _output.Text = "操作进行中。切换等待可点击“取消等待”，完成后再关闭。"; } };
    }

    private SharedWorkspaceGroup Selected => _group.SelectedItem as SharedWorkspaceGroup ?? throw new HubException("请先选择共享组。", "shared_group_required");
    private ProfileRegistration Account => _account.SelectedItem as ProfileRegistration ?? throw new HubException("请选择来源或目标账号。", "profile_required");
    private static Button Button(string text) => new() { Content = text, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 4, 8, 4) };
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private void Add(Panel row, string text, Func<Task> action) { var button = Button(text); button.Click += async (_, _) => await RunAsync(action); row.Children.Add(button); }
    private void Show(object value) => _output.Text = value switch
    {
        SharedImportPreview preview => string.Join(Environment.NewLine,
            new[] { "导入预览：仅保留所选文件。会话索引尚未导入，官方客户端恢复尚未检查。", "" }
                .Concat(preview.Items.Select(item => $"{ItemName(item.Kind)} {item.Id}：{DispositionName(item.Disposition)}" +
                    (item.Issues.Count == 0 ? "" : "；" + string.Join("；", item.Issues.Select(IssueName)))))),
        SharedImportResult result => $"{(result.State == "Restored" ? "已撤销本次导入" : "已处理本次文件导入")}。" +
            $"\n写入文件或目录：{result.ImportedPaths.Count}；跳过项：{result.SkippedPaths.Count}。" +
            $"\n导入编号：{result.ImportId}\n来源 Profile 保留。会话索引尚未导入，不能据此在官方客户端恢复或跨账号续聊。",
        AccountSwitchResult result => result.Succeeded ? "目标身份及原工作位置已确认。" :
            result.State?.SendingBlocked == true ? "切换未完成，已暂停发送。原身份或工作位置尚未确认，请保留当前工作内容。" :
            "切换未完成，原任务未被取消。客户端未支持时不会修改登录状态。",
        _ => "操作已完成，请查看当前共享组状态。"
    };
    private static string ItemName(string kind) => kind switch
    { "Project" => "项目", "Conversation" => "会话", "Knowledge" => "知识", "Memory" => "记忆", _ => kind };
    private static string DispositionName(SharedImportDisposition disposition) => disposition switch
    { SharedImportDisposition.Copy => "可导入", SharedImportDisposition.Duplicate => "内容相同，跳过", SharedImportDisposition.Conflict => "同 ID 内容冲突，跳过", _ => "关联或格式未确认，跳过" };
    private static string IssueName(string issue) => issue.Split(':')[0] switch
    {
        "conversation_dependency_missing" => "尚未选择项目引用的会话",
        "conversation_dependency_conflict" => "项目引用的会话存在冲突",
        "conversation_dependency_unverified" => "项目引用的会话无法读取或格式未确认",
        "project_conversation_index_unconfirmed" => "项目会话索引不可用或版本未支持",
        "project_environment_format_unconfirmed" or "project_environment_or_permission_format_unconfirmed" => "环境或权限关联尚未支持",
        "project_relationship_field_unsupported" => "项目关联使用了尚未支持的字段格式",
        "project_directory_format_unconfirmed" => "项目目录格式尚未支持，仅支持已确认的项目 JSON 文件",
        "shared_conversation_id_mismatch" => "数据库内部会话 ID 与文件名不同",
        "shared_import_source_busy" => "数据库仍被写入，请先关闭来源客户端",
        "shared_conversation_database_invalid" => "数据库无法读取或完整性检查未通过",
        "shared_sqlite_unavailable" => "本机 SQLite 读取组件不可用",
        _ => "内容格式尚未支持"
    };
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return; _busy = true; _actions.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { _output.Text = "已取消等待；原任务未被取消。若身份变更已开始，已先处理恢复，请查看组状态。"; }
        catch (Exception ex) { _output.Text = ex is HubException ? ex.Message : "操作未完成：" + ex.GetType().Name + "。请查看组状态。"; }
        finally { _busy = false; _actions.IsEnabled = true; _cancel.IsEnabled = false; }
    }
    private async Task RefreshAsync()
    {
        var selected = (_group.SelectedItem as SharedWorkspaceGroup)?.Id;
        var groups = await _groups.ListAsync(); _group.ItemsSource = groups;
        _group.SelectedItem = groups.FirstOrDefault(g => g.Id == selected) ?? groups.FirstOrDefault();
        var accounts = (await _hub.Repository.ReadProfilesAsync()).Profiles;
        _account.ItemsSource = accounts; _account.SelectedItem = accounts.FirstOrDefault();
        await ReadSelectedAsync();
    }
    private async Task ReadSelectedAsync()
    {
        _preview = null; _lastImport = null; _receiptGroup = null;
        if (_group.SelectedItem is not SharedWorkspaceGroup selected) { _members.ItemsSource = null; _switch.IsEnabled = _recover.IsEnabled = false; _identity.Text = ""; _status.Text = "先新建共享组，再选择已有账号加入。"; _output.Text = "共享组只纳入明确选择的工作内容。"; return; }
        var group = await _groups.ReadAsync(selected.Id);
        _members.ItemsSource = (await _hub.Repository.ReadProfilesAsync()).Profiles.Where(p => group.AccountProfileIds.Contains(p.Id)).ToArray();
        var capability = await _switches.GetCapabilitiesAsync(group.Id); _switch.IsEnabled = _recover.IsEnabled = capability.Supported;
        _status.Text = capability.Supported ? "客户端可用。切换将等待组内正在运行的任务完成。" : "无感账号切换尚不可用：官方身份选择、登录环境隔离及原会话恢复尚未确认。现有多开入口保留。";
        var state = await _switches.ReadStateAsync(group.Id);
        var lastAccount = (await _hub.Repository.ReadProfilesAsync()).Profiles.FirstOrDefault(p => p.Id == state?.ActiveProfileId);
        _identity.Text = lastAccount is null ? "当前账号：尚未从共享客户端确认" : "上次切换确认的账号：" + lastAccount.Name;
        _output.Text = $"共享组：{group.Name}\n组内账号：{group.AccountProfileIds.Count}\n\n先选择来源账号和内容，查看预览后再应用。相同内容跳过，冲突不覆盖。" +
            "\nHub 不管理账号凭据。当前会话索引尚未导入，文件保留不代表官方客户端可恢复。";
    }
    private async Task CreateAsync()
    {
        var name = InputDialog.Ask(this, "新建共享组", "为这组项目和会话填写名称。"); if (string.IsNullOrWhiteSpace(name)) return;
        var created = await _groups.CreateAsync(name); await RefreshAsync(); _group.SelectedItem = ((IReadOnlyList<SharedWorkspaceGroup>)_group.ItemsSource).First(g => g.Id == created.Id); await ReadSelectedAsync();
    }
    private async Task AddAccountAsync() { await _groups.AddAccountAsync(Selected.Id, Account.Id); await ReadSelectedAsync(); }
    private async Task RemoveAccountAsync()
    {
        var member = _members.SelectedItem as ProfileRegistration ?? throw new HubException("请选择要移出的组内账号。", "profile_required");
        await _groups.RemoveAccountAsync(Selected.Id, member.Id); await ReadSelectedAsync();
    }
    private async Task PreviewAsync()
    {
        var group = Selected; var account = Account; var catalog = await _groups.PreviewCatalogAsync(account.Id);
        var selection = new ImportSelectionWindow(catalog) { Owner = this };
        if (selection.ShowDialog() != true) return;
        var preview = await _groups.PreviewImportAsync(group.Id, account.Id, selection.Projects, selection.Conversations, selection.IncludeKnowledge);
        _preview = preview.Id; _receiptGroup = group.Id; Show(preview);
    }
    private async Task ApplyAsync()
    {
        if (_preview is not Guid preview || _receiptGroup != Selected.Id) throw new HubException("请先选择内容并查看导入预览。", "shared_preview_required");
        if (MessageBox.Show(this, "将按这次预览保留所选文件。冲突或关联不完整的项目跳过，来源文件保持原样。会话索引尚未导入，当前不能在官方客户端恢复或跨账号续聊。", "应用导入", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try
        {
            var result = await _groups.ApplyImportAsync(Selected.Id, preview); _lastImport = result.ImportId; _preview = null; Show(result);
        }
        catch (Exception error) when (error.Data["SharedImportId"] is Guid)
        {
            _lastImport = (Guid)error.Data["SharedImportId"]!; _preview = null;
            throw new HubException("导入未完成。可点击“撤销本次导入”处理已写入的内容。恢复编号：" + _lastImport,
                "shared_import_interrupted", error);
        }
    }
    private async Task RestoreAsync()
    {
        if (_lastImport is not Guid import || _receiptGroup != Selected.Id) throw new HubException("此窗口尚无可撤销的导入；历史导入可通过 CLI 的 restore-import 恢复。", "shared_import_required");
        Show(await _groups.RestoreImportAsync(Selected.Id, import)); _lastImport = null;
    }
    private async Task SwitchAsync()
    {
        var group = await _groups.ReadAsync(Selected.Id); var target = Account;
        if (!group.AccountProfileIds.Contains(target.Id)) throw new HubException("请先将目标账号加入共享组。", "shared_account_not_member");
        using var cancellation = new CancellationTokenSource(); _waiting = cancellation; _cancel.IsEnabled = true;
        _output.Text = "正在等待组内任务完成；取消等待不会取消原任务。";
        try { Show(await _switches.ExecuteAsync(group.Id, target.Id, cancellation.Token)); }
        finally { _waiting = null; _cancel.IsEnabled = false; }
    }
    private async Task RecoverAsync() => Show(await _switches.RecoverAsync(Selected.Id));
}

internal sealed class ImportSelectionWindow : Window
{
    private readonly List<CheckBox> _projects = [];
    private readonly List<CheckBox> _conversations = [];
    private readonly CheckBox _knowledge = new() { Content = "同时导入知识与记忆", Margin = new Thickness(0, 8, 0, 8) };
    public string[] Projects => _projects.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();
    public string[] Conversations => _conversations.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();
    public bool IncludeKnowledge => _knowledge.IsChecked == true;
    public ImportSelectionWindow(SharedImportCatalog catalog)
    {
        Title = "选择要导入的内容"; Width = 650; Height = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new Thickness(20) }; Content = dock;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        _knowledge.IsEnabled = catalog.KnowledgeAvailable || catalog.MemoryAvailable; footer.Children.Add(_knowledge);
        var accept = new Button { Content = "查看预览", Padding = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Right };
        accept.Click += (_, _) => { DialogResult = true; }; footer.Children.Add(accept);
        var body = new StackPanel(); var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; dock.Children.Add(scroll);
        body.Children.Add(new TextBlock { Text = "仅导入勾选的内容，项目引用的会话也需选择。数据库、brain 与 annotations 一起判断冲突。会话索引尚未导入，文件导入不代表官方客户端可恢复。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        Entries(body, "项目", catalog.ProjectIds, _projects); Entries(body, "会话", catalog.ConversationIds, _conversations);
    }
    private static void Entries(Panel body, string title, IReadOnlyList<string> ids, List<CheckBox> boxes)
    {
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        if (ids.Count == 0) body.Children.Add(new TextBlock { Text = "未找到可选内容。" });
        foreach (var id in ids) { var box = new CheckBox { Content = id, Tag = id, Margin = new Thickness(0, 4, 0, 4) }; boxes.Add(box); body.Children.Add(box); }
    }
}
