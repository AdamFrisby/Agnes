using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;

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

/// <summary>One left-nav category on the Settings tab; carries keywords so search can find it.</summary>
public sealed partial class SettingsCategoryVm : ObservableObject
{
    private readonly string _keywords;

    public SettingsCategoryVm(string id, string label, string icon, string keywords)
    {
        Id = id;
        Label = label;
        Icon = icon;
        _keywords = keywords.ToLowerInvariant();
    }

    public string Id { get; }
    public string Label { get; }
    public string Icon { get; }

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Whether this category matches a search query (by label or keywords).</summary>
    public bool Matches(string query)
        => query.Length == 0
           || Label.Contains(query, StringComparison.OrdinalIgnoreCase)
           || _keywords.Contains(query.ToLowerInvariant());
}
