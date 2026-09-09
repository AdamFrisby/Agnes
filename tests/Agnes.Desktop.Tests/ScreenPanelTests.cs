using Agnes.App.Desktop.Controls;
using Agnes.App.Desktop.Views;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The Screen panel, rendered for real against the simulated graphical sandbox: does a frame actually reach
/// the picture, and does the keyboard let go when told.
/// </summary>
[Collection("Avalonia headless")]
public class ScreenPanelTests
{
    private sealed class TestApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    public static class TestAppBuilder
    {
        // Skia rather than the headless drawing stub, because the panel's picture is a RenderTargetBitmap and
        // the whole point of this test is that something was drawn into it.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<TestApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia();
    }

    private static (Window Window, ScreenPanelView View, DisplayViewModel Vm) Show()
    {
        var vm = new DisplayViewModel(new SimulatedHost(), "sim-1", ImmediateDispatcher.Instance);
        var view = new ScreenPanelView { DataContext = vm };
        var window = new Window { Width = 900, Height = 620, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    private static async Task PumpAsync(Func<bool> until, int attempts = 400)
    {
        for (var i = 0; i < attempts && !until(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public async Task The_panel_paints_a_frame_from_the_simulated_sandbox()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(async () =>
        {
            var (window, view, vm) = Show();

            await vm.ConnectCommand.ExecuteAsync(null);
            await PumpAsync(() => vm.LastFrameAt is not null);

            Assert.True(vm.IsConnected);
            Assert.NotNull(vm.LastFrameAt);
            Assert.Equal(1280, vm.Width);
            Assert.Equal(800, vm.Height);

            var image = Assert.Single(view.GetVisualDescendants().OfType<Image>());
            var bitmap = Assert.IsType<RenderTargetBitmap>(image.Source);
            Assert.Equal(new PixelSize(1280, 800), bitmap.PixelSize);

            // Not blank: a picture drawn from the synthetic desktop has more than one colour in it. A canvas
            // that was allocated but never drawn into is uniform, and that is the failure worth catching.
            Assert.True(IsNotBlank(bitmap), "the panel's canvas is a single flat colour — no frame was drawn");

            await vm.DisconnectCommand.ExecuteAsync(null);
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>Reads the canvas back and asks whether it holds more than one colour.</summary>
    private static bool IsNotBlank(RenderTargetBitmap bitmap)
    {
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var stride = width * 4;
        var buffer = new byte[stride * height];
        var scratch = System.Runtime.InteropServices.Marshal.AllocHGlobal(buffer.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), scratch, buffer.Length, stride);
            System.Runtime.InteropServices.Marshal.Copy(scratch, buffer, 0, buffer.Length);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(scratch);
        }

        var first = BitConverter.ToUInt32(buffer, 0);
        for (var i = 4; i < buffer.Length; i += 4 * 997) // a coprime stride, so the sweep isn't aligned to rows
        {
            if (BitConverter.ToUInt32(buffer, i) != first)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task Escape_twice_within_a_second_gives_the_keyboard_back()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(async () =>
        {
            var (window, view, vm) = Show();
            await vm.ConnectCommand.ExecuteAsync(null);
            await PumpAsync(() => vm.LastFrameAt is not null);

            var surface = Assert.Single(view.GetVisualDescendants().OfType<Panel>(), p => p.Name == "Surface");
            var badge = Assert.Single(view.GetVisualDescendants().OfType<Border>(), b => b.Name == "CaptureBadge");

            surface.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(badge.IsVisible, "focusing the surface should say that keys now go to the screen");

            // One Escape is a key for the guest and must NOT release.
            RaiseEscape(surface);
            Assert.True(badge.IsVisible, "a single Escape is a keystroke for the guest, not a request to leave");

            // A second one inside the window does release.
            RaiseEscape(surface);
            Dispatcher.UIThread.RunJobs();
            Assert.False(badge.IsVisible);
            Assert.Contains("Keyboard released", vm.Status, StringComparison.Ordinal);

            await vm.DisconnectCommand.ExecuteAsync(null);
            window.Close();
        }, CancellationToken.None);
    }

    private static void RaiseEscape(Panel surface)
    {
        surface.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Escape,
            Source = surface,
        });
        Dispatcher.UIThread.RunJobs();
    }
}

/// <summary>
/// The pointer transform and the keysym table — the two pieces of the panel that are pure functions, and the
/// two that go silently wrong rather than visibly wrong when they are.
/// </summary>
public class ScreenInputMappingTests
{
    // A 1280x800 guest in a 900x620 panel: width-bound, so there are letterbox bands top and bottom.
    private static DisplayFit WideFit() => DisplayFit.Compute(1280, 800, 900, 620);

    [Fact]
    public void The_fit_letterboxes_and_centres_the_picture()
    {
        var fit = WideFit();

        Assert.Equal(900.0 / 1280, fit.Scale, 6);
        Assert.Equal(0, fit.OffsetX, 6);            // width-bound: no bands at the sides
        Assert.Equal(900.0, fit.DrawnWidth, 6);
        Assert.Equal((620 - fit.DrawnHeight) / 2, fit.OffsetY, 6);
        Assert.True(fit.OffsetY > 0, "a 16:10 guest in a 900x620 panel must have bands above and below");
    }

    [Fact]
    public void Corners_and_centre_map_to_the_corners_and_centre_of_the_guest()
    {
        var fit = WideFit();

        Assert.Equal((0, 0), fit.ToGuest(fit.OffsetX, fit.OffsetY));
        Assert.Equal((1279, 799), fit.ToGuest(fit.OffsetX + fit.DrawnWidth, fit.OffsetY + fit.DrawnHeight));

        var (cx, cy) = fit.ToGuest(fit.OffsetX + (fit.DrawnWidth / 2), fit.OffsetY + (fit.DrawnHeight / 2));
        Assert.Equal(640, cx);
        Assert.Equal(400, cy);
    }

    [Fact]
    public void A_point_in_the_letterbox_is_outside_the_picture_but_still_clamps_into_the_guest()
    {
        var fit = WideFit();

        Assert.False(fit.Contains(450, 2));                  // in the band above the picture
        Assert.True(fit.Contains(450, 310));                 // dead centre
        Assert.Equal((450 * 1280 / 900, 0), fit.ToGuest(450, 2));
    }

    [Fact]
    public void A_taller_panel_than_the_guest_is_height_bound_instead()
    {
        var fit = DisplayFit.Compute(1280, 800, 640, 800);

        Assert.Equal(0.5, fit.Scale, 6);
        Assert.Equal(0, fit.OffsetX, 6);
        Assert.Equal(200, fit.OffsetY, 6);                    // (800 - 400) / 2
        Assert.Equal((640, 400), fit.ToGuest(320, 400));
    }

    [Fact]
    public void A_panel_with_no_size_maps_nothing_rather_than_dividing_by_zero()
    {
        var fit = DisplayFit.Compute(1280, 800, 0, 0);

        Assert.Equal(0, fit.Scale);
        Assert.False(fit.Contains(0, 0));
        Assert.Equal((0, 0), fit.ToGuest(100, 100));
    }

    [Theory]
    // The vocabulary the host's computer-use tooling actually uses.
    [InlineData(Key.Return, "Return")]  // Key.Enter is the same value in Avalonia
    [InlineData(Key.Tab, "Tab")]
    [InlineData(Key.Escape, "Escape")]
    [InlineData(Key.Back, "BackSpace")]
    [InlineData(Key.Delete, "Delete")]
    [InlineData(Key.Space, "space")]
    [InlineData(Key.Left, "Left")]
    [InlineData(Key.Right, "Right")]
    [InlineData(Key.Up, "Up")]
    [InlineData(Key.Down, "Down")]
    [InlineData(Key.Home, "Home")]
    [InlineData(Key.End, "End")]
    [InlineData(Key.PageUp, "Page_Up")]
    [InlineData(Key.PageDown, "Page_Down")]
    [InlineData(Key.LeftShift, "shift")]
    [InlineData(Key.RightShift, "shift")]
    [InlineData(Key.LeftCtrl, "ctrl")]
    [InlineData(Key.LeftAlt, "alt")]
    [InlineData(Key.LWin, "super")]
    [InlineData(Key.A, "a")]
    [InlineData(Key.Z, "z")]
    [InlineData(Key.D0, "0")]
    [InlineData(Key.D9, "9")]
    [InlineData(Key.NumPad0, "KP_0")]
    [InlineData(Key.F1, "F1")]
    [InlineData(Key.F12, "F12")]
    public void The_keysym_table_names_the_keys_the_guest_understands(Key key, string keysym)
        => Assert.Equal(keysym, X11Keysyms.For(key));

    [Fact]
    public void Every_letter_digit_and_function_key_has_a_name()
    {
        for (var key = Key.A; key <= Key.Z; key++)
        {
            Assert.NotNull(X11Keysyms.For(key));
        }

        for (var key = Key.D0; key <= Key.D9; key++)
        {
            Assert.NotNull(X11Keysyms.For(key));
        }

        for (var key = Key.F1; key <= Key.F12; key++)
        {
            Assert.NotNull(X11Keysyms.For(key));
        }
    }

    [Fact]
    public void Layout_dependent_characters_are_left_to_text_input()
    {
        // Deliberately absent from the key map: what these keys produce depends on the layout, and guessing
        // would be wrong on most keyboards in the world.
        Assert.Null(X11Keysyms.For(Key.OemComma));
        Assert.Null(X11Keysyms.For(Key.OemQuestion));

        // They arrive as text instead, already resolved by the platform.
        Assert.Equal("?", X11Keysyms.ForText("?"));
        Assert.Equal("ü", X11Keysyms.ForText("ü"));
        Assert.Equal("space", X11Keysyms.ForText(" "));
        Assert.Null(X11Keysyms.ForText("\t"));   // control characters are the key map's job
        Assert.Null(X11Keysyms.ForText("ab"));   // never a whole string
        Assert.Null(X11Keysyms.ForText(null));
    }
}
