using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Agnes.App.Mobile;

/// <summary>
/// The Android application object that owns the Avalonia app lifetime. Avalonia 12 splits the Android
/// head this way: the application object configures the <see cref="AppBuilder"/>, and the activity is a
/// plain host for the view.
/// </summary>
[Application(
    Label = "Agnes",
    Theme = "@style/AgnesSplashTheme",
    Icon = "@mipmap/ic_launcher",
    // Agnes talks TLS to a host the user runs themselves, which in practice is often a LAN address with
    // a private CA. Cleartext stays OFF; the trust anchors are declared in Resources/xml/network_security_config.
    UsesCleartextTraffic = false,
    NetworkSecurityConfig = "@xml/network_security_config")]
public sealed class MainApplication : AvaloniaAndroidApplication<App>
{
    public MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    /// <summary>
    /// Runs before Avalonia starts, which is early enough to hand the application context to
    /// <see cref="AndroidHost"/>.
    ///
    /// Deliberately not <c>OnCreate</c>: overriding a Java method here would put a native method on this
    /// class's Java-callable wrapper, and the wrapper for an Application deriving from a *generic* base
    /// is the exact thing that fails to bind in a packaged APK. Keeping the override managed-only means
    /// there is nothing to bind. See the marshal-methods note in the csproj.
    /// </summary>
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        AndroidHost.Attach(this);
        return base.CustomizeAppBuilder(builder).LogToTrace();
    }
}
