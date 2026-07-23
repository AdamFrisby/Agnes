using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>A node in a session's agent tree: the main agent or a (possibly nested) subagent.</summary>
public sealed class AgentNode : ObservableObject
{
    private bool _isSelected;

    public AgentNode(string? id, string name, bool isMain, Action<string?> select)
    {
        Id = id;
        Name = name;
        IsMain = isMain;
        SelectCommand = new RelayCommand(() => select(id));
    }

    /// <summary>Agent id (null for the main agent).</summary>
    public string? Id { get; }

    public string Name { get; }
    public bool IsMain { get; }

    /// <summary>Subagents spawned under this one.</summary>
    public ObservableCollection<AgentNode> Children { get; } = [];

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }
}
