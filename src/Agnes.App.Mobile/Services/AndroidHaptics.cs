using Android.Content;
using Android.OS;

namespace Agnes.App.Mobile.Services;

/// <summary>
/// Vibrator-backed haptics. Gated by the user's <see cref="MobileSettings.Haptics"/> preference, which
/// is read through a delegate so flipping the switch takes effect without rebuilding anything.
/// </summary>
public sealed class AndroidHaptics : IHaptics
{
    private readonly Vibrator? _vibrator;
    private readonly Func<bool> _enabled;

    public AndroidHaptics(Context context, Func<bool> enabled)
    {
        _enabled = enabled;
        try
        {
            // VibratorManager is the API 31+ route; the direct VIBRATOR_SERVICE cast still works below it.
            if (OperatingSystem.IsAndroidVersionAtLeast(31)
                && context.GetSystemService(Context.VibratorManagerService) is VibratorManager manager)
            {
                _vibrator = manager.DefaultVibrator;
            }
            else
            {
#pragma warning disable CA1422 // VIBRATOR_SERVICE is obsolete from 31; this branch only runs below it.
                _vibrator = context.GetSystemService(Context.VibratorService) as Vibrator;
#pragma warning restore CA1422
            }
        }
        catch
        {
            _vibrator = null; // a device without a vibrator simply has no haptics.
        }
    }

    public void Tick() => OneShot(12, 90);

    public void Success() => Pattern([0, 18, 70, 26]);

    public void Alert() => Pattern([0, 34, 90, 34, 90, 46]);

    private void OneShot(long milliseconds, int amplitude)
    {
        if (!Ready())
        {
            return;
        }

        try
        {
            _vibrator!.Vibrate(VibrationEffect.CreateOneShot(milliseconds, amplitude));
        }
        catch
        {
            // Haptics are a nicety; never let one break an interaction.
        }
    }

    private void Pattern(long[] timings)
    {
        if (!Ready())
        {
            return;
        }

        try
        {
            _vibrator!.Vibrate(VibrationEffect.CreateWaveform(timings, -1));
        }
        catch
        {
            // as above
        }
    }

    private bool Ready() => _vibrator is { HasVibrator: true } && _enabled();
}
