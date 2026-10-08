using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace BigScreen.Net;

/// <summary>
/// Session logic on top of <see cref="MirrorChannel"/>.
///
/// Host: owns the authoritative <see cref="State"/>, tracks which connections have the mod
/// (they said Hello), pushes state on every change and as a slow heartbeat.
/// Client: says Hello once ready, then mirrors whatever state arrives.
///
/// The host is also a client of itself (Mirror host mode), but it never sends itself
/// messages: it applies its own state directly.
/// </summary>
internal sealed class SyncSession
{
    public SyncState State { get; private set; } = new SyncState();

    /// <summary>Raised whenever State changes, on host (local edits) and client (received).</summary>
    public event Action<SyncState> StateChanged;

    /// <summary>Host only: a guest asked for something. Return true to apply.</summary>
    public event Func<Request, NetworkConnectionToClient, bool> RequestReceived;

    private readonly Dictionary<int, NetworkConnectionToClient> _moddedPeers = new();
    private bool _helloSent;
    private float _nextHeartbeat;
    private float _nextRegisterCheck;
    private bool _subscribed;

    private const float HeartbeatSeconds = 5f;

    public bool IsHost => NetworkServer.active;
    public bool IsConnectedClient => !NetworkServer.active && NetworkClient.isConnected;
    public int ModdedPeerCount => _moddedPeers.Count;
    public bool HelloSent => _helloSent;

    public void Tick()
    {
        if (!_subscribed)
        {
            MirrorChannel.ServerMessage += OnServerMessage;
            MirrorChannel.ClientMessage += OnClientMessage;
            _subscribed = true;
        }

        bool inSession = NetworkServer.active || NetworkClient.active;
        if (!inSession) return;

        if (Time.unscaledTime >= _nextRegisterCheck)
        {
            _nextRegisterCheck = Time.unscaledTime + 1f;
            MirrorChannel.EnsureRegistered();
        }

        if (IsHost)
        {
            if (State.GuestsCanControl != Plugin.GuestsCanControl.Value)
            {
                State.GuestsCanControl = Plugin.GuestsCanControl.Value;
                Commit();
            }
            // Same for auto-advance: every player sees the queue, so they should also see
            // whether it will keep rolling without them.
            if (State.AutoAdvance != (Plugin.AutoAdvance != null && Plugin.AutoAdvance.Value))
            {
                State.AutoAdvance = Plugin.AutoAdvance != null && Plugin.AutoAdvance.Value;
                Commit();
            }
            // The host's screen geometry is the screen everyone sees, so republish it when
            // the host changes it - raising the screen moves it for the whole lobby.
            if (!Mathf.Approximately(State.ScreenClearance, Plugin.ScreenGroundClearance.Value) ||
                !Mathf.Approximately(State.ScreenWidth, Plugin.ScreenWidthMeters.Value))
            {
                State.ScreenClearance = Plugin.ScreenGroundClearance.Value;
                State.ScreenWidth = Plugin.ScreenWidthMeters.Value;
                Commit();
            }
            if (Time.unscaledTime >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.unscaledTime + HeartbeatSeconds;
                PrunePeers();
                Broadcast();
            }
        }
        else if (NetworkClient.isConnected && NetworkClient.ready && !_helloSent)
        {
            // Hello once we are "ready" (scene loaded, player spawned) so the host's reply
            // lands in a client that can act on it.
            if (MirrorChannel.SendToServer(w => Protocol.WriteHello(w, Plugin.Version)))
            {
                _helloSent = true;
                Plugin.Log.LogInfo("Sent Hello to host.");
            }
        }
    }

    public void Reset()
    {
        _moddedPeers.Clear();
        _helloSent = false;
        _nextHeartbeat = 0f;
        State = new SyncState();
        MirrorChannel.Reset();
    }

    // --- Host-side mutations ----------------------------------------------------------

    /// <summary>Host: apply a mutation to the state, bump revision, notify + broadcast.</summary>
    public void Mutate(Action<SyncState> edit)
    {
        if (!IsHost) return;
        edit(State);
        Commit();
    }

    private void Commit()
    {
        State.Revision++;
        StateChanged?.Invoke(State);
        Broadcast();
    }

    /// <summary>Client: ask the host to do something (only honoured when GuestsCanControl).</summary>
    public bool SendRequest(Request req)
    {
        if (IsHost) return false;
        return MirrorChannel.SendToServer(w => Protocol.WriteRequest(w, req));
    }

    private void Broadcast()
    {
        if (!IsHost || _moddedPeers.Count == 0) return;
        var dead = new List<int>();
        foreach (var kv in _moddedPeers)
        {
            if (!MirrorChannel.SendTo(kv.Value, w => Protocol.WriteState(w, State)))
                dead.Add(kv.Key);
        }
        foreach (var id in dead) _moddedPeers.Remove(id);
    }

    private void PrunePeers()
    {
        try
        {
            var live = NetworkServer.connections;
            if (live == null) return;
            var dead = new List<int>();
            foreach (var id in _moddedPeers.Keys)
                if (!live.ContainsKey(id)) dead.Add(id);
            foreach (var id in dead)
            {
                _moddedPeers.Remove(id);
                Plugin.Log.LogInfo($"Modded peer {id} left.");
            }
        }
        catch (Exception e) { Plugin.Log.LogDebug($"PrunePeers: {e.Message}"); }
    }

    // --- Message handlers ---------------------------------------------------------------

    private void OnServerMessage(NetworkConnectionToClient conn, NetworkReader reader)
    {
        if (!Protocol.ReadHeader(reader, out var kind))
        {
            Plugin.Log.LogWarning("Dropped a BigScreen message with an unknown protocol version (mismatched mod versions?).");
            return;
        }

        switch (kind)
        {
            case Protocol.Kind.Hello:
            {
                string ver = Protocol.ReadHello(reader);
                if (conn == null)
                {
                    Plugin.Log.LogWarning($"Hello from a BigScreen v{ver} peer had no usable connection; " +
                                          "cannot register them, so they will never receive state.");
                    return;
                }
                _moddedPeers[conn.connectionId] = conn;
                Plugin.Log.LogInfo($"Peer {conn.connectionId} has BigScreen v{ver}. Modded peers: {_moddedPeers.Count}.");
                MirrorChannel.SendTo(conn, w => Protocol.WriteState(w, State));
                break;
            }
            case Protocol.Kind.Request:
            {
                var req = Protocol.ReadRequest(reader);
                if (!State.GuestsCanControl)
                {
                    Plugin.Log.LogInfo($"Ignored {req.Action} request from peer {conn?.connectionId}: GuestsCanControl is off.");
                    return;
                }
                RequestReceived?.Invoke(req, conn);
                break;
            }
            default:
                Plugin.Log.LogDebug($"Server ignored message kind {kind}.");
                break;
        }
    }

    private void OnClientMessage(NetworkReader reader)
    {
        if (IsHost) return; // host applies its own state directly

        if (!Protocol.ReadHeader(reader, out var kind))
        {
            Plugin.Log.LogWarning("Dropped a BigScreen message with an unknown protocol version (host has a different mod version?).");
            return;
        }

        if (kind != Protocol.Kind.State) return;

        var incoming = Protocol.ReadState(reader);
        if (incoming.Revision < State.Revision) return; // stale
        bool changed = incoming.Revision != State.Revision;
        State = incoming;
        if (changed) StateChanged?.Invoke(State);
    }
}
