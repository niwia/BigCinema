// A copy of the state-message serialization from Net/Protocol.cs, with the parts the queue
// does not touch left out.
//
// Trimmed: the message id, the protocol version byte and the kind (MirrorChannel writes
// those), and the screen fields (HasScreen, ScreenPosition, ScreenYaw, GuestsCanControl,
// ScreenClearance, ScreenWidth). Everything else is identical - the field ORDER in particular,
// because that is the part that has to agree between a v3 host and a v3 client.
//
// The queue still writes count then index then every entry's url+title, and Mathf.Min still
// clamps the count at QueueLimit: a queue that grew past the limit must not push garbage
// into the messages that follow it.
using System.Collections.Generic;

namespace BigScreen.Tests;

internal static class StateWire
{
    public static void WriteState(NetworkWriter w, SyncState s)
    {
        WriteInt(w, s.Revision);
        WriteString(w, s.VideoUrl ?? "");
        WriteString(w, s.Title ?? "");
        WriteBool(w, s.Playing);
        WriteDouble(w, s.AnchorNetTime);
        WriteDouble(w, s.AnchorVideoTime);
        WriteQueue(w, s);
        WriteBool(w, s.AutoAdvance);
    }

    private static void WriteQueue(NetworkWriter w, SyncState s)
    {
        var queue = s.Queue;
        int count = queue == null ? 0 : Mathf.Min(queue.Count, QueueOps.QueueLimit);
        WriteInt(w, count);
        WriteInt(w, s.QueueIndex);
        for (int i = 0; i < count; i++)
        {
            var item = queue[i];
            WriteString(w, item?.Url ?? "");
            WriteString(w, item?.Title ?? "");
        }
    }

    public static SyncState ReadState(NetworkReader r)
    {
        var s = new SyncState();
        s.Revision = ReadInt(r);
        s.VideoUrl = ReadString(r);
        s.Title = ReadString(r);
        s.Playing = ReadBool(r);
        s.AnchorNetTime = ReadDouble(r);
        s.AnchorVideoTime = ReadDouble(r);
        ReadQueue(r, s);
        s.AutoAdvance = ReadBool(r);
        return s;
    }

    private static void ReadQueue(NetworkReader r, SyncState s)
    {
        int count = ReadInt(r);
        s.QueueIndex = ReadInt(r);
        s.Queue = new List<QueueEntry>();
        for (int i = 0; i < count; i++)
        {
            var item = new QueueEntry
            {
                Url = ReadString(r) ?? "",
                Title = ReadString(r) ?? ""
            };
            s.Queue.Add(item);
        }
    }

    // Local wrappers so the two blocks above read like the protocol they are copied from,
    // which calls the writer/reader statically as NetworkWriterExtensions.WriteInt(w, ...).
    private static void WriteInt(NetworkWriter w, int v) => w.WriteInt(v);

    private static void WriteString(NetworkWriter w, string v) => w.WriteString(v);

    private static void WriteBool(NetworkWriter w, bool v) => w.WriteBool(v);

    private static void WriteDouble(NetworkWriter w, double v) => w.WriteDouble(v);

    private static int ReadInt(NetworkReader r) => r.ReadInt();

    private static string ReadString(NetworkReader r) => r.ReadString();

    private static bool ReadBool(NetworkReader r) => r.ReadBool();

    private static double ReadDouble(NetworkReader r) => r.ReadDouble();
}
