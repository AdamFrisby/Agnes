using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Agnes.Abstractions;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using FluentIcons.Common;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// A first-class Settings tab (a document, like a session), so settings get a real, searchable surface
/// with room to grow — instead of one cramped flyout. It binds through to the owning window's settings
/// state (theme, MCP, GitHub, sandbox image, …); the tab is just the container.
/// </summary>
public sealed class SettingsDocument : Document
{
    public SettingsDocument(MainWindowViewModel owner)
    {
        Owner = owner;
        Id = "settings";
        Title = "Settings";
        CanClose = true;
    }

    public MainWindowViewModel Owner { get; }
}

/// <summary>
/// A connected host addressed over REST: where it is, the token to present, and — the part that is easy to
/// forget and fatal to forget — the certificate fingerprint it is authenticated by.
///
/// Agnes hosts are commonly self-signed and pinned. The hub connection has always honoured that pin, but every
/// management call behind the settings surface used to be made with a default <c>HttpClient</c>, which rejects
/// exactly those certificates: sessions worked while Devices, MCP, Projects, Sandboxes and GitHub all failed
/// the TLS handshake. Passing this record around instead of a bare (url, token) pair means the pin travels with
/// the address, and <see cref="Http"/> is the only client any of those calls can reach for.
/// </summary>
public sealed record HostEndpoint(string Url, string Token, string? Fingerprint)
{
    /// <summary>The endpoint a connected tab is talking to, reading the pin off the live connection so the
    /// REST calls and the hub can't disagree about how the host is trusted.</summary>
    public static HostEndpoint Of(SessionDocument document)
        => new(document.Host!.HostUrl, document.HostToken, document.Host!.PinnedFingerprint);

    /// <summary>A client that trusts this host the same way its hub connection does.</summary>
    public HttpClient Http => Agnes.Client.AgnesHttp.For(Fingerprint);
}

/// <summary>One left-nav category on the Settings tab; carries keywords so search can find it.</summary>
public sealed partial class SettingsCategoryVm : ObservableObject
{
    private readonly string _keywords;

    public SettingsCategoryVm(string id, string label, Symbol icon, string keywords)
    {
        Id = id;
        Label = label;
        Icon = icon;
        _keywords = keywords.ToLowerInvariant();
    }

    public string Id { get; }
    public string Label { get; }

    /// <summary>The category's glyph, named from the icon catalogue rather than carried as a character,
    /// so the compiler checks it and it takes the row's foreground like the label does.</summary>
    public Symbol Icon { get; }

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Whether this category matches a search query (by label or keywords).</summary>
    public bool Matches(string query)
        => query.Length == 0
           || Label.Contains(query, StringComparison.OrdinalIgnoreCase)
           || _keywords.Contains(query.ToLowerInvariant());
}

/// <summary>
/// One sandbox VM on the Sandboxes page. Wraps the record so the row can hold the armed state for a two-step
/// delete: deleting is permanent destruction of a VM and its contents, which is not a single-click action.
/// </summary>
public sealed partial class SandboxRowVm : ObservableObject
{
    public SandboxRowVm(SandboxRecordDto record) => Record = record;

    public SandboxRecordDto Record { get; }

    public string SessionId => Record.SessionId;
    public string Title => Record.Title;
    public string State => Record.State;
    public bool Live => Record.Live;
    public string WorkingDirectory => Record.WorkingDirectory;
    public string VmName => Record.VmName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteLabel))]
    private bool _isConfirmingDelete;

    public string DeleteLabel => IsConfirmingDelete ? "Delete for good?" : "Delete";
}

/// <summary>
/// One curated MCP preset, plus whether the host already has a server of that name. Without that, every
/// preset offers "Install" forever and the only way to know whether you already installed Playwright is to
/// scroll down to the configured list and check by eye.
/// </summary>
public sealed class McpPresetRowVm
{
    public McpPresetRowVm(McpServerInfo preset, bool isInstalled)
    {
        Preset = preset;
        IsInstalled = isInstalled;
    }

    public McpServerInfo Preset { get; }

    public bool IsInstalled { get; }

    public string Name => Preset.Name;

    /// <summary>What the preset runs, so "Install" isn't a blind click.</summary>
    public string Command => string.Equals(Preset.Transport, "http", StringComparison.OrdinalIgnoreCase)
        ? Preset.Url ?? string.Empty
        : string.Join(' ', new[] { Preset.Command }.Concat(Preset.Args).Where(s => !string.IsNullOrEmpty(s)));

    public string ActionLabel => IsInstalled ? "Installed" : "Install";

    public bool CanInstall => !IsInstalled;
}

/// <summary>
/// One MCP server found in a registry. Beyond the name it carries the two things that decide whether you want
/// it: what it does, and what it will demand of you — a server needing an API key you don't have is better
/// known before installing than after it fails to start.
/// </summary>
public sealed class McpCatalogRowVm
{
    public McpCatalogRowVm(CatalogHit<McpCatalogEntry> hit, bool isInstalled)
    {
        CatalogId = hit.CatalogId;
        CatalogName = hit.CatalogName;
        Entry = hit.Entry;
        IsInstalled = isInstalled;
    }

    public string CatalogId { get; }
    public string CatalogName { get; }
    public McpCatalogEntry Entry { get; }
    public bool IsInstalled { get; }

    public string EntryId => Entry.Id;
    public string Name => Entry.Name;
    public string Description => Entry.Description ?? "No description.";

    /// <summary>Who publishes it, which registry it came from, and how it runs.</summary>
    public string Provenance
    {
        get
        {
            var parts = new List<string>();
            if (Entry.Publisher is { Length: > 0 } publisher)
            {
                parts.Add(publisher);
            }

            parts.Add(Entry.Transport == McpCatalogTransport.Http ? "hosted (http)" : "runs locally (stdio)");
            parts.Add(CatalogName);
            return string.Join(" · ", parts);
        }
    }

    public bool NeedsConfiguration => Entry.RequiredEnvironment.Count > 0;

    /// <summary>The environment variables that must be filled in for this server to work, named.</summary>
    public string RequiredEnvironment => string.Join(", ", Entry.RequiredEnvironment.Select(v => v.Name));

    public string RequiredEnvironmentLabel => $"needs {RequiredEnvironment}";

    public string ActionLabel => IsInstalled ? "Installed" : "Install";

    public bool CanInstall => !IsInstalled;
}

/// <summary>One linked GitHub account on the GitHub page, holding the armed state for a two-step unlink.</summary>
public sealed partial class GitHubAccountRowVm : ObservableObject
{
    public GitHubAccountRowVm(string account) => Account = account;

    public string Account { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnlinkLabel))]
    private bool _isConfirmingUnlink;

    public string UnlinkLabel => IsConfirmingUnlink ? "Really unlink?" : "Unlink";
}

/// <summary>
/// One paired device on the Devices page. Two things the raw <see cref="DeviceInfo"/> can't do on its own:
/// say when the device was last actually used (the answer to "which of these is stale?"), and hold the
/// armed state for a two-step revoke. Revoking is irreversible and can cut off the device you're sitting at,
/// so the first click arms and the second commits — no modal, no accidental lockout.
/// </summary>
public sealed partial class DeviceRowVm : ObservableObject
{
    public DeviceRowVm(DeviceInfo info, DateTimeOffset now)
    {
        Info = info;
        Detail = Describe(info, now);
    }

    public DeviceInfo Info { get; }

    public string Id => Info.Id;
    public string Name => Info.Name;

    /// <summary>True for the device this client is connected on — revoking it signs you out.</summary>
    public bool IsCurrentDevice => Info.IsCurrentDevice;

    /// <summary>When it paired and when it was last seen, in one line.</summary>
    public string Detail { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RevokeLabel))]
    private bool _isConfirmingRevoke;

    public string RevokeLabel => IsConfirmingRevoke
        ? (IsCurrentDevice ? "Sign this device out?" : "Really revoke?")
        : "Revoke";

    /// <summary>
    /// "paired 2026-07-01 · active now" / "· last seen 3 days ago" / "· never connected". A device that has
    /// never connected, or hasn't in months, is the one you're looking for when you came here to revoke
    /// something — so the list says which is which rather than making you guess from the pairing date.
    /// </summary>
    internal static string Describe(DeviceInfo info, DateTimeOffset now)
    {
        var paired = $"paired {info.PairedAt.ToLocalTime():yyyy-MM-dd}";
        if (info.LastSeenAt is not { } seen)
        {
            return paired + " · never connected";
        }

        var ago = now - seen;
        var lastSeen = ago switch
        {
            { TotalMinutes: < 5 } => "active now",
            { TotalMinutes: < 60 } => $"last seen {(int)ago.TotalMinutes} min ago",
            { TotalHours: < 24 } => $"last seen {(int)ago.TotalHours}h ago",
            { TotalDays: < 30 } => $"last seen {(int)ago.TotalDays}d ago",
            _ => $"last seen {seen.ToLocalTime():yyyy-MM-dd}",
        };

        return $"{paired} · {lastSeen}";
    }
}
