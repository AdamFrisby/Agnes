using System.Collections.ObjectModel;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// Brings an <see cref="ObservableCollection{T}"/> to a desired state by the smallest set of edits,
/// instead of <c>Clear()</c> + re-add.
///
/// <para>The difference is not cosmetic. <c>Clear()</c> raises a <b>Reset</b>, and a Reset tells every
/// bound <c>ItemsControl</c> that it knows nothing: it drops its containers, rebuilds them, loses the
/// scroll offset and drops the keyboard focus. Doing that on a timer means a list cannot be read while
/// it updates — the thing you were looking at jumps away mid-sentence. Restoring the selected item
/// afterwards, which this view model did, hides the bug from a test while leaving it fully present for
/// a person, because selection is not what scrolling follows.</para>
///
/// <para>So each row is matched by key and left <b>identically alone</b> when it has not changed — and
/// because the rows are records, "has not changed" is just <c>Equals</c>, structurally, for free. An
/// unchanged row keeps its container, so a queue refreshing under a reader is invisible; a row whose
/// state moved raises a Replace for that one index; genuinely new rows are inserted at their place.
/// Append — the common case for a timeline — therefore costs exactly one Add at the end and disturbs
/// nothing above it.</para>
/// </summary>
internal static class Reconcile
{
    /// <summary>
    /// Edits <paramref name="target"/> into <paramref name="desired"/>, in order, touching only what differs.
    /// </summary>
    /// <param name="key">Stable identity of a row — NOT its value. Two records with the same key and
    /// different contents are the same row in a new state, and are replaced in place rather than
    /// removed and re-added.</param>
    public static void Apply<T, TKey>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired,
        Func<T, TKey> key)
        where TKey : notnull
    {
        // Walk both sequences together. At each index the target either already holds the row that
        // belongs there, holds it further down (so the intervening rows are gone), or does not hold it
        // at all (so it is new).
        var wanted = new Dictionary<TKey, int>(desired.Count);
        for (var i = 0; i < desired.Count; i++)
        {
            // A duplicate key would make the walk below ambiguous; first occurrence wins, which matches
            // the order the caller asked for.
            wanted.TryAdd(key(desired[i]), i);
        }

        // Drop what is no longer wanted, back to front so the indices ahead of us stay valid.
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.ContainsKey(key(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            var wantKey = key(want);

            if (i < target.Count && Equals(key(target[i]), wantKey))
            {
                // Same row, same place. Replace only if its contents actually moved — for records that
                // is structural equality, so an unchanged row is left completely untouched and keeps
                // its container.
                if (!EqualityComparer<T>.Default.Equals(target[i], want))
                {
                    target[i] = want;
                }

                continue;
            }

            var existing = IndexOfKey(target, wantKey, key, from: i);
            if (existing >= 0)
            {
                // It is present but out of order: move it rather than remove-and-insert, so a reordering
                // sort does not destroy and rebuild every container it passes over.
                target.Move(existing, i);
                if (!EqualityComparer<T>.Default.Equals(target[i], want))
                {
                    target[i] = want;
                }

                continue;
            }

            target.Insert(i, want);
        }

        // Anything past the desired length is surplus (the loop above only ever grew to desired.Count).
        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static int IndexOfKey<T, TKey>(ObservableCollection<T> target, TKey wanted, Func<T, TKey> key, int from)
        where TKey : notnull
    {
        for (var i = from; i < target.Count; i++)
        {
            if (Equals(key(target[i]), wanted))
            {
                return i;
            }
        }

        return -1;
    }
}
