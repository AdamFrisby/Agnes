using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// The settings rail's groups. Fourteen categories in one flat list read as a heap; grouped by where the
/// setting lives — this device, the connected host, the library of things you reuse, help — a person
/// can find "the host stuff" without reading every label. Membership is declared here, not on the
/// categories, so the flat list (which search and selection already use) stays as it is.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly (string Title, string[] Ids)[] NavGroupPlan =
    [
        ("This device", ["appearance", "cache", "keymap"]),
        ("Host", ["devices", "github", "collaborators", "sandboxes", "projects", "mcp", "localmodels", "plugins"]),
        ("Library", ["prompts", "profiles"]),
        ("Help", ["bugreport"]),
    ];

    public IReadOnlyList<SettingsNavGroup> SettingsNavGroups => _settingsNavGroups ??= BuildNavGroups();

    /// <summary>Whether the open page's settings live on a host this app is not connected to right now.
    /// Shown once, above the page, instead of each page saying it in its own words.</summary>
    public bool ShowNoHostNotice => ActiveHttpHost() is null && NavGroupPlan[1].Ids.Contains(SettingsCategory, StringComparer.Ordinal);

    private IReadOnlyList<SettingsNavGroup>? _settingsNavGroups;

    private IReadOnlyList<SettingsNavGroup> BuildNavGroups()
    {
        var byId = SettingsCategories.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<SettingsNavGroup>();
        foreach (var (title, ids) in NavGroupPlan)
        {
            var items = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
            foreach (var item in items) { placed.Add(item.Id); }
            if (items.Length > 0) { groups.Add(new SettingsNavGroup(title, items)); }
        }

        // A category the plan does not name (a plugin-contributed page, say) still gets a home.
        var rest = SettingsCategories.Where(c => !placed.Contains(c.Id)).ToArray();
        if (rest.Length > 0) { groups.Add(new SettingsNavGroup("More", rest)); }
        return groups;
    }
}

/// <summary>One group on the settings rail. Hides itself when a search has hidden every page in it.</summary>
public sealed partial class SettingsNavGroup : ObservableObject
{
    public SettingsNavGroup(string title, IReadOnlyList<SettingsCategoryVm> items)
    {
        Title = title;
        Items = items;
        foreach (var item in items)
        {
            item.PropertyChanged += OnItemChanged;
        }
    }

    public string Title { get; }
    public IReadOnlyList<SettingsCategoryVm> Items { get; }
    public bool IsVisible => Items.Any(i => i.IsVisible);

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsCategoryVm.IsVisible))
        {
            OnPropertyChanged(nameof(IsVisible));
        }
    }
}
