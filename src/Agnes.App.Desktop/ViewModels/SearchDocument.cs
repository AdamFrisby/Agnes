using Agnes.Ui.Core.ViewModels;
using Dock.Model.Mvvm.Controls;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// A first-class Search tab: host-backed full-text search over every session's transcript (see
/// <c>.ideas/ops/02-memory-search.md</c>), complementing the top-bar search that only covers open tabs.
/// The tab is just the container; the <see cref="MemorySearchViewModel"/> it hosts is owned by the window.
/// </summary>
public sealed class SearchDocument : Document
{
    public SearchDocument(MemorySearchViewModel search)
    {
        Search = search;
        Id = "memory-search";
        Title = "Search";
        CanClose = true;
    }

    public MemorySearchViewModel Search { get; }
}
