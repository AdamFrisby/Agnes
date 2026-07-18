using Agnes.Ui.Core.Mvvm;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>A prompt the user lined up while a turn was running; sent when the turn ends.</summary>
public sealed class QueuedPrompt : ObservableObject
{
    private string _text;

    public QueuedPrompt(string text) => _text = text;

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }
}
