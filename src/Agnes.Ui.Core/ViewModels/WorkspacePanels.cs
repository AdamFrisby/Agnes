using Agnes.Ui.Core.Mvvm;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>Full content shown in a tab's right-hand preview column (e.g. a full diff).</summary>
public sealed record PreviewViewModel(string Title, string Body, string Kind);

/// <summary>A file touched by a tool call, listed in the left "Files modified" panel.</summary>
public sealed class FileEntry : ObservableObject
{
    private string _statusText;
    private string _detail;

    public FileEntry(string toolCallId, string name, string statusText, string detail)
    {
        ToolCallId = toolCallId;
        Name = name;
        _statusText = statusText;
        _detail = detail;
    }

    public string ToolCallId { get; }
    public string Name { get; }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    /// <summary>Full detail (e.g. a diff) shown in the preview when this file is opened.</summary>
    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }
}
