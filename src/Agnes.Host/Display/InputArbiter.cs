using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Host.Display;

/// <summary>
/// Who is allowed to drive the guest's pointer and keyboard, and how fast. Two hands on one mouse is not a
/// UI problem to be smoothed over — it is a correctness problem: the agent's next screenshot would show the
/// consequences of somebody else's click and it would reason from it as its own. So control is <em>held</em>,
/// exactly one holder at a time, and the transitions are facts appended to the session log.
/// <para>
/// The asymmetry is deliberate. A person takes control by asking, and wins immediately. The agent never
/// takes control from a person; it takes it only from nobody, implicitly, on its first input of a turn — so
/// an autonomous session needs no ceremony, while a person who has reached for the mouse is never fought.
/// A hold expires on its own after <see cref="DisplayOptions.ControlIdleSeconds"/>, because the failure mode
/// of "person walks away" must not be "agent is locked out forever".
/// </para>
/// <para>Pure and self-contained on purpose: it owns its own state, takes the clock as a dependency, and
/// returns the notice to publish rather than publishing anything itself.</para>
/// </summary>
public sealed class InputArbiter
{
    private readonly DisplayOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    // A rolling window of when input was injected, oldest first, trimmed to the last minute on each check.
    private readonly Queue<DateTimeOffset> _recent = new();

    private DisplayControlHolder _holder = DisplayControlHolder.None;
    private string? _holderDeviceId;
    private DateTimeOffset _lastUserActivity;

    public InputArbiter(DisplayOptions options, TimeProvider? time = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The message an agent tool throws with when a person is driving. Worded as an instruction
    /// because the model reads it as the tool's error and must know what to do next.</summary>
    public const string UserHoldsMessage = "The user has taken control of the display; ask before continuing.";

    public DisplayControlHolder Holder
    {
        get
        {
            lock (_gate)
            {
                return _holder;
            }
        }
    }

    public string? HolderDeviceId
    {
        get
        {
            lock (_gate)
            {
                return _holderDeviceId;
            }
        }
    }

    /// <summary>The current holder as the wire states it, for an <c>Info</c> frame.</summary>
    public DisplayControlNotice Current
    {
        get
        {
            lock (_gate)
            {
                return new DisplayControlNotice(_holder, _holderDeviceId);
            }
        }
    }

    /// <summary>
    /// A person takes (<paramref name="take"/> true) or hands back the display. Returns the notice to
    /// publish, or null when nothing changed — so a client hammering "take" produces one log entry, not one
    /// per click. Handing back only works for the device that holds it.
    /// </summary>
    public DisplayControlNotice? RequestControl(string deviceId, bool take)
    {
        lock (_gate)
        {
            ExpireIdleHold();

            if (take)
            {
                _lastUserActivity = _time.GetUtcNow();
                if (_holder == DisplayControlHolder.User && string.Equals(_holderDeviceId, deviceId, StringComparison.Ordinal))
                {
                    return null;
                }

                _holder = DisplayControlHolder.User;
                _holderDeviceId = deviceId;
                return new DisplayControlNotice(_holder, _holderDeviceId);
            }

            if (_holder != DisplayControlHolder.User || !string.Equals(_holderDeviceId, deviceId, StringComparison.Ordinal))
            {
                return null; // you cannot hand back what you were not holding.
            }

            return Clear();
        }
    }

    /// <summary>Releases a hold belonging to a device whose channel has closed. A browser tab that went away
    /// must not keep the agent locked out until the idle timer catches up.</summary>
    public DisplayControlNotice? ReleaseFor(string deviceId)
    {
        lock (_gate)
        {
            return _holder == DisplayControlHolder.User && string.Equals(_holderDeviceId, deviceId, StringComparison.Ordinal)
                ? Clear()
                : null;
        }
    }

    /// <summary>Releases an expired hold if the clock has passed the idle timeout. Called on the timer sweep
    /// as well as implicitly on every decision, so a hold expires even with nothing else happening.</summary>
    public DisplayControlNotice? ReleaseIfIdle()
    {
        lock (_gate)
        {
            return ExpireIdleHold();
        }
    }

    /// <summary>
    /// The agent asks to inject. Returns the notice to publish when it implicitly took control (holder was
    /// None), or null when it already held it.
    /// </summary>
    /// <exception cref="InvalidOperationException">A person is driving.</exception>
    public DisplayControlNotice? ClaimForAgent()
    {
        lock (_gate)
        {
            ExpireIdleHold();

            if (_holder == DisplayControlHolder.User)
            {
                throw new InvalidOperationException(UserHoldsMessage);
            }

            if (_holder == DisplayControlHolder.Agent)
            {
                return null;
            }

            _holder = DisplayControlHolder.Agent;
            _holderDeviceId = null;
            return new DisplayControlNotice(_holder, _holderDeviceId);
        }
    }

    /// <summary>Whether the agent could inject right now, without claiming anything. Used by the tools that
    /// only look (a screenshot never takes control).</summary>
    public bool AgentMayInject
    {
        get
        {
            lock (_gate)
            {
                ExpireIdleHold();
                return _holder != DisplayControlHolder.User;
            }
        }
    }

    /// <summary>A person's input arrived; keeps their hold alive.</summary>
    public void NoteUserActivity()
    {
        lock (_gate)
        {
            _lastUserActivity = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// Spends <paramref name="count"/> events from the session's rolling per-minute budget, or throws. One
    /// budget for the session rather than one per actor: the resource being protected is the guest's input
    /// queue, and it does not care who filled it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The budget is exhausted; the message names the limit.</exception>
    public void SpendBudget(int count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var cutoff = now - TimeSpan.FromMinutes(1);
            while (_recent.Count > 0 && _recent.Peek() < cutoff)
            {
                _recent.Dequeue();
            }

            if (_recent.Count + count > _options.InputEventsPerMinute)
            {
                throw new InvalidOperationException(
                    $"This session's display input budget is spent ({_options.InputEventsPerMinute} events a minute). "
                    + "Wait a few seconds and look at the screen before trying again.");
            }

            for (var i = 0; i < count; i++)
            {
                _recent.Enqueue(now);
            }
        }
    }

    /// <summary>Whether a budget spend of <paramref name="count"/> would be refused, without spending it.</summary>
    public bool WouldExceedBudget(int count)
    {
        lock (_gate)
        {
            var cutoff = _time.GetUtcNow() - TimeSpan.FromMinutes(1);
            var live = _recent.Count(t => t >= cutoff);
            return live + count > _options.InputEventsPerMinute;
        }
    }

    /// <summary>Checks one agent tool call's expansion against the per-call ceiling and the blocked-chord list.
    /// The chord list is agent-only; see <see cref="DisplayOptions.BlockedChords"/> for why.</summary>
    /// <exception cref="InvalidOperationException">Too many events for one call.</exception>
    public void CheckAgentCall(int eventCount)
    {
        if (eventCount > _options.InputEventsPerToolCall)
        {
            throw new InvalidOperationException(
                $"That would be {eventCount} input events in one call; the limit is {_options.InputEventsPerToolCall}. "
                + "Break it into smaller steps and check the screen between them.");
        }
    }

    /// <summary>Refuses a chord the operator has blocked for agents.</summary>
    /// <exception cref="InvalidOperationException">The chord is on the blocked list.</exception>
    public void CheckAgentChord(KeyChord chord)
    {
        if (chord.IsBlockedBy(_options.BlockedChords))
        {
            throw new InvalidOperationException(
                $"'{chord.Normalize()}' is blocked on this host: it would leave the application you are working in. "
                + "Stay inside the window, or ask the user to do it.");
        }
    }

    // Must be called under _gate.
    private DisplayControlNotice? ExpireIdleHold()
    {
        if (_holder != DisplayControlHolder.User || _options.ControlIdleSeconds <= 0)
        {
            return null;
        }

        return _time.GetUtcNow() - _lastUserActivity >= TimeSpan.FromSeconds(_options.ControlIdleSeconds)
            ? Clear()
            : null;
    }

    // Must be called under _gate.
    private DisplayControlNotice Clear()
    {
        _holder = DisplayControlHolder.None;
        _holderDeviceId = null;
        return new DisplayControlNotice(_holder, _holderDeviceId);
    }
}
