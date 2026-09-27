using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal static class RiskModeNotice
{
    internal static void Refresh(InfoBar notice)
    {
        notice.Title = L.T("无视风险模式");
        notice.Message = L.T("二次风险确认已关闭。结束部分程序可能丢失数据、使桌面失效，甚至导致系统崩溃。");
        notice.Severity = InfoBarSeverity.Error;
        notice.IsClosable = false;
        notice.IsOpen = RiskConfirmationMode.IsEnabled;
    }

    internal static void Attach(Window owner, InfoBar notice)
    {
        var closed = false;
        void Update(object? sender, EventArgs args)
        {
            if (closed) return;
            if (notice.DispatcherQueue.HasThreadAccess) Refresh(notice);
            else notice.DispatcherQueue.TryEnqueue(() => { if (!closed) Refresh(notice); });
        }
        Refresh(notice);
        RiskConfirmationMode.Changed += Update;
        owner.Closed += (_, _) => { closed = true; RiskConfirmationMode.Changed -= Update; };
    }
}
