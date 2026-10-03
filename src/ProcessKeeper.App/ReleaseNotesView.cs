using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal static class ReleaseNotesView
{
    internal static FrameworkElement Create(ReleaseNotesDocument document, Action<string>? openLink = null)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var block in document.Blocks)
        {
            if (block.Kind == "separator") { panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], Margin = new Thickness(0, 4, 0, 4) }); continue; }
            var text = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(block.Indent * 12, 0, 0, 0),
                FontSize = block.Kind == "heading" ? Math.Max(15, 26 - block.Level * 2) : 14 };
            var paragraph = new Paragraph();
            foreach (var span in block.Spans)
            {
                var run = new Run { Text = span.Text, FontWeight = span.Bold || block.Kind == "heading" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    FontStyle = span.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal };
                if (span.Code) run.FontFamily = new FontFamily("Consolas");
                if (span.Url.Length > 0 && openLink is not null)
                {
                    var link = new Hyperlink(); link.Inlines.Add(run); var url = span.Url; link.Click += (_, _) => openLink(url); paragraph.Inlines.Add(link);
                }
                else paragraph.Inlines.Add(run);
            }
            text.Blocks.Add(paragraph); panel.Children.Add(text);
        }
        if (document.Truncated) panel.Children.Add(new TextBlock { Text = L.T("更新日志较长，完整内容请查看发布主页。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        return panel;
    }
}
