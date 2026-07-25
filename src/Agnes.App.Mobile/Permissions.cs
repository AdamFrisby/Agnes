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
