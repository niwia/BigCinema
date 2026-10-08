// A copy of the queue half of Net/Protocol.cs's SyncState, trimmed to the fields the shared
// queue travels with. The real class also carries HasScreen / ScreenPosition / ScreenYaw /
// GuestsCanControl / ScreenClearance / ScreenWidth; those hold no queue logic, so leaving
// them out keeps this file honest about what it covers.
using System.Collections.Generic;

namespace BigScreen.Tests;

/// <summary>One queued item: where it streams from, and the title we already resolved.</summary>
internal sealed class QueueEntry
{
    public string Url = "";
    public string Title = "";
}

/// <summary>
/// The whole shared state of the screen, as far as the queue is concerned. The host owns it;
/// clients only ever receive it - which is why the host-side edits in <see cref="QueueOps"/>
/// are the ones worth testing.
/// </summary>
internal sealed class SyncState
{
    public int Revision;
    public string VideoUrl = "";
    public string Title = "";
    public bool Playing;
    public double AnchorNetTime;
    public double AnchorVideoTime;

    /// <summary>
    /// The shared "up next" list, owned by the host like everything else here. The whole list
    /// travels with every state message so a guest joining mid-film sees what is queued
    /// without asking for it.
    /// </summary>
    public List<QueueEntry> Queue = new List<QueueEntry>();

    /// <summary>Index into <see cref="Queue"/> of the item now playing, or -1 when empty.</summary>
    public int QueueIndex = -1;

    /// <summary>Host: roll straight into the next queued item when this one ends.</summary>
    public bool AutoAdvance = true;

    public SyncState Clone()
    {
        var copy = (SyncState)MemberwiseClone();
        // The list is a reference: a shallow clone would share it with the original, and
        // the first undo of a queue edit would silently rewrite the live queue. The entries
        // themselves are shared on purpose - they are immutable in practice (a new
        // QueueEntry replaces an old one, nobody mutates one).
        copy.Queue = Queue == null ? new List<QueueEntry>() : new List<QueueEntry>(Queue);
        return copy;
    }
}
