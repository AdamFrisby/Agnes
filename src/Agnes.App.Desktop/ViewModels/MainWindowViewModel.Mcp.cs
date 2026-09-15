using System.Collections.ObjectModel;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>The MCP servers page's own presentation state: rows the list can read, and a registry failure
/// line kept apart from the count.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The configured servers as rows: the wire record plus the words a row shows for it.</summary>
    public ObservableCollection<McpServerRowVm> McpServerRows { get; } = [];

    public bool HasMcpServers => McpServerRows.Count > 0;

    /// <summary>Registries that could not answer the last search, one sentence each; empty when all did.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMcpCatalogFailure))]
    private string _mcpCatalogFailure = string.Empty;

    public bool HasMcpCatalogFailure => McpCatalogFailure.Length > 0;

    /// <summary>How many presets are a quick install. The curated set is four; the host lists them first.</summary>
    private const int QuickInstallCount = 4;

    /// <summary>The curated presets, as cards.</summary>
    public ObservableCollection<McpPresetRowVm> McpQuickPresets { get; } = [];

    /// <summary>Everything else the registries lead with, one row per server, shown under Find more until a
    /// search replaces it. The same host list, de-duplicated — without that, one server appeared four times.</summary>
    public ObservableCollection<McpPresetRowVm> McpMorePresets { get; } = [];

    public bool HasMcpMorePresets => McpMorePresets.Count > 0;

    /// <summary>"31 more servers the registries lead with. Search to narrow them down."</summary>
    public string McpMorePresetsNote => McpMorePresets.Count switch
    {
        0 => string.Empty,
        1 => "One more server the registries lead with. Search to find others.",
        var n => $"{n} more servers the registries lead with. Search to narrow them down.",
    };

    private void RebuildPresetGroups()
    {
        McpQuickPresets.Clear();
        McpMorePresets.Clear();
        var distinct = McpCatalogShaping.DistinctPresets(McpPresets.Select(p => p.Preset));
        var byPreset = McpPresets.ToDictionary(p => p.Preset, p => p);
        foreach (var (info, i) in distinct.Select((p, i) => (p, i)))
        {
            (i < QuickInstallCount ? McpQuickPresets : McpMorePresets).Add(byPreset[info]);
        }

        OnPropertyChanged(nameof(HasMcpMorePresets));
        OnPropertyChanged(nameof(McpMorePresetsNote));
    }

    private void RebuildMcpServerRows()
    {
        McpServerRows.Clear();
        foreach (var s in McpServers)
        {
            McpServerRows.Add(new McpServerRowVm(s));
        }

        OnPropertyChanged(nameof(HasMcpServers));
    }

    private static string DescribeMcpServers(IReadOnlyList<McpServerInfo> list)
    {
        if (list.Count == 0)
        {
            return "No MCP servers on this host yet.";
        }

        var enabled = list.Count(s => s.Enabled);
        var servers = list.Count == 1 ? "One server on this host" : $"{list.Count} servers on this host";
        return enabled == list.Count ? servers + ", all enabled." : $"{servers}, {enabled} enabled.";
    }
}

/// <summary>One configured MCP server, with its facts in words: where it runs, how it talks, whom it applies to.</summary>
public sealed class McpServerRowVm
{
    public McpServerRowVm(McpServerInfo info) => Info = info;

    public McpServerInfo Info { get; }

    public string Id => Info.Id;
    public string Name => Info.Name;
    public bool Enabled => Info.Enabled;

    public string RunAtLabel => string.Equals(Info.RunAt, "sandbox", StringComparison.OrdinalIgnoreCase) ? "in the sandbox" : "on the host";

    public string Transport => Info.Transport.ToLowerInvariant();

    public string ScopeLabel => Info.ApplyScope switch
    {
        McpApplyScope.ThisHost => "this host",
        McpApplyScope.AllHosts => "every host",
        _ => "this workspace",
    };

    /// <summary>What it launches or where it lives — the line that tells two servers of one name apart.</summary>
    public string Launch => string.Equals(Info.Transport, "http", StringComparison.OrdinalIgnoreCase)
        ? Info.Url ?? string.Empty
        : string.Join(' ', new[] { Info.Command }.Concat(Info.Args).Where(s => !string.IsNullOrEmpty(s)));

    public string ToggleLabel => Enabled ? "Disable" : "Enable";
}
