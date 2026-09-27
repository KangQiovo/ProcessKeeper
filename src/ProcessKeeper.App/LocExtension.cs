using Microsoft.UI.Xaml.Markup;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Text { get; set; } = "";
    protected override object ProvideValue() => L.T(Text);
}
