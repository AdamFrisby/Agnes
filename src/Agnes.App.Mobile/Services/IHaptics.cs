namespace Agnes.App.Mobile.Services;

/// <summary>
/// Physical feedback for moments the eye may miss. Used sparingly and only where something actually
/// changed in the world: a prompt left the device, a turn finished, an approval landed. Never for
/// navigation — a phone that buzzes on every tap gets its haptics turned off.
/// </summary>
public interface IHaptics
{
    /// <summary>A light tick: the composer sent, an option was chosen.</summary>
    void Tick();

    /// <summary>A double pulse: the agent finished a turn.</summary>
    void Success();

    /// <summary>A heavier pulse: something is blocked on the user, or failed.</summary>
    void Alert();
}

/// <summary>Haptics disabled (the setting is off, or the device has no vibrator).</summary>
public sealed class NullHaptics : IHaptics
{
    public static readonly NullHaptics Instance = new();

    public void Tick() { }
    public void Success() { }
    public void Alert() { }
}
