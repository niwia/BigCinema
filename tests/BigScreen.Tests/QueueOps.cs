// A copy of the host-side queue mutations that live in BigScreenController.ApplyOnHost, plus
// the two decisions taken outside it (the auto-advance guard in the "ended" branch, and
// YtDlp.LooksLikeUrl, which QueueAdd relies on). They are copied instead of linked because
// the originals sit in a file full of VideoPlayer and Mirror calls; this way the index rules
// are pinned by a runnable test without first untangling the controller.
//
// Keep this in step with BigScreenController.cs: if a rule changes there, change it here (and
// the test) in the same commit.
using System;

namespace BigScreen.Tests;

internal static class QueueOps
{
    /// <summary>
    /// How many items one queue may hold. The whole queue is written into every state message,
    /// and past this the heartbeat starts getting expensive for the other players.
    /// </summary>
    public const int QueueLimit = 32;

    /// <summary>Copy of YtDlp.LooksLikeUrl - the only part of that file with no I/O in it.</summary>
    public static bool LooksLikeUrl(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Appends to the shared queue. An empty title is left empty on purpose: the title only
    /// exists after something resolves the url, and re-resolving to fill in a label would mean
    /// the host hitting the network because someone queued a link.
    ///
    /// Returns the reason an add was refused (what the caller shows in LastError), or null
    /// when it was accepted.
    /// </summary>
    public static string QueueAdd(SyncState state, string url, string title)
    {
        url = (url ?? "").Trim();
        if (!LooksLikeUrl(url)) return "Enter a full URL starting with https://";
        if (state.Queue.Count >= QueueLimit) return $"The queue is full ({QueueLimit} items).";
        state.Queue.Add(new QueueEntry { Url = url, Title = title ?? "" });
        return null;
    }

    /// <summary>
    /// Removes a queue entry and decides what becomes the current one.
    ///
    /// Removing something already watched shifts the index down by one; removing something
    /// further down the queue leaves it alone (it is ahead of the film, not behind it);
    /// removing the item that is playing must not stop the film, so the index moves to
    /// whatever now occupies that slot - which is -1 only when the queue is now empty.
    /// Out of range indices are ignored, because a guest's index can refer to a queue the
    /// host has edited since.
    /// </summary>
    public static void QueueRemove(SyncState state, int index)
    {
        if (index < 0 || index >= state.Queue.Count) return;

        bool removingCurrent = index == state.QueueIndex;
        int newIndex = state.QueueIndex;
        if (index < state.QueueIndex) newIndex--;
        else if (removingCurrent) newIndex = Mathf.Min(index, state.Queue.Count - 2);

        // Order matters: the clamp above reads the count BEFORE the item goes away.
        state.Queue.RemoveAt(index);
        state.QueueIndex = newIndex;
    }

    public static void QueueClear(SyncState state)
    {
        state.Queue.Clear();
        state.QueueIndex = -1;
    }

    /// <summary>Starts a queue entry from the top, paused, like any freshly loaded video.</summary>
    public static void QueuePlayIndex(SyncState state, int index, double now)
    {
        if (index < 0 || index >= state.Queue.Count) return;
        string url = state.Queue[index].Url;
        state.QueueIndex = index;
        state.VideoUrl = url;
        state.Title = "";
        state.Playing = false;
        state.AnchorVideoTime = 0;
        state.AnchorNetTime = now;
    }

    /// <summary>Skips to another queue entry, clamping at both ends.</summary>
    public static void QueueStep(SyncState state, int delta, double now)
    {
        if (state.Queue.Count == 0) return;
        int next = Mathf.Clamp(state.QueueIndex + delta, 0, state.Queue.Count - 1);
        if (next == state.QueueIndex) return;
        QueuePlayIndex(state, next, now);
    }

    /// <summary>
    /// Which queue entry a film that just ended should roll into, or -1 when there is nothing
    /// to roll into. Mirrors the guard in the "ended" branch of the controller
    /// (<c>QueueIndex + 1 &lt; Queue.Count</c>): with nothing playing (-1) and a non-empty
    /// queue it lands on the first item, which is what picks up a queue filled while idle.
    /// </summary>
    public static int AutoAdvanceIndex(SyncState state)
    {
        if (state.Queue.Count == 0) return -1;
        int next = state.QueueIndex + 1;
        return next >= state.Queue.Count ? -1 : next;
    }
}
