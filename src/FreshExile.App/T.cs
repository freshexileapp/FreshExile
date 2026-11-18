using System.Windows.Markup;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>XAML text in the interface's language: the markup extension l:T with the Russian text in single quotes (see <see cref="Loc"/>).</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
