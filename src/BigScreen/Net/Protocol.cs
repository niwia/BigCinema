using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace BigScreen.Net;

/// <summary>
/// Wire format for BigScreen messages. Every message starts with our Mirror message id
/// (written by <see cref="MirrorChannel"/>), then a protocol version byte, then a kind.
///
/// Serialization uses Mirror's own NetworkWriter/NetworkReader primitives so we never
/// hand-roll byte packing and stay compatible with Mirror's batching and MTU handling.
/// The extension classes are called statically because the IL2CPP interop proxies do not
/// always expose them as C# extension methods.
/// </summary>
internal static class Protocol
{
    // 2: added ScreenClearance / ScreenWidth. A v1 peer is rejected with a clear
    //    warning rather than misreading the trailing bytes.
    // 3: added the shared queue (a list of upcoming items, plus the index of the one
    //    playing) and the auto-advance flag. A v2 peer is rejected the same way: the
    //    state message gained trailing fields, so guessing would corrupt everything after.
    public const byte Version = 3;

    public enum Kind : byte
    {
        Hello = 1,     // client -> host: "I have the mod (version X)"
        State = 2,     // host -> clients: full authoritative state
        Request = 3,   // client -> host: please do X (only honoured if GuestsCanControl)
    }

    public enum Action : byte
    {
        Load = 1,
        Play = 2,
        Pause = 3,
        SeekTo = 4,
        Stop = 5,
        Place = 6,
        Remove = 7,
        QueueAdd = 8,        // req.Text = url, req.Alt = title ("" to resolve later)
        QueueRemove = 9,     // req.Value = index
        QueueClear = 10,
        QueuePlayIndex = 11, // req.Value = index: play that one now
        QueueNext = 12,
        QueuePrev = 13,
    }

    /// <summary>
    /// How many items one queue may hold. The whole queue is written into every state
    /// message, and Mirror's default channel has an MTU-sized buffer; past this the
    /// heartbeat would start getting expensive for the other players.
    /// </summary>
    public const int QueueLimit = 32;

    public static void WriteHeader(NetworkWriter w, Kind kind)
    {
        NetworkWriterExtensions.WriteByte(w, Version);
        NetworkWriterExtensions.WriteByte(w, (byte)kind);
    }

    /// <summary>Returns false if the message is from an incompatible protocol version.</summary>
    public static bool ReadHeader(NetworkReader r, out Kind kind)
    {
        kind = 0;
        byte version = NetworkReaderExtensions.ReadByte(r);
        if (version != Version) return false;
        kind = (Kind)NetworkReaderExtensions.ReadByte(r);
        return true;
    }

    public static void WriteHello(NetworkWriter w, string modVersion)
    {
        WriteHeader(w, Kind.Hello);
        NetworkWriterExtensions.WriteString(w, modVersion ?? "");
    }

    public static string ReadHello(NetworkReader r) => NetworkReaderExtensions.ReadString(r);

    public static void WriteState(NetworkWriter w, SyncState s)
    {
        WriteHeader(w, Kind.State);
        NetworkWriterExtensions.WriteInt(w, s.Revision);
        NetworkWriterExtensions.WriteBool(w, s.HasScreen);
        NetworkWriterExtensions.WriteVector3(w, s.ScreenPosition);
        NetworkWriterExtensions.WriteFloat(w, s.ScreenYaw);
        NetworkWriterExtensions.WriteString(w, s.VideoUrl ?? "");
        NetworkWriterExtensions.WriteString(w, s.Title ?? "");
        NetworkWriterExtensions.WriteBool(w, s.Playing);
        NetworkWriterExtensions.WriteDouble(w, s.AnchorNetTime);
        NetworkWriterExtensions.WriteDouble(w, s.AnchorVideoTime);
        NetworkWriterExtensions.WriteBool(w, s.GuestsCanControl);
        NetworkWriterExtensions.WriteFloat(w, s.ScreenClearance);
        NetworkWriterExtensions.WriteFloat(w, s.ScreenWidth);
        WriteQueue(w, s);
        NetworkWriterExtensions.WriteBool(w, s.AutoAdvance);
    }

    private static void WriteQueue(NetworkWriter w, SyncState s)
    {
        var queue = s.Queue;
        int count = queue == null ? 0 : Mathf.Min(queue.Count, QueueLimit);
        NetworkWriterExtensions.WriteInt(w, count);
        NetworkWriterExtensions.WriteInt(w, s.QueueIndex);
        for (int i = 0; i < count; i++)
        {
            var item = queue[i];
            NetworkWriterExtensions.WriteString(w, item?.Url ?? "");
            NetworkWriterExtensions.WriteString(w, item?.Title ?? "");
        }
    }

    private static void ReadQueue(NetworkReader r, SyncState s)
    {
        int count = NetworkReaderExtensions.ReadInt(r);
        s.QueueIndex = NetworkReaderExtensions.ReadInt(r);
        s.Queue = new List<QueueEntry>();
        for (int i = 0; i < count; i++)
        {
            var item = new QueueEntry
            {
                Url = NetworkReaderExtensions.ReadString(r) ?? "",
                Title = NetworkReaderExtensions.ReadString(r) ?? ""
            };
            s.Queue.Add(item);
        }
    }

    public static SyncState ReadState(NetworkReader r)
    {
        var s = new SyncState();
        s.Revision = NetworkReaderExtensions.ReadInt(r);
        s.HasScreen = NetworkReaderExtensions.ReadBool(r);
        s.ScreenPosition = NetworkReaderExtensions.ReadVector3(r);
        s.ScreenYaw = NetworkReaderExtensions.ReadFloat(r);
        s.VideoUrl = NetworkReaderExtensions.ReadString(r);
        s.Title = NetworkReaderExtensions.ReadString(r);
        s.Playing = NetworkReaderExtensions.ReadBool(r);
        s.AnchorNetTime = NetworkReaderExtensions.ReadDouble(r);
        s.AnchorVideoTime = NetworkReaderExtensions.ReadDouble(r);
        s.GuestsCanControl = NetworkReaderExtensions.ReadBool(r);
        s.ScreenClearance = NetworkReaderExtensions.ReadFloat(r);
        s.ScreenWidth = NetworkReaderExtensions.ReadFloat(r);
        ReadQueue(r, s);
        s.AutoAdvance = NetworkReaderExtensions.ReadBool(r);
        return s;
    }

    public static void WriteRequest(NetworkWriter w, Request req)
    {
        WriteHeader(w, Kind.Request);
        NetworkWriterExtensions.WriteByte(w, (byte)req.Action);
        NetworkWriterExtensions.WriteString(w, req.Text ?? "");
        NetworkWriterExtensions.WriteString(w, req.Alt ?? "");
        NetworkWriterExtensions.WriteDouble(w, req.Value);
        NetworkWriterExtensions.WriteVector3(w, req.Position);
        NetworkWriterExtensions.WriteFloat(w, req.Yaw);
    }

    public static Request ReadRequest(NetworkReader r)
    {
        var req = new Request();
        req.Action = (Action)NetworkReaderExtensions.ReadByte(r);
        req.Text = NetworkReaderExtensions.ReadString(r);
        req.Alt = NetworkReaderExtensions.ReadString(r);
        req.Value = NetworkReaderExtensions.ReadDouble(r);
        req.Position = NetworkReaderExtensions.ReadVector3(r);
        req.Yaw = NetworkReaderExtensions.ReadFloat(r);
        return req;
    }
}

/// <summary>
/// The whole shared state of the screen. The host owns it; clients only ever receive it.
///
/// Playback position is expressed as an anchor: "at network time <see cref="AnchorNetTime"/>
/// the video was at <see cref="AnchorVideoTime"/> and (if <see cref="Playing"/>) advancing at
/// 1x". Everyone can then compute the expected position at any moment from Mirror's shared
/// clock (NetworkTime.time) without the host streaming positions every frame.
/// </summary>
internal sealed class SyncState
{
    public int Revision;
    public bool HasScreen;
    public Vector3 ScreenPosition;
    public float ScreenYaw;
    public string VideoUrl = "";
    public string Title = "";
    public bool Playing;
    public double AnchorNetTime;
    public double AnchorVideoTime;
    public bool GuestsCanControl;

    /// <summary>
    /// Screen geometry, owned by the host. Each player keeps their own values in config for
    /// when THEY host; in someone else's game the host's numbers win, so everyone is looking
    /// at the same screen in the same place. Defaults match the config defaults, for the
    /// window before the first state arrives.
    /// </summary>
    public float ScreenClearance = 0.6f;
    public float ScreenWidth = 4f;

    /// <summary>
    /// The shared "up next" list, owned by the host like everything else here. The whole
    /// list travels with every state message so a guest joining mid-film sees what is
    /// queued without asking for it.
    /// </summary>
    public List<QueueEntry> Queue = new List<QueueEntry>();

    /// <summary>Index into <see cref="Queue"/> of the item now playing, or -1 when empty.</summary>
    public int QueueIndex = -1;

    /// <summary>Host: roll straight into the next queued item when this one ends.</summary>
    public bool AutoAdvance = true;

    public double ExpectedVideoTime(double netTimeNow)
    {
        if (!Playing) return AnchorVideoTime;
        return AnchorVideoTime + (netTimeNow - AnchorNetTime);
    }

    public SyncState Clone()
    {
        var copy = (SyncState)MemberwiseClone();
        // The list is a reference: a shallow clone would share it with the original, and
        // the first "undo" of a queue edit would silently rewrite the live queue.
        copy.Queue = Queue == null ? new List<QueueEntry>() : new List<QueueEntry>(Queue);
        return copy;
    }
}

/// <summary>One queued item: where it streams from, and the title we already resolved.</summary>
internal sealed class QueueEntry
{
    public string Url = "";
    public string Title = "";
}

internal struct Request
{
    public Protocol.Action Action;
    public string Text;

    /// <summary>
    /// A second string for actions that need two (a queue item carries its url *and* the
    /// title we already know, so nobody has to resolve it twice to display it).
    /// </summary>
    public string Alt;
    public double Value;
    public Vector3 Position;
    public float Yaw;
}
