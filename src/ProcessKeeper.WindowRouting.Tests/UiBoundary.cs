using ProcessKeeper.Core;

namespace Microsoft.UI.Xaml
{
    public sealed class RoutedEventArgs : EventArgs { }
    public enum TextWrapping { Wrap }
    public enum VerticalAlignment { Center }
}
namespace Microsoft.UI.Xaml.Controls
{
    public enum InfoBarSeverity { Informational, Success, Warning }
    public enum ListViewSelectionMode { Single }
    public enum ContentDialogResult { None, Primary }
    public enum Orientation { Horizontal }
    public static class ToolTipService
    {
        public static void SetToolTip(object element, object value) { }
    }
    public sealed class ListView
    {
        public object? ItemsSource { get; set; }
        public ListViewSelectionMode SelectionMode { get; set; }
        public int SelectedIndex { get; set; }
        public double MaxHeight { get; set; }
    }
    public sealed class StackPanel
    {
        public double Spacing { get; set; }
        public double Width { get; set; }
        public Orientation Orientation { get; set; }
        public List<object> Children { get; } = [];
    }
    public sealed class TextBlock
    {
        public string Text { get; set; } = "";
        public Microsoft.UI.Xaml.TextWrapping TextWrapping { get; set; }
        public Microsoft.UI.Xaml.VerticalAlignment VerticalAlignment { get; set; }
        public double FontSize { get; set; }
        public double MaxWidth { get; set; }
    }
}
namespace ProcessKeeper.App
{
    using Microsoft.UI.Xaml.Controls;
    public sealed partial class MainWindow
    {
        // Fixture has no application startup, inventory, launch, or restart implementation.
        private bool _working = false, _dialogOpen = false, _closed = false;
        private ApplicationGroup? SelectedApp => null;
        public int RestartConfirmationCount { get; private set; }
        public int SuccessNoticeCount { get; private set; }
        public int SelectionDialogCount { get; private set; }
        public int CandidateCount(ApplicationGroup app, bool hiddenOnly = false) => WindowCandidates(app, hiddenOnly).Length;
        public (int Pid, nint Handle, bool Recommended, string[] RowText)[] Choices(ApplicationGroup app, bool hiddenOnly = false) =>
            WindowCandidates(app, hiddenOnly).Select(choice => (choice.Process.Id, choice.Window.Handle, choice.IsRecommended,
                WindowChoiceRow(choice).Children.OfType<TextBlock>().Select(text => text.Text).ToArray())).ToArray();
        public Task Open(ApplicationGroup app) => OperateWindow(false, target: app);
        private Task ConfirmAndRestartAvdAsync(ApplicationGroup app) { RestartConfirmationCount++; return Task.CompletedTask; }
        private static bool HasSpecialEntry(ApplicationGroup app) => false;
        private static Task OpenSpecialWindowAsync(ApplicationGroup app) => Task.CompletedTask;
        private void ShowNotice(string title, string message, InfoBarSeverity severity)
        {
            if (severity == InfoBarSeverity.Success) SuccessNoticeCount++;
        }
        private static object NewDialog(string title, object panel, string primary) => panel;
        private Task<ContentDialogResult> ShowDialog(object dialog)
        {
            SelectionDialogCount++;
            return Task.FromResult(ContentDialogResult.Primary);
        }
        private static void Log(string message) { }
        private static Task RefreshAsync() => Task.CompletedTask;
    }
}
