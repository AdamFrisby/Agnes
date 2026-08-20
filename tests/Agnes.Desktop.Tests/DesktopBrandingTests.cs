using Agnes.App.Desktop;
using Agnes.App.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

namespace Agnes.Desktop.Tests;

[Collection("Avalonia headless")]
public sealed class DesktopBrandingTests
{
    [Fact]
    public async Task Application_and_about_surface_use_agnes_branding()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(BrandingTestApp));
        await session.Dispatch(AssertBranding, CancellationToken.None);
    }

    private static void AssertBranding()
    {
        var app = Assert.IsType<Agnes.App.Desktop.App>(Application.Current);

        Assert.Equal("Agnes", app.Name);

        var menu = Assert.IsType<NativeMenu>(NativeMenu.GetMenu(app));
        var aboutItem = Assert.IsType<NativeMenuItem>(Assert.Single(menu.Items));
        Assert.Equal("About Agnes", aboutItem.Header);

        var about = new AboutAgnesWindow();
        Assert.Equal("About Agnes", about.Title);
        Assert.NotNull(about.FindControl<Image>("BrandLogo")?.Source);
        Assert.Equal("Agnes", about.FindControl<TextBlock>("ProductNameText")?.Text);
        Assert.Equal(DesktopBranding.Version, about.FindControl<TextBlock>("VersionText")?.Text);
        Assert.Equal("A remote interface to coding CLIs.", about.FindControl<TextBlock>("DescriptionText")?.Text);
        Assert.Equal("Learn more about Agnes", about.FindControl<Button>("LearnMoreButton")?.Content);
        Assert.Equal(DesktopBranding.Copyright, about.FindControl<TextBlock>("CopyrightText")?.Text);
    }

    [Fact]
    public void Learn_more_targets_the_agnes_repository()
    {
        Assert.Equal("https://github.com/AdamFrisby/Agnes", DesktopBranding.RepositoryUri.AbsoluteUri.TrimEnd('/'));
        Assert.StartsWith("Version ", DesktopBranding.Version, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(DesktopBranding.Copyright));
    }
}

public static class BrandingTestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Agnes.App.Desktop.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}
