using Avalonia.Controls;

namespace CAAI;

public sealed class HotReloadRoot : ContentControl
{
    private readonly Func<Control> _factory;

    public HotReloadRoot(Func<Control> factory)
    {
        _factory = factory;
        Rebuild();
    }

    public void Rebuild()
    {
        // Construct first, so a failed build doesn't destroy
        // the currently displayed UI.
        var newContent = _factory();

        Content = newContent;
    }
}