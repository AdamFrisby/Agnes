using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>A prompt the user lined up while a turn was running; sent when the turn ends.</summary>
public sealed class QueuedPrompt : ObservableObject
{
    private string _text;

    public QueuedPrompt(string text) => _text = text;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }
}
