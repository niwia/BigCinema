// Why SyncState.Clone deep-copies the queue.
//
// A clone of the state that shared its List with the live one would make the first "undo"
// of a queue edit rewrite the queue every other player is watching. The list being new is the
// whole point of the method, so it is what the test checks.

namespace BigScreen.Tests;

internal static class CloneTests
{
    public static void CloneGetsAFreshList()
    {
        var s = new SyncState();
        QueueOps.QueueAdd(s, "https://youtu.be/a", "A");
        QueueOps.QueueAdd(s, "https://youtu.be/b", "B");

        var copy = s.Clone();

        Check.NotSame(s.Queue, copy.Queue, "the clone has its own queue");
        QueueOps.QueueRemove(copy, 0);
        Check.Equal(2, s.Queue.Count, "an edit to the clone leaves the original alone");
    }
}
