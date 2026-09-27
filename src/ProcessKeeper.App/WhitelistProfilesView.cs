using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

/// <summary>A profile is selected for inspection first; activation is an explicit, confirmed action.</summary>
internal sealed class WhitelistProfilesView : UserControl
{
    private readonly WhitelistProfilesStore _store;
    private readonly Func<bool> _canEdit;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Func<Func<WhitelistProfilesSnapshot>, Task<WhitelistProfilesSnapshot>> _mutate;
    private readonly Action<WhitelistProfilesSnapshot> _changed;
    private readonly ComboBox _choice = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new() { MaxLength = 80, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 230,
        MaxLength = 1048576, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly List<AppBarButton> _actions = new();
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private WhitelistProfilesSnapshot? _snapshot;
    private string _selectedId = "", _savedText = "";
    private bool _busy, _selecting;

    internal WhitelistProfilesView(WhitelistProfilesStore store, Func<bool> canEdit,
        Func<string, string, Task<bool>> confirm,
        Func<Func<WhitelistProfilesSnapshot>, Task<WhitelistProfilesSnapshot>> mutate,
        Action<WhitelistProfilesSnapshot> changed)
    {
        _store = store; _canEdit = canEdit; _confirm = confirm; _mutate = mutate; _changed = changed;
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = L.T("白名单配置"), FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = L.T("最多五套。选择后可预览编辑；应用后才切换当前白名单。"), TextWrapping = TextWrapping.Wrap });
        _choice.Header = L.T("选择配置"); _name.Header = L.T("配置名称"); _editor.Header = L.T("规则 JSON");
        AutomationProperties.SetName(_choice, L.T("选择配置"));
        panel.Children.Add(_choice); panel.Children.Add(_summary); panel.Children.Add(_name);
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            IsDynamicOverflowEnabled = true, ClosedDisplayMode = AppBarClosedDisplayMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        void Add(string label, Symbol icon, Func<Task> action)
        {
            var button = new AppBarButton { Label = L.T(label), Icon = new SymbolIcon(icon) };
            button.Click += async (_, _) => await RunAsync(action); _actions.Add(button); commands.PrimaryCommands.Add(button);
        }
        Add("新建空配置", Symbol.Add, CreateAsync);
        Add("应用此配置", Symbol.Accept, ActivateAsync);
        Add("重命名", Symbol.Edit, RenameAsync);
        Add("删除配置", Symbol.Delete, DeleteAsync);
        Add("重新读取", Symbol.Refresh, ReloadAsync);
        panel.Children.Add(commands); panel.Children.Add(_editor);
        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var validate = new Button { Content = L.T("检查格式") };
        validate.Click += async (_, _) => await RunAsync(() => { var rules = RuleFileCodec.Parse(_editor.Text); Report(L.F($"格式正确 | {rules.Count} 条规则"), true); return Task.CompletedTask; });
        var save = new Button { Content = L.T("保存规则") };
        save.Click += async (_, _) => await RunAsync(SaveAsync);
        saveRow.Children.Add(validate); saveRow.Children.Add(save); panel.Children.Add(saveRow); panel.Children.Add(_status);
        Content = panel;
        _choice.SelectionChanged += async (_, _) =>
        {
            if (_selecting || _busy || _choice.SelectedItem is not ComboBoxItem { Tag: string nextId } || nextId == _selectedId) return;
            var previousId = _selectedId;
            await RunAsync(async () =>
            {
                if (!await CanDiscardAsync()) { Select(previousId); return; }
                ShowProfile(nextId);
            });
        };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); if (_status.Severity == InfoBarSeverity.Success) _status.IsOpen = false; };
        Loaded += async (_, _) => { if (_snapshot is null) await RunAsync(ReloadAsync); };
        Unloaded += (_, _) => _noticeTimer.Stop();
    }

    private bool Dirty => _editor.Text != _savedText;
    private async Task<bool> CanDiscardAsync() => !Dirty || await _confirm(L.T("放弃未保存的编辑？"), L.T("未保存的规则编辑将被丢弃。"));
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; _choice.IsEnabled = _name.IsEnabled = _editor.IsEnabled = false;
        foreach (var button in _actions) button.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { Report(ex.Message, false); }
        finally { _busy = false; _choice.IsEnabled = _name.IsEnabled = _editor.IsEnabled = true; foreach (var button in _actions) button.IsEnabled = true; }
    }
    private void Report(string text, bool success)
    {
        _noticeTimer.Stop(); _status.Message = text; _status.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error; _status.IsOpen = true;
        if (success) _noticeTimer.Start();
    }
    private void Select(string id)
    {
        _selecting = true;
        try { _choice.SelectedItem = _choice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, id)); }
        finally { _selecting = false; }
    }
    private void Apply(WhitelistProfilesSnapshot state, string? selected = null)
    {
        _snapshot = state; _selecting = true;
        try
        {
            _choice.Items.Clear();
            foreach (var profile in state.Profiles) _choice.Items.Add(new ComboBoxItem { Tag = profile.Id,
                Content = profile.Name + (profile.Id == state.ActiveId ? " | " + L.T("当前使用") : "") });
        }
        finally { _selecting = false; }
        ShowProfile(state.Profiles.Any(p => p.Id == selected) ? selected! : state.ActiveId);
    }
    private void ShowProfile(string id)
    {
        if (_snapshot is null) return;
        var profile = _snapshot.Profiles.First(p => p.Id == id); _selectedId = id; Select(id); _name.Text = profile.Name;
        _editor.Text = RuleFileCodec.Serialize(profile.Rules); _savedText = _editor.Text;
        _summary.Text = L.F($"{profile.Rules.Count} 条规则 | {_snapshot.Profiles.Count}/5 套配置") +
            (id == _snapshot.ActiveId ? " | " + L.T("当前使用") : " | " + L.T("仅预览，尚未应用"));
    }
    private async Task ReloadAsync()
    {
        if (!await CanDiscardAsync()) return;
        Apply(await Task.Run(_store.Load), _selectedId);
    }
    private void RequireEdit()
    {
        if (!_canEdit()) throw new InvalidOperationException(L.T("请等待当前操作完成"));
        if (_snapshot is null) throw new InvalidOperationException(L.T("请先重新读取配置。"));
    }
    private async Task CommitAsync(Func<WhitelistProfilesSnapshot> action, string? selected = null)
    {
        RequireEdit(); var next = await _mutate(action); Apply(next, selected ?? _selectedId); _changed(next); Report(L.T("白名单配置已保存"), true);
    }
    private async Task CreateAsync()
    {
        RequireEdit(); if (!await CanDiscardAsync()) return;
        var name = _name.Text.Trim();
        if (name == _snapshot!.Profiles.First(p => p.Id == _selectedId).Name)
        {
            var number = 1;
            do { name = L.F($"新配置 {number++}"); }
            while (_snapshot.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));
        }
        var oldIds = _snapshot!.Profiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var next = await _mutate(() => _store.Create(name, _snapshot.Revision));
        Apply(next, next.Profiles.First(p => !oldIds.Contains(p.Id)).Id); _changed(next); Report(L.T("已创建空配置，尚未应用。"), true);
    }
    private async Task RenameAsync()
    {
        RequireEdit(); if (!await CanDiscardAsync()) return;
        var name = _name.Text.Trim();
        await CommitAsync(() => _store.Rename(_selectedId, name, _snapshot!.Revision));
    }
    private async Task DeleteAsync()
    {
        RequireEdit();
        if (!await _confirm(L.T("删除配置？"), _name.Text + "\n" + L.T("当前使用的配置不能删除。"))) return;
        await CommitAsync(() => _store.Delete(_selectedId, _snapshot!.Revision));
    }
    private async Task ActivateAsync()
    {
        RequireEdit(); if (!await CanDiscardAsync()) return;
        if (_selectedId == _snapshot!.ActiveId) return;
        if (!await _confirm(L.T("应用此配置？"), _name.Text + "\n" + L.T("将切换当前白名单；不会立即关闭任何程序。"))) return;
        await CommitAsync(() => _store.Activate(_selectedId, _snapshot.Revision));
    }
    private async Task SaveAsync()
    {
        RequireEdit(); var rules = RuleFileCodec.Parse(_editor.Text);
        if (_selectedId == _snapshot!.ActiveId && !await _confirm(L.T("保存当前配置？"), L.T("保存后立即更新白名单保护，不会执行关闭操作。"))) return;
        await CommitAsync(() => _store.Save(_selectedId, rules, _snapshot.Revision));
    }
}
