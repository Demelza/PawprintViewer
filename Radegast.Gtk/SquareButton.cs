using Gtk;

namespace Radegast.Gtk;

/// <summary>A themed button whose width follows its height.</summary>
internal sealed class SquareButton : Button
{
    private readonly CssProvider _style = new();

    public SquareButton(string label) : base(label)
    {
        // Keep the theme's vertical sizing, colors and borders while freeing
        // the horizontal padding that would make a one-character button wide.
        _style.LoadFromData("button { min-width: 0; padding-left: 0; padding-right: 0; }");
        StyleContext.AddProvider(_style, 600);
        SizeAllocated += (_, e) =>
        {
            if (WidthRequest != e.Allocation.Height) WidthRequest = e.Allocation.Height;
        };
    }

    protected override void OnGetPreferredWidth(out int minimumWidth, out int naturalWidth) =>
        GetPreferredHeight(out minimumWidth, out naturalWidth);
}
