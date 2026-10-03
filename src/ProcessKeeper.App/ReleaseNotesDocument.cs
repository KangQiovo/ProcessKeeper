using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal sealed record ReleaseNoteSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false, string Url = "");
internal sealed record ReleaseNoteBlock(string Kind, int Level, int Indent, IReadOnlyList<ReleaseNoteSpan> Spans);
internal sealed record ReleaseNotesDocument(IReadOnlyList<ReleaseNoteBlock> Blocks, bool Truncated);
internal static class UpdatePackageDescription
{
    internal static string Text(UpdateAsset asset)
    {
        var platform = asset.PackageTarget switch
    {
        UpdatePackageTarget.Windows7Compat => L.T("Windows 7 及以上 | x86/x64 兼容版"),
        UpdatePackageTarget.Windows10x64 => L.T("Windows 10/11 | x86/x64"),
        UpdatePackageTarget.Windows10arm64 => L.T("Windows 10/11 | ARM64"),
        _ => L.T("通用版本")
    };
        return platform + " | " + L.T(asset.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)
            ? "安装版 | 包含卸载程序" : "单文件免安装版");
    }
}

/// <summary>Parses bounded Markdown to native text data. HTML and images never create web or image controls.</summary>
internal static class ReleaseNotesParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseTaskLists().Build();
    internal static ReleaseNotesDocument Parse(string? markdown)
    {
        var source = markdown ?? ""; var truncated = source.Length > 65536;
        if (truncated) source = source.Substring(0, 65536);
        var blocks = new List<ReleaseNoteBlock>();
        var spanBudget = 4096;
        void Append(string kind, int level, int indent, IEnumerable<ReleaseNoteSpan> spans)
        {
            if (blocks.Count >= 400 || spanBudget <= 0) { truncated = true; return; }
            var bounded = spans.Take(spanBudget).ToArray(); spanBudget -= bounded.Length;
            blocks.Add(new(kind, level, Math.Min(indent, 8), bounded));
        }
        void Walk(ContainerBlock container, int indent, string prefix = "", bool quoted = false, int depth = 0)
        {
            if (depth > 20) { truncated = true; return; }
            foreach (var block in container)
            {
                if (blocks.Count >= 400 || spanBudget <= 0) { truncated = true; break; }
                switch (block)
                {
                    case HeadingBlock heading:
                        Append("heading", heading.Level, indent, Inline(heading.Inline)); break;
                    case ParagraphBlock paragraph:
                        Append(quoted ? "quote" : "paragraph", 0, indent,
                            (prefix.Length > 0 ? new[] { new ReleaseNoteSpan(prefix) } : Array.Empty<ReleaseNoteSpan>()).Concat(Inline(paragraph.Inline)));
                        prefix = ""; break;
                    case CodeBlock code:
                        Append("code", 0, indent, new[] { new ReleaseNoteSpan(code.Lines.ToString(), Code: true) }); break;
                    case ListBlock list:
                        int itemIndex = 0;
                        foreach (var item in list.OfType<ListItemBlock>())
                            Walk(item, indent + 1, list.IsOrdered ? (++itemIndex).ToString() + ". " : "• ", quoted, depth + 1);
                        break;
                    case QuoteBlock quote: Walk(quote, indent + 1, "", true, depth + 1); break;
                    case ThematicBreakBlock: Append("separator", 0, indent, Array.Empty<ReleaseNoteSpan>()); break;
                    case Table table:
                        foreach (var row in table.OfType<TableRow>())
                        {
                            var cells = row.OfType<TableCell>().Select(cell => cell.OfType<LeafBlock>().SelectMany(leaf => Inline(leaf.Inline))).ToArray();
                            var spans = new List<ReleaseNoteSpan>();
                            for (int index = 0; index < cells.Length; index++)
                            { if (index > 0) spans.Add(new(" | ")); spans.AddRange(cells[index].Select(span => span with { Bold = span.Bold || row.IsHeader })); }
                            Append("table", 0, indent, spans);
                        }
                        break;
                    case HtmlBlock html: Append("paragraph", 0, indent, new[] { new ReleaseNoteSpan(html.Lines.ToString()) }); break;
                    case ContainerBlock nested: Walk(nested, indent, prefix, quoted, depth + 1); break;
                    case LeafBlock leaf: Append("paragraph", 0, indent, Inline(leaf.Inline)); break;
                }
            }
        }
        Walk(Markdown.Parse(source, Pipeline), 0);
        return new(blocks, truncated);
    }
    private static IEnumerable<ReleaseNoteSpan> Inline(ContainerInline? container, bool bold = false, bool italic = false, string url = "", int depth = 0)
    {
        if (container is null || depth > 20) yield break;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: yield return new(literal.Content.ToString(), bold, italic, Url: url); break;
                case CodeInline code: yield return new(code.Content, bold, italic, true, url); break;
                case LineBreakInline line: yield return new(line.IsHard ? "\n" : " ", bold, italic, Url: url); break;
                case EmphasisInline emphasis:
                    foreach (var span in Inline(emphasis, bold || emphasis.DelimiterCount >= 2, italic || emphasis.DelimiterCount == 1, url, depth + 1)) yield return span;
                    break;
                case LinkInline link:
                    foreach (var span in Inline(link, bold, italic, !link.IsImage && SafeLink(link.Url) ? link.Url! : "", depth + 1)) yield return span;
                    break;
                case AutolinkInline auto: yield return new(auto.Url, bold, italic, Url: SafeLink(auto.Url) ? auto.Url : ""); break;
                case HtmlInline html: yield return new(html.Tag, bold, italic, Url: url); break;
                case TaskList task: yield return new(task.Checked ? "☑ " : "☐ ", bold, italic); break;
                case ContainerInline nested:
                    foreach (var span in Inline(nested, bold, italic, url, depth + 1)) yield return span;
                    break;
            }
        }
    }
    internal static bool SafeLink(string? url) => url is not null && url.Length <= 4096 &&
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http" && parsed.UserInfo.Length == 0;
}
