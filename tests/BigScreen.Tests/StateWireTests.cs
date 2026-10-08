// State message round-trips.
//
// A queue edit is only useful if it reaches the other players, and the whole queue rides in
// every state message. These tests pin the field order: if WriteState and ReadState ever
// disagree about where the queue starts, a guest joining mid-film reads a queue of nonsense
// instead of the next few films - and nothing about that shows up until someone clicks.

namespace BigScreen.Tests;

internal static class StateWireTests
{
    private static SyncState ReadBack(SyncState s)
    {
        var w = new NetworkWriter();
        StateWire.WriteState(w, s);
        var r = new NetworkReader(w.ToArray());
        return StateWire.ReadState(r);
    }

    public static void StateRoundTrips()
    {
        var s = new SyncState
        {
            Revision = 42,
            VideoUrl = "https://youtu.be/first",
            Title = "First film",
            Playing = true,
            AnchorNetTime = 100.25,
            AnchorVideoTime = 37.5,
            QueueIndex = 1,
            AutoAdvance = false,
        };
        QueueOps.QueueAdd(s, "https://youtu.be/first", "First film");
        QueueOps.QueueAdd(s, "https://youtu.be/second", "");   // title not resolved yet
        QueueOps.QueueAdd(s, "https://youtu.be/third", "Third");

        Check.SameState(s, ReadBack(s), "a populated state survives the wire");
    }

    public static void StateRoundTripsAnEmptyQueue()
    {
        var s = new SyncState { Revision = 7 };

        Check.SameState(s, ReadBack(s), "an untouched state survives the wire, index -1 and all");
    }

    public static void StateRoundTripsOnlyUpToTheQueueLimit()
    {
        var s = new SyncState();
        for (int i = 0; i < QueueOps.QueueLimit + 1; i++)
            QueueOps.QueueAdd(s, $"https://youtu.be/{i:00}", $"item {i}");
        s.QueueIndex = 5;

        var back = ReadBack(s);

        // Mathf.Min on the way out, not a truncation on the way in: any extra items would
        // shift every byte that follows the queue and desynchronise the two ends.
        Check.Equal(QueueOps.QueueLimit, back.Queue.Count, "the writer clamps at the queue limit");
        Check.Equal("https://youtu.be/31", back.Queue[31].Url, "and it is the front 32 that survive");
    }
}
