// Host-side queue rules.
//
// These are copied decisions from BigScreenController.ApplyOnHost and its "ended" branch:
// which item becomes current when one is removed, how a skip clamps at the ends, and what
// auto-advance does when a film runs out. The UI only ever sends a request, so the index
// arithmetic lives in exactly one place - and that place is the thing to pin down.

namespace BigScreen.Tests;

internal static class QueueOpsTests
{
    /// <summary>Any fixed "network time" will do: the anchors are compared, never interpreted.</summary>
    private const double Now = 1234.5;

    // A queue that is playing, so index tests start from a realistic state.
    private static SyncState Playing(params string[] urls)
    {
        var s = new SyncState();
        foreach (string url in urls) s.Queue.Add(new QueueEntry { Url = url, Title = "" });
        s.QueueIndex = urls.Length > 0 ? 0 : -1;
        s.VideoUrl = urls.Length > 0 ? urls[0] : "";
        s.Playing = urls.Length > 0;
        return s;
    }

    public static void AddAppendsTheItem()
    {
        var s = new SyncState();

        Check.Null(QueueOps.QueueAdd(s, "https://youtu.be/a", ""), "a url is accepted");
        Check.Equal("https://youtu.be/a", s.Queue[0].Url, "the url is stored");
        Check.Equal("", s.Queue[0].Title, "an unresolved title stays empty on purpose");
    }

    public static void AddRejectsANonUrl()
    {
        var s = new SyncState();

        // The host is the only one that talks to yt-dlp, so a bad url has to die here rather
        // than travel to every client as a queue row that can never play.
        Check.Equal("Enter a full URL starting with https://",
            QueueOps.QueueAdd(s, "youtu.be/a", "no scheme"),
            "a url without a scheme is refused");
    }

    public static void AddRefusesPastTheLimit()
    {
        var s = new SyncState();
        for (int i = 0; i < QueueOps.QueueLimit; i++) QueueOps.QueueAdd(s, $"https://youtu.be/{i:00}", "");

        Check.Equal($"The queue is full ({QueueOps.QueueLimit} items).",
            QueueOps.QueueAdd(s, "https://youtu.be/one-more", ""),
            "one past the limit is refused");
        Check.Equal(QueueOps.QueueLimit, s.Queue.Count, "and the queue is left alone");
    }

    public static void QueueRemoveMovesTheCurrentIndex()
    {
        // Before the current item: everything behind it slides up a slot, so the index must
        // follow the film down.
        var before = Playing("a", "b", "c");
        before.QueueIndex = 1;
        QueueOps.QueueRemove(before, 0);
        Check.Equal(0, before.QueueIndex, "removing an earlier item shifts the index down");

        // The current item: the film on screen is not interrupted, so the index moves to
        // whatever slid into the slot it used to occupy.
        var current = Playing("a", "b", "c");
        current.QueueIndex = 1;
        QueueOps.QueueRemove(current, 1);
        Check.Equal(1, current.QueueIndex, "removing the playing item keeps the slot current");
        Check.Equal("c", current.Queue[1].Url, "filled by the item that slid in");

        // After the current item: nothing ahead of the film moves.
        var after = Playing("a", "b", "c");
        QueueOps.QueueRemove(after, 2);
        Check.Equal(0, after.QueueIndex, "removing a later item leaves the index alone");
    }

    public static void RemoveTheOnlyItemLeavesNothingPlaying()
    {
        var s = Playing("a");

        QueueOps.QueueRemove(s, 0);

        // The clamp reads Count - 2 before the item goes away, so the index is -1 and not one
        // past the last item.
        Check.Equal(-1, s.QueueIndex, "an emptied queue leaves nothing current");
    }

    public static void RemoveOutOfRangeIsIgnored()
    {
        var s = Playing("a", "b", "c");
        s.QueueIndex = 1;

        QueueOps.QueueRemove(s, -1);
        QueueOps.QueueRemove(s, s.Queue.Count);

        // A guest's index can be stale by the time the host acts on it.
        Check.Equal(1, s.QueueIndex, "an out of range removal is a no-op");
    }

    public static void ClearEmptiesTheQueue()
    {
        var s = Playing("a", "b", "c");
        s.QueueIndex = 2;

        QueueOps.QueueClear(s);

        Check.Equal(-1, s.QueueIndex, "clearing resets the index");
    }

    public static void PlayIndexLoadsTheEntryPaused()
    {
        var s = Playing("a", "b", "c");
        s.Playing = true;
        s.AnchorVideoTime = 90;

        QueueOps.QueuePlayIndex(s, 2, Now);

        Check.Equal(2, s.QueueIndex, "the entry becomes current");
        Check.Equal("c", s.VideoUrl, "it is what is loaded");
        Check.False(s.Playing, "and it starts paused, like any freshly loaded video");
    }

    public static void StepClampsAtBothEnds()
    {
        var front = Playing("a", "b", "c");
        QueueOps.QueueStep(front, -1, Now);
        Check.Equal(0, front.QueueIndex, "stepping back off the front stays there");

        var end = Playing("a", "b", "c");
        QueueOps.QueueStep(end, +1, Now);
        QueueOps.QueueStep(end, +1, Now);
        QueueOps.QueueStep(end, +1, Now);
        Check.Equal(2, end.QueueIndex, "stepping on past the end stays there");
    }

    public static void StepMovesToTheNextItem()
    {
        var s = Playing("a", "b", "c");

        QueueOps.QueueStep(s, +1, Now);

        Check.Equal(1, s.QueueIndex, "a step lands on the next entry");
    }

    public static void AutoAdvancePicksTheNextEntry()
    {
        Check.Equal(1, QueueOps.AutoAdvanceIndex(Playing("a", "b", "c")),
            "mid-queue, the next item rolls in");
        Check.Equal(-1, QueueOps.AutoAdvanceIndex(Ended("a", "b", "c")),
            "at the end, there is nothing to roll into");
        Check.Equal(-1, QueueOps.AutoAdvanceIndex(new SyncState()),
            "an empty queue, likewise");
        // QueueIndex is -1 while nothing plays, so a queue that was filled while idle invites
        // the host to start at the front rather than reporting "Ended." over a list nobody
        // has watched yet.
        var idle = new SyncState();
        QueueOps.QueueAdd(idle, "https://youtu.be/a", "");
        QueueOps.QueueAdd(idle, "https://youtu.be/b", "");
        Check.Equal(0, QueueOps.AutoAdvanceIndex(idle),
            "nothing playing yet, the front of the queue is next");
    }

    private static SyncState Ended(params string[] urls)
    {
        var s = Playing(urls);
        s.QueueIndex = urls.Length - 1;
        return s;
    }
}
