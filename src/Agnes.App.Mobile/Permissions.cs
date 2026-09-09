using Android.App;

// Declared here rather than in a hand-written manifest so the Android SDK merges them and the reasons
// stay next to the request.
//
// POST_NOTIFICATIONS is the whole point of the app being on a phone: an agent that gets blocked while
// the screen is off has to be able to say so. VIBRATE backs the haptics. INTERNET reaches the host —
// there is no local mode; every session lives on a machine somewhere else.
[assembly: UsesPermission(global::Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(global::Android.Manifest.Permission.AccessNetworkState)]
[assembly: UsesPermission(global::Android.Manifest.Permission.PostNotifications)]
[assembly: UsesPermission(global::Android.Manifest.Permission.Vibrate)]

// Saving a file an agent sent to Downloads. Capped at API 28 because from 29 the MediaStore write needs
// no permission at all — asking for broad storage access on a modern device in order to save one
// screenshot would be an enormous ask for a small feature, and Android rightly buries the setting.
[assembly: UsesPermission(
    global::Android.Manifest.Permission.WriteExternalStorage,
    MaxSdkVersion = 28)]
