using Avalonia.Markup.Xaml;

namespace Agnes.App.Mobile.Preview;

/// <summary>The harness's Avalonia application — same resources and styles as the Android head.</summary>
public partial class PreviewApp : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
