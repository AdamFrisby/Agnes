// The Android SDK contributes implicit global usings (Android.App, Android.Widget, …) to every file in
// this head, which collides with Avalonia on a handful of very common names. Aliasing the UI meaning
// once here is clearer than qualifying `Avalonia.Controls.Button` at a dozen call sites — this project
// is an Avalonia app that happens to run on Android, not the other way round.
//
// `Application` is deliberately NOT aliased: `[Application]` in MainApplication.cs is the Android
// attribute, and an alias there would be a trap rather than a convenience.
global using Button = Avalonia.Controls.Button;
global using Color = Avalonia.Media.Color;
global using View = Avalonia.Controls.Control;
