using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal static class ReleaseNotesView
{
    internal static FrameworkElement Create(ReleaseNotesDocument document, Action<string>? openLink = null)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var block in document.Blocks)
        {
            if (block.Kind == "separator") { var line = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 8) }; line.SetResourceReference(Border.BackgroundProperty, "MutedBrush"); panel.Children.Add(line); continue; }
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(block.Indent * 12, 0, 0, 8),
                FontSize = block.Kind == "heading" ? Math.Max(15, 26 - block.Level * 2) : 14 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            foreach (var span in block.Spans)
            {
                var run = new Run(span.Text) { FontWeight = span.Bold || block.Kind == "heading" ? FontWeights.SemiBold : FontWeights.Normal,
                    FontStyle = span.Italic ? FontStyles.Italic : FontStyles.Normal };
                if (span.Code) run.FontFamily = new FontFamily("Consolas");
                if (span.Url.Length > 0 && openLink is not null)
                {
                    var link = new Hyperlink(run); var url = span.Url;
                    link.SetResourceReference(TextElement.ForegroundProperty, "InkBrush");
                    link.Click += (_, _) => openLink(url); text.Inlines.Add(link);
                }
                else text.Inlines.Add(run);
            }
            panel.Children.Add(text);
        }
        if (document.Truncated)
        {
            var notice = new TextBlock { Text = L.T("更新日志较长，完整内容请查看发布主页。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            notice.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush"); panel.Children.Add(notice);
        }
        return panel;
    }
}
