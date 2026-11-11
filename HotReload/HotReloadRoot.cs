using System;
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
        var newContent = _factory();
        Content = newContent;
    }
}