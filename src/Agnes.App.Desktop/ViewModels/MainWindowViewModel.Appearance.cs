using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>The Appearance page's own commands: the chat font size as two buttons, not only a key chord.</summary>
public sealed partial class MainWindowViewModel
{
    public IRelayCommand IncreaseChatFontScaleCommand => _increaseChatFont ??= new RelayCommand(() => AdjustChatFontSize(+1));

    public IRelayCommand DecreaseChatFontScaleCommand => _decreaseChatFont ??= new RelayCommand(() => AdjustChatFontSize(-1));

    private IRelayCommand? _increaseChatFont;
    private IRelayCommand? _decreaseChatFont;
}
