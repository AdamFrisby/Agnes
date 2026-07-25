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

    public override void OnCreate()
    {
        AndroidHost.Attach(this);
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder).LogToTrace();
}
