using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Recycling.Model;
using Avalonia.Controls.Templates;

namespace Agnes.App.Desktop;

/// <summary>
/// A Dock content recycler that caches one realized view per data item (by reference) and always
/// resolves the view via the ambient DataTemplates. This gives each document its own view (so tab
/// switches keep their state) AND, crucially, each document TYPE gets its own view — fixing Dock's
/// default behaviour of handing a newly-activated document of a different type the previously-built
/// view (which showed a SessionTabView for the Settings tab).
/// </summary>
public sealed class PerItemControlRecycling : IControlRecycling
{
    private readonly Dictionary<object, Control> _cache = new(ReferenceEqualityComparer.Instance);

    // Part of the interface; we always key by the data item itself, so this is unused.
    public bool TryToUseIdAsKey { get; set; }

    public bool TryGetValue(object? data, out object? control)
    {
        control = null;
        if (data is not null && _cache.TryGetValue(data, out var existing))
        {
            control = existing;
            return true;
        }

        return false;
    }

    public void Add(object data, object control)
    {
        if (control is Control c)
        {
            _cache[data] = c;
        }
    }

    public object? Build(object? data, object? existing, object? parent)
    {
        if (data is null)
        {
            return null;
        }

        if (_cache.TryGetValue(data, out var cached))
        {
            return cached;
        }

        var template = (parent as Control)?.FindDataTemplate(data);
        var built = template?.Build(data);
        if (built is Control control)
        {
            control.DataContext = data;
            _cache[data] = control;
        }

        return built;
    }

    /// <summary>Drops a cached view (call when a dockable is closed, so it doesn't leak).</summary>
    public void Forget(object data) => _cache.Remove(data);

    public void Clear() => _cache.Clear();
}
