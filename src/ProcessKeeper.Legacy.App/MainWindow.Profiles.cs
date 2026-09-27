using System.Windows.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private void AddProfilesView(Panel panel)
    {
        panel.Children.Add(new WhitelistProfilesView(new WhitelistProfilesStore(_directory),
            () => !_closed && !_busy && _rulesReadable,
            Confirm,
            async mutation =>
            {
                if (_closed || _busy || !_rulesReadable) throw new InvalidOperationException(L.T("请等待当前操作完成"));
                _busy = true;
                try { return await Task.Run(mutation); }
                finally { _busy = false; }
            },
            state =>
            {
                if (_closed) return;
                _rules = state.ActiveRules; Log(L.T("白名单配置已保存") + " | " + state.ActiveProfile.Name); _ = RenderAsync();
            }));
    }
}
