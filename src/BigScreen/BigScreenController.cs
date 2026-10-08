using System;
using System.Threading;
using System.Threading.Tasks;
using BigScreen.Net;
using BigScreen.Resolve;
using BigScreen.UI;
using BigScreen.Util;
using BigScreen.Video;
using BigScreen.World;
using Mirror;
using UnityEngine;

namespace BigScreen;

/// <summary>
/// The one MonoBehaviour. Runs every frame for the life of the game and:
///   - drives the <see cref="SyncSession"/> (host state / client follow),
///   - builds and tears down the in-world <see cref="ScreenObject"/>,
///   - resolves page URLs to stream URLs (yt-dlp, off-thread) and feeds the video backend,
///   - keeps local playback locked to the host's timeline (drift correction),
///   - draws the control panel.
///
/// Everyone, host included, runs the same "follow the state" logic; the host is simply
/// the only one allowed to change the state (and the one who forwards guest requests).
/// </summary>
public class BigScreenController : MonoBehaviour
{
    public BigScreenController(IntPtr ptr) : base(ptr) { }

    internal static BigScreenController Instance { get; private set; }

    internal SyncSession Session { get; } = new SyncSession();
    internal string StatusLine { get; private set; } = "Idle.";
    /// <summary>
    /// Last thing that went wrong, shown in the panel until something replaces it. The
    /// timestamp is for the console lamp, which should go back to inviting a paste rather
    /// than sitting red forever over a mistake you have already moved on from.
    /// </summary>
    internal string LastError
    {
        get => _lastError;
        private set { _lastError = value; _lastErrorAt = Time.unscaledTime; }
    }
    private string _lastError;
    private float _lastErrorAt = -1000f;
    internal double LastDrift { get; private set; }
    internal double LocalVideoTime => _video != null && _video.IsReady ? _video.Time : 0.0;
    internal double LocalVideoDuration => _video != null && _video.IsReady ? _video.Duration : 0.0;
    internal bool CanControl => Session.IsHost || (Session.IsConnectedClient && Session.State.GuestsCanControl);

    private ControlPanel _panel;
    private bool _panelVisible;
    private CursorLockMode _priorLock = CursorLockMode.Locked;
    private bool _priorCursorVisible;

    private ScreenObject _screen;
    private IVideoBackend _video;

    // What the local video backend currently has loaded, keyed by the page URL from state.
    private string _loadedPageUrl;
    private string _resolvingPageUrl;
    private CancellationTokenSource _resolveCts;
    private bool _pendingInitialSeek;
    private float _lastSeekAt = -100f;
    private float _nextDiag;
    private bool _wasInSession;
    private bool _configDirty;
    private Vector3 _lastAimOrigin;
    private float _nextConfigSave;

    // Writing the config file is main-thread disk I/O. Holding a nudge button would do it
    // every click, so changes are batched and written at most this often.
    private const float ConfigSaveIntervalSeconds = 3f;

    private const float MinSecondsBetweenSeeks = 2.5f;

    // How long the console's lamp stays red after a failure.
    private const float ConsoleErrorSeconds = 8f;

    private void Awake()
    {
        Instance = this;
        _panel = new ControlPanel(this);
        Session.StateChanged += OnStateChanged;
        Session.RequestReceived += OnGuestRequest;
    }

    private void Update()
    {
        MainThread.Drain();

        try
        {
            HandleInput();
            Session.Tick();
            Dev.AutoStart.Tick(this);

            bool inSession = NetworkServer.active || NetworkClient.active;
            if (_wasInSession && !inSession) ResetSession("left lobby");
            _wasInSession = inSession;

            if (inSession)
            {
                ReconcileScreen();
                ReconcileVideo();
                FollowTimeline();
                Vector3 screenPosition = _screen != null ? _screen.Root.transform.position : Vector3.zero;
                SleepGuard.Tick(screenPosition, _screen != null, Session.State.Playing);
                CrosshairGuard.Tick(screenPosition, _screen != null, Session.State.Playing);
            }

            FlushConfigIfDue();

            if (Plugin.Diagnostics.Value && Time.unscaledTime >= _nextDiag)
            {
                _nextDiag = Time.unscaledTime + 2f;
                Plugin.Log.LogInfo($"[diag] host={Session.IsHost} client={NetworkClient.isConnected} peers={Session.ModdedPeerCount} " +
                                   $"rev={Session.State.Revision} screen={(_screen != null)} url={Session.State.VideoUrl} " +
                                   $"playing={Session.State.Playing} ready={_video?.IsReady} t={LocalVideoTime:F1} drift={LastDrift:F2} " +
                                   $"nettime={SafeNetTime():F1} conv_failed={MirrorChannel.ConversionFailed} " +
                                   $"console={_screen?.Console?.DescribeAim(_lastAimOrigin) ?? "none"} " +
                                   $"awake={SleepGuard.Suppressing} xhair={(CrosshairGuard.Hidden ? "hidden" : "shown")} " +
                                   $"idle={CrosshairGuard.SecondsIdle:F0}/{CrosshairGuard.DelaySeconds:F0}s({CrosshairGuard.DelaySource})");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Update failed: {e}");
        }
    }

    /// <summary>
    /// Unity calls this from native code, so an exception must never leave it. Our type is
    /// injected into IL2CPP, and letting a managed exception unwind into native Unity frames
    /// takes the process down with a fatal access violation rather than a logged error.
    /// The same applies to Update; both swallow and log instead.
    /// </summary>
    private void OnGUI()
    {
        if (!_panelVisible) return;

        try
        {
            _panel.Draw();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Panel draw failed: {e}");
        }
    }

    // --- Input / panel ---------------------------------------------------------------

    private void HandleInput()
    {
        if (Input.GetKeyDown(Plugin.ToggleUiKey.Value)) SetPanelVisible(!_panelVisible);

        // The game re-locks the cursor every frame while playing; reassert while open.
        if (_panelVisible)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    internal void SetPanelVisible(bool visible)
    {
        if (visible == _panelVisible) return;
        _panelVisible = visible;
        if (visible)
        {
            _priorLock = Cursor.lockState;
            _priorCursorVisible = Cursor.visible;
        }
        else
        {
            Cursor.lockState = _priorLock;
            Cursor.visible = _priorCursorVisible;
        }
    }

    // --- User actions (host applies directly; guests send requests) -------------------

    internal void UserPlaceScreen()
    {
        if (!TryGetLocalPlayerPose(out var pos, out var fwd)) { LastError = "Could not find your character."; return; }
        // Logged so a spawn point can be picked from a spot you actually walked to.
        Plugin.Log.LogInfo($"Your position: ({pos.x:F2}, {pos.y:F2}, {pos.z:F2})  facing ({fwd.x:F2}, {fwd.y:F2}, {fwd.z:F2})");
        float yaw = Quaternion.LookRotation(new Vector3(fwd.x, 0f, fwd.z).normalized, Vector3.up).eulerAngles.y;
        PlaceScreenForViewer(pos, yaw);
    }

    /// <summary>
    /// Puts the screen 4 m in front of a viewer pose, facing back at it. The quad's picture
    /// faces local -Z, so the screen takes the viewer's own yaw and its +Z points away.
    ///
    /// Separate from <see cref="UserPlaceScreen"/> so an automated run can place from a
    /// recorded pose rather than from wherever the camera happens to point. The camera is
    /// not a reliable source at spawn: the game's teleport rotates the character body but
    /// leaves camera yaw alone, so a recorded yaw never reaches cameraTransform.forward.
    /// </summary>
    internal void PlaceScreenForViewer(Vector3 viewerPosition, float viewerYaw)
    {
        var fwd = Quaternion.Euler(0f, viewerYaw, 0f) * Vector3.forward;
        PlaceScreenAt(viewerPosition + fwd * 4f, viewerYaw);
    }

    /// <summary>Puts the screen at an exact world pose, rather than relative to a viewer.</summary>
    internal void PlaceScreenAt(Vector3 position, float yaw)
        => Apply(new Request { Action = Protocol.Action.Place, Position = position, Yaw = yaw });

    internal void UserRemoveScreen() => Apply(new Request { Action = Protocol.Action.Remove });

    /// <summary>
    /// Slides the screen along its own axes rather than the world's, so "left" means left
    /// as seen from where the screen was placed from, whatever its yaw.
    ///
    /// The screen takes the yaw of whoever placed it and its picture faces local -Z, so its
    /// local +X is that viewer's right and its local +Z points away from them.
    ///
    /// Unlike the height, position is shared state: this goes through the host the same way
    /// placing does, so a guest without control cannot move everyone's screen.
    /// </summary>
    internal void UserNudgeScreen(float rightMeters, float forwardMeters)
    {
        var s = Session.State;
        if (!s.HasScreen) { LastError = "There is no screen to move."; return; }

        var delta = Quaternion.Euler(0f, s.ScreenYaw, 0f) * new Vector3(rightMeters, 0f, forwardMeters);
        PlaceScreenAt(s.ScreenPosition + delta, s.ScreenYaw);
    }

    /// <summary>
    /// Raises or lowers the screen by a step, for everyone. Screen height is the host's, so
    /// this is a host-only control; the value is written back to config so the height
    /// survives a restart and applies to the next lobby this player hosts.
    /// </summary>
    internal void UserNudgeScreenHeight(float deltaMeters)
    {
        if (!Session.IsHost)
        {
            StatusLine = "The host sets the screen height.";
            return;
        }

        float height = Mathf.Clamp(Plugin.ScreenGroundClearance.Value + deltaMeters, -2f, 5f);
        Plugin.ScreenGroundClearance.Value = height;
        _configDirty = true;
        // ReconcileScreen re-applies the layout on the next frame.
        StatusLine = $"Screen height {height:F2} m.";
    }

    /// <summary>
    /// Writes where you are standing into Dev.SpawnPosition, so an unattended run starts
    /// from a spot you picked rather than wherever the game drops you.
    /// </summary>
    internal void UserSetSpawnHere()
    {
        if (!TryGetLocalPlayerPose(out var pos, out var fwd)) { LastError = "Could not find your character."; return; }

        // Record the direction as well as the spot. The screen is auto-placed relative to
        // where the player faces, so without a yaw it lands somewhere different every run.
        float yaw = Quaternion.LookRotation(fwd, Vector3.up).eulerAngles.y;
        var text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                 "{0:F2},{1:F2},{2:F2},{3:F1}", pos.x, pos.y, pos.z, yaw);
        Plugin.SpawnPosition.Value = text;
        _configDirty = true;

        StatusLine = "Spawn set to " + text;
        Plugin.Log.LogInfo("Spawn set to " + text);
    }

    internal void UserLoad(string pageUrl)
    {
        // Breadcrumbs: the game has died between this click and StartResolve without
        // logging anything, so each step writes a line. Logging is set to flush on every
        // write, so the last line in the log is the last step that completed.
        Util.Trace.Write($"UserLoad enter: {pageUrl}");
        pageUrl = (pageUrl ?? "").Trim();
        if (!YtDlp.LooksLikeUrl(pageUrl)) { LastError = "Enter a full URL starting with https://"; return; }
        LastError = null;
        if (!Session.State.HasScreen)
        {
            // Be helpful: loading with no screen places one in front of you first.
            UserPlaceScreen();
        }
        Util.Trace.Write("UserLoad: about to Apply");
        Apply(new Request { Action = Protocol.Action.Load, Text = pageUrl });
        Util.Trace.Write("UserLoad: Apply returned");
    }

    internal void UserTogglePlay() => Apply(new Request { Action = Session.State.Playing ? Protocol.Action.Pause : Protocol.Action.Play });
    internal void UserSeekRelative(double delta) => Apply(new Request { Action = Protocol.Action.SeekTo, Value = Session.State.ExpectedVideoTime(SafeNetTime()) + delta });
    // --- Shared queue (user side) --------------------------------------------------------

    internal void UserQueueAdd(string url, string title = "")
        => Apply(new Request { Action = Protocol.Action.QueueAdd, Text = url, Alt = title });

    internal void UserQueueRemove(int index)
        => Apply(new Request { Action = Protocol.Action.QueueRemove, Value = index });

    internal void UserQueueClear() => Apply(new Request { Action = Protocol.Action.QueueClear });

    internal void UserQueuePlay(int index)
        => Apply(new Request { Action = Protocol.Action.QueuePlayIndex, Value = index });

    internal void UserQueueStep(int delta)
        => Apply(new Request { Action = delta < 0 ? Protocol.Action.QueuePrev : Protocol.Action.QueueNext });
    internal void UserSeekTo(double seconds) => Apply(new Request { Action = Protocol.Action.SeekTo, Value = seconds });
    internal void UserStop() => Apply(new Request { Action = Protocol.Action.Stop });

    internal void UserUpdateYtDlp()
    {
        StatusLine = "Updating yt-dlp...";
        RunOffThread("BigScreen-ytdlp-update", () =>
        {
            var msg = YtDlp.Update(CancellationToken.None);
            MainThread.Post(() => { StatusLine = "yt-dlp: " + msg; Plugin.Log.LogInfo("yt-dlp update: " + msg); });
        });
    }

    internal void ApplyLocalAudioConfig()
    {
        _screen?.ApplyAudioConfig();
        _video?.SetVolume(Plugin.Volume.Value);
    }

    private void Apply(Request req)
    {
        if (Session.IsHost)
        {
            ApplyOnHost(req);
        }
        else if (Session.IsConnectedClient)
        {
            if (!Session.State.GuestsCanControl) { LastError = "The host has not enabled guest control."; return; }
            if (!Session.SendRequest(req)) LastError = "Could not send the request to the host.";
        }
    }

    /// <summary>Host: turn a request (local or from a guest) into a state mutation.</summary>
    private void ApplyOnHost(Request req)
    {
        double now = SafeNetTime();
        switch (req.Action)
        {
            case Protocol.Action.Place:
                Session.Mutate(s => { s.HasScreen = true; s.ScreenPosition = req.Position; s.ScreenYaw = req.Yaw; });
                RememberScreenPose(req.Position, req.Yaw);
                break;
            case Protocol.Action.Remove:
                Session.Mutate(s => { s.HasScreen = false; s.VideoUrl = ""; s.Title = ""; s.Playing = false; s.AnchorVideoTime = 0; });
                break;
            case Protocol.Action.Load:
                Session.Mutate(s =>
                {
                    s.VideoUrl = req.Text;
                    s.Title = "";
                    s.Playing = false;
                    s.AnchorVideoTime = 0;
                    s.AnchorNetTime = now;
                    // Playing an item that is already queued means auto-advance should carry on
                    // from there, so follow the queue to it.
                    int at = s.Queue.FindIndex(q => q.Url == (req.Text ?? ""));
                    s.QueueIndex = at;
                });
                break;
            case Protocol.Action.Play:
                if (string.IsNullOrEmpty(Session.State.VideoUrl)) return;
                Session.Mutate(s => { s.AnchorVideoTime = s.ExpectedVideoTime(now); s.AnchorNetTime = now; s.Playing = true; });
                break;
            case Protocol.Action.Pause:
                Session.Mutate(s => { s.AnchorVideoTime = s.ExpectedVideoTime(now); s.AnchorNetTime = now; s.Playing = false; });
                break;
            case Protocol.Action.SeekTo:
                Session.Mutate(s =>
                {
                    double target = Math.Max(0, req.Value);
                    double len = LocalVideoDuration;
                    if (len > 0 && target > len - 0.5) target = Math.Max(0, len - 0.5);
                    s.AnchorVideoTime = target;
                    s.AnchorNetTime = now;
                });
                break;
            case Protocol.Action.Stop:
                Session.Mutate(s => { s.VideoUrl = ""; s.Title = ""; s.Playing = false; s.AnchorVideoTime = 0; });
                break;
            case Protocol.Action.QueueAdd:
                QueueAddInternal(req.Text, req.Alt);
                break;
            case Protocol.Action.QueueRemove:
                QueueRemoveInternal((int)Math.Round(req.Value));
                break;
            case Protocol.Action.QueueClear:
                Session.Mutate(s => { s.Queue.Clear(); s.QueueIndex = -1; });
                break;
            case Protocol.Action.QueuePlayIndex:
                QueuePlayIndexInternal((int)Math.Round(req.Value), now);
                break;
            case Protocol.Action.QueueNext:
                QueueStep(1, now);
                break;
            case Protocol.Action.QueuePrev:
                QueueStep(-1, now);
                break;
        }
    }

    // --- Shared queue (host side) --------------------------------------------------------

    /// <summary>
    /// Appends to the shared queue. An empty title is left empty on purpose: the title only
    /// exists after something resolves the url, and re-resolving to fill in a label would
    /// mean the host hitting the network because someone queued a link. The panel shows the
    /// url until the resolve fills the title in.
    /// </summary>
    private void QueueAddInternal(string url, string title)
    {
        url = (url ?? "").Trim();
        if (!YtDlp.LooksLikeUrl(url)) { LastError = "Enter a full URL starting with https://"; return; }
        if (Session.State.Queue.Count >= Protocol.QueueLimit)
        {
            LastError = $"The queue is full ({Protocol.QueueLimit} items).";
            return;
        }
        Session.Mutate(s => s.Queue.Add(new Net.QueueEntry { Url = url, Title = title ?? "" }));
        StatusLine = $"Added to queue ({Session.State.Queue.Count} waiting).";
    }

    private void QueueRemoveInternal(int index)
    {
        var s = Session.State;
        if (index < 0 || index >= s.Queue.Count) return;

        bool removingCurrent = index == s.QueueIndex;
        // Removing the item that is playing must not stop the film: it stays on screen and
        // the index moves to whatever now occupies that slot.
        int newIndex = s.QueueIndex;
        if (index < s.QueueIndex) newIndex--;
        else if (removingCurrent) newIndex = Math.Min(index, s.Queue.Count - 2);

        Session.Mutate(st => { st.Queue.RemoveAt(index); st.QueueIndex = newIndex; });
    }

    private void QueuePlayIndexInternal(int index, double now)
    {
        var s = Session.State;
        if (index < 0 || index >= s.Queue.Count) return;
        string url = s.Queue[index].Url;
        Session.Mutate(st =>
        {
            st.QueueIndex = index;
            st.VideoUrl = url;
            st.Title = "";
            st.Playing = false;
            st.AnchorVideoTime = 0;
            st.AnchorNetTime = now;
        });
    }

    /// <summary>Skips to another queue entry, clamping at both ends.</summary>
    private void QueueStep(int delta, double now)
    {
        var s = Session.State;
        if (s.Queue.Count == 0) return;
        int next = Mathf.Clamp(s.QueueIndex + delta, 0, s.Queue.Count - 1);
        if (next == s.QueueIndex) return;
        QueuePlayIndexInternal(next, now);
    }

    /// <summary>
    /// Rolls into the next queued item when the current one ends. Host only: the queue is
    /// host-owned state, and a guest that decided when the night ends would fight the host.
    /// </summary>
    private void AutoAdvanceQueue(double now)
    {
        var s = Session.State;
        if (s.Queue.Count == 0) return;
        int next = s.QueueIndex + 1;
        if (next >= s.Queue.Count)
        {
            // End of the queue, not an edge case worth a special message: "Ended." is accurate.
            return;
        }
        string url = s.Queue[next].Url;
        Plugin.Log.LogInfo($"Queue: advancing to item {next} ({url}).");
        Session.Mutate(st =>
        {
            st.QueueIndex = next;
            st.VideoUrl = url;
            st.Title = "";
            st.AnchorVideoTime = 0;
            st.AnchorNetTime = now;
            st.Playing = true;
        });
    }

    /// <summary>
    /// Writes the screen's pose into Dev.ScreenPose so a restart puts it back where it was.
    /// Host only, because the host is the authority on where the screen is - a guest would
    /// be recording a pose that the next state broadcast overwrites.
    ///
    /// Runs on every placement, including each nudge, so the spot you settle on is already
    /// saved by the time you stop clicking.
    /// </summary>
    private void RememberScreenPose(Vector3 position, float yaw)
    {
        Plugin.ScreenPose.Value = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:F2},{1:F2},{2:F2},{3:F1}", position.x, position.y, position.z, yaw);
        _configDirty = true;
    }

    /// <summary>
    /// Writes pending config changes, at most once every few seconds.
    ///
    /// Config.Save() writes the file synchronously on the main thread. Calling it straight
    /// from a button handler meant one disk write per click while dragging the screen into
    /// place; batching keeps that off the input path.
    /// </summary>
    private void FlushConfigIfDue()
    {
        if (!_configDirty || Time.unscaledTime < _nextConfigSave) return;
        _configDirty = false;
        _nextConfigSave = Time.unscaledTime + ConfigSaveIntervalSeconds;

        try { Plugin.Instance.Config.Save(); }
        catch (Exception e) { Plugin.Log.LogWarning($"Saving the config failed: {e.Message}"); }
    }

    private bool OnGuestRequest(Request req, NetworkConnectionToClient from)
    {
        Plugin.Log.LogInfo($"Guest {from?.connectionId} requested {req.Action}.");
        ApplyOnHost(req);
        return true;
    }

    private void OnStateChanged(SyncState s)
    {
        // Any change to the timeline should make us re-check our position promptly.
        _lastSeekAt = -100f;
    }

    // --- Reconciliation: make the local world match the state -------------------------------

    private void ReconcileScreen()
    {
        var s = Session.State;
        if (s.HasScreen && _screen == null)
        {
            try
            {
                _screen = ScreenObject.Create(s.ScreenPosition, s.ScreenYaw, s.ScreenWidth,
                    Plugin.RenderWidth.Value, Plugin.RenderHeight.Value);
                _video = CreateBackend();
                _video.SetVolume(Plugin.Volume.Value);
                _loadedPageUrl = null;
                Plugin.Log.LogInfo($"Screen placed at {s.ScreenPosition}.");
            }
            catch (Exception e)
            {
                LastError = "Could not create the screen: " + e.Message;
                Plugin.Log.LogError(LastError + "\n" + e);
                // Avoid retrying every frame.
                _screen?.Dispose(); _screen = null;
                if (Session.IsHost) Session.Mutate(x => x.HasScreen = false);
            }
        }
        else if (!s.HasScreen && _screen != null)
        {
            TearDownScreen();
        }
        else if (_screen != null && _screen.Root != null)
        {
            if ((_screen.Root.transform.position - s.ScreenPosition).sqrMagnitude > 0.0001f
                || Mathf.Abs(Mathf.DeltaAngle(_screen.Root.transform.eulerAngles.y, s.ScreenYaw)) > 0.1f)
            {
                _screen.MoveTo(s.ScreenPosition, s.ScreenYaw);
            }
        }

        // Cheap no-op unless the configured height actually changed, so the screen can be
        // raised or lowered from the config file without re-placing it.
        _screen?.ApplyLayout(s.ScreenClearance);
        TickConsole();
    }

    /// <summary>
    /// Drives the in-world console: where the player is aiming, and whether they pressed.
    ///
    /// The interact key is swallowed while the F8 panel is open, so typing in the panel's
    /// URL field cannot also mash console buttons behind it.
    /// </summary>
    private void TickConsole()
    {
        var console = _screen?.Console;
        if (console == null) return;

        bool aimValid = TryGetAimRay(out var origin, out var direction);
        _lastAimOrigin = aimValid ? origin : Vector3.zero;

        // Aiming was proven correct by the diagnostics - the ray lands on the right button -
        // so anything still not working is the press itself. A mouse click counts too: it is
        // the obvious thing to try when looking at a button, and it means the console does
        // not depend on one key surviving whatever the game does to input.
        // Left click is the game's own interact button, so it is the default and E is not:
        // E raises the player's right arm in Big Walk. The key is opt-in via config.
        var key = Plugin.InteractKey.Value;
        bool interact = key != KeyCode.None && Input.GetKeyDown(key);
        // Legacy Input only sees the mouse, so a gamepad's interact button never reached us.
        // GameInput asks Rewired, which is where the game's real bindings live.
        bool click = Input.GetMouseButtonDown(0) || GameInput.InteractPressed();

        if (Plugin.Diagnostics.Value && Input.anyKeyDown)
        {
            // Proves whether legacy Input sees anything at all, which is the difference
            // between "the key is swallowed" and "we are ignoring it on purpose".
            Plugin.Log.LogInfo($"Input: anyKeyDown  interact({Plugin.InteractKey.Value})={interact} " +
                               $"click/interact={click} panelOpen={_panelVisible}");
        }

        bool pressed = (interact || click) && !_panelVisible;
        if ((interact || click) && _panelVisible)
            Plugin.Log.LogInfo("Console: press ignored because the F8 panel is open.");

        console.Tick(this, aimValid, origin, direction, Session.State.Playing, ConsoleStatus(),
                     Plugin.ConsoleReach.Value, pressed);
    }

    /// <summary>
    /// Boils the mod's state down to the four colours the console's lamp can show. Resolving
    /// a URL takes several seconds, so "working" has to be distinguishable from "nothing
    /// happened" or pressing paste looks like it did nothing at all.
    /// </summary>
    private ControlConsole.Status ConsoleStatus()
    {
        if (!string.IsNullOrEmpty(LastError) && Time.unscaledTime - _lastErrorAt < ConsoleErrorSeconds)
            return ControlConsole.Status.Error;
        if (string.IsNullOrEmpty(Session.State.VideoUrl)) return ControlConsole.Status.Idle;
        return _video != null && _video.IsReady ? ControlConsole.Status.Ready : ControlConsole.Status.Working;
    }

    /// <summary>Where the local player is looking, for aiming at the console.</summary>
    private static bool TryGetAimRay(out Vector3 origin, out Vector3 direction)
    {
        origin = Vector3.zero;
        direction = Vector3.forward;
        try
        {
            var pc = WorldManager.localPlayerCharacter;
            if (pc == null) return false;
            var cam = pc.cameraTransform;
            if (cam == null) return false;

            origin = cam.position;
            direction = cam.forward;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The console has no text, so anything it needs to say in words comes out here and
    /// shows up in the F8 panel and the log.
    /// </summary>
    internal void ReportConsoleError(string message)
    {
        LastError = message;
        Plugin.Log.LogWarning("Console: " + message);
    }

    private void ReconcileVideo()
    {
        var s = Session.State;
        if (_video == null) return;

        string wanted = s.VideoUrl ?? "";

        if (wanted.Length == 0)
        {
            if (_loadedPageUrl != null || _resolvingPageUrl != null)
            {
                CancelResolve();
                _video.Stop();
                _screen?.ClearToIdle();
                _loadedPageUrl = null;
                StatusLine = "Screen idle.";
            }
            _video.Tick();
            return;
        }

        if (wanted != _loadedPageUrl && wanted != _resolvingPageUrl)
        {
            StartResolve(wanted);
        }

        _video.Tick();

        if (_video.Error != null && LastError != _video.Error)
        {
            LastError = _video.Error;
        }
    }

    private void StartResolve(string pageUrl)
    {
        Util.Trace.Write("StartResolve enter");
        CancelResolve();
        _resolvingPageUrl = pageUrl;
        _resolveCts = new CancellationTokenSource();
        var ct = _resolveCts.Token;
        LastError = null;

        void Complete(YtDlp.Result r) => MainThread.Post(() =>
        {
            if (ct.IsCancellationRequested || _resolvingPageUrl != pageUrl) return;
            _resolvingPageUrl = null;
            if (_video == null) return; // screen was removed while we were resolving
            if (!r.Ok)
            {
                LastError = r.Error;
                StatusLine = "Could not load video.";
                _loadedPageUrl = pageUrl; // don't retry in a loop; a new Load will change the URL/revision
                Plugin.Log.LogWarning($"Resolve failed: {r.Error}");
                return;
            }
            Plugin.Log.LogInfo($"Resolved '{r.Title}' ({r.Duration:F0}s).");
            // The stream URL is what actually decides whether playback works, so describe it.
            // Logged as host + itag + mime rather than in full: the whole googlevideo URL is
            // ~1100 characters of signed query string and contains a signature.
            Plugin.Log.LogInfo("Stream: " + DescribeStreamUrl(r.DirectUrl));
            StatusLine = $"Loading '{r.Title}'...";
            _loadedPageUrl = pageUrl;
            _pendingInitialSeek = true;
            _video.Load(r.DirectUrl, r.Duration);
            if (Session.IsHost && Session.State.VideoUrl == pageUrl && Session.State.Title != r.Title)
            {
                Session.Mutate(x =>
                {
                    x.Title = r.Title;
                    // Backfill the queued entry too, so the "up next" list stops showing a bare
                    // URL once the title is known. Only the current one needs it.
                    int i = x.QueueIndex;
                    if (i >= 0 && i < x.Queue.Count && !string.IsNullOrEmpty(r.Title))
                        x.Queue[i].Title = r.Title;
                });
            }
        });

        // A URL that already points at a media file needs no resolving, and Dev.DirectUrl forces
        // the same path for any page URL. Both skip yt-dlp completely, which also separates two
        // things that currently fail together: whether the video pipeline works, and whether
        // running yt-dlp is what kills the process.
        string directUrl = Plugin.DevDirectUrl.Value;
        if (string.IsNullOrWhiteSpace(directUrl) && YtDlp.LooksLikeDirectMedia(pageUrl))
            directUrl = pageUrl;

        if (!string.IsNullOrWhiteSpace(directUrl))
        {
            StatusLine = "Loading direct URL (yt-dlp skipped)...";
            Util.Trace.Write($"StartResolve: direct media, calling Complete: {directUrl.Trim()}");
            Complete(new YtDlp.Result { Ok = true, Title = "Direct URL", DirectUrl = directUrl.Trim() });
            return;
        }

        StatusLine = "Resolving stream with yt-dlp...";
        Plugin.Log.LogInfo($"Resolving {pageUrl}");
        RunOffThread("BigScreen-resolve", () => Complete(YtDlp.Resolve(pageUrl, ct)));
    }

    /// <summary>
    /// Runs work on a dedicated background thread rather than the runtime thread pool.
    ///
    /// The mod previously used Task.Run here. The game died twice with a fatal access violation
    /// inside coreclr.dll while a resolve was in flight, and this was the only concurrency the
    /// mod owned, so we keep our background work on threads we create ourselves. See the note
    /// on the YtDlp class. Exceptions are logged here because nothing awaits this work.
    /// </summary>
    private static void RunOffThread(string name, Action work)
    {
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception e) { Plugin.Log.LogError($"{name} failed: {e}"); }
        })
        { IsBackground = true, Name = name };
        thread.Start();
    }

    private void CancelResolve()
    {
        try { _resolveCts?.Cancel(); } catch { }
        _resolveCts = null;
        _resolvingPageUrl = null;
    }

    /// <summary>
    /// Keep the local player on the host's timeline. Runs for host and guests alike.
    /// </summary>
    private void FollowTimeline()
    {
        var s = Session.State;
        if (_video == null || !_video.IsLoaded || string.IsNullOrEmpty(s.VideoUrl)) return;
        if (!_video.IsReady)
        {
            if (_video.Error == null) StatusLine = "Buffering...";
            return;
        }

        double now = SafeNetTime();
        double expected = s.ExpectedVideoTime(now);
        double len = _video.Duration;
        bool ended = len > 0 && expected >= len - 0.3;

        if (_pendingInitialSeek)
        {
            _pendingInitialSeek = false;
            _video.Seek(expected);
            _lastSeekAt = Time.unscaledTime;
            // Host: a freshly loaded video starts paused at 0; auto-play once ready.
            if (Session.IsHost && !s.Playing && Plugin.AutoPlay.Value && s.AnchorVideoTime <= 0.01 && !ended)
                ApplyOnHost(new Request { Action = Protocol.Action.Play });
        }

        if (ended)
        {
            if (_video.IsPlaying) _video.Pause();
            if (Session.IsHost && s.Playing)
            {
                if (Plugin.AutoAdvance != null && Plugin.AutoAdvance.Value && Session.State.QueueIndex + 1 < Session.State.Queue.Count)
                {
                    // Next film rolls in, and everybody's FollowTimeline picks it up from the
                    // new VideoUrl on their own client. Skipping the "Ended." state entirely
                    // keeps the status line from flickering through it.
                    AutoAdvanceQueue(now);
                    return;
                }
                Session.Mutate(x => { x.Playing = false; x.AnchorVideoTime = Math.Max(0, len - 0.3); x.AnchorNetTime = now; });
            }
            StatusLine = "Ended.";
            LastDrift = 0;
            return;
        }

        double drift = _video.Time - expected;
        LastDrift = drift;

        if (Math.Abs(drift) > Plugin.DriftTolerance.Value && Time.unscaledTime - _lastSeekAt > MinSecondsBetweenSeeks)
        {
            _video.Seek(expected);
            _lastSeekAt = Time.unscaledTime;
            Plugin.Log.LogDebug($"Re-synced: drift was {drift:F2}s.");
        }

        if (s.Playing && !_video.IsPlaying) _video.Play();
        else if (!s.Playing && _video.IsPlaying) _video.Pause();

        StatusLine = s.Playing ? "Playing." : "Paused.";
    }

    // --- Helpers ------------------------------------------------------------------------------

    /// <summary>
    /// A one-line summary of a stream URL for the log: host, plus the few query parameters
    /// that say whether Unity can play it. Never logs the whole URL - googlevideo links are
    /// signed and IP-bound, so the signature has no business in a log people paste around.
    /// </summary>
    private static string DescribeStreamUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(none)";
        try
        {
            var uri = new Uri(url);
            var parts = new System.Collections.Generic.List<string> { uri.Host, $"{url.Length} chars" };
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = pair[..eq];
                // itag is the format number, mime the container Media Foundation will see.
                if (key is "itag" or "mime" or "dur")
                    parts.Add(key + "=" + Uri.UnescapeDataString(pair[(eq + 1)..]));
            }
            return string.Join("  ", parts);
        }
        catch
        {
            return $"({url.Length} chars, unparseable)";
        }
    }

    private static double SafeNetTime()
    {
        try { return NetworkTime.time; }
        catch { return Time.unscaledTimeAsDouble; }
    }

    /// <summary>
    /// The local player's character, or false if the lobby has not produced one yet.
    /// WorldManager usually has it; the scan is the fallback for the window right after a
    /// join when it is still null.
    /// </summary>
    internal static bool TryGetLocalPlayer(out PlayerCharacter player)
    {
        player = null;
        try
        {
            PlayerCharacter pc = WorldManager.localPlayerCharacter;
            if (pc == null)
            {
                var all = PlayerCharacter.allPlayerCharacters;
                if (all != null)
                    for (int i = 0; i < all.Count; i++)
                    {
                        var p = all[i];
                        if (p != null && p.playerNetworking != null && p.playerNetworking.isLocalPlayer) { pc = p; break; }
                    }
            }
            if (pc == null) return false;
            player = pc;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Local player lookup failed: {e.Message}");
            return false;
        }
    }

    private static bool TryGetLocalPlayerPose(out Vector3 position, out Vector3 forward)
    {
        position = Vector3.zero;
        forward = Vector3.forward;
        try
        {
            if (!TryGetLocalPlayer(out var pc)) return false;

            position = pc.transform.position;
            // Prefer the camera's facing so "in front of me" matches what the player is looking at.
            var cam = pc.cameraTransform != null ? pc.cameraTransform : pc.transform;
            forward = cam.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = pc.transform.forward;
            forward.Normalize();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Local player lookup failed: {e.Message}");
            return false;
        }
    }

    private IVideoBackend CreateBackend()
    {
        bool useMpv = Plugin.PreferredBackend.Value == VideoBackendType.Mpv ||
                      (Plugin.PreferredBackend.Value == VideoBackendType.Auto && Video.Mpv.MpvNative.IsAvailable);

        if (useMpv)
        {
            Plugin.Log.LogInfo("Initializing MpvVideoBackend (libmpv).");
            try
            {
                return new MpvVideoBackend(_screen.Root, _screen.Audio, _screen.Texture);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Failed to initialize MpvVideoBackend ({e.Message}); falling back to UnityVideoBackend.");
            }
        }

        Plugin.Log.LogInfo("Initializing UnityVideoBackend.");
        return new UnityVideoBackend(_screen.Root, _screen.Audio, _screen.Texture);
    }

    private void TearDownScreen()
    {
        CancelResolve();
        try { _video?.Dispose(); } catch { }
        _video = null;
        try { _screen?.Dispose(); } catch { }
        _screen = null;
        _loadedPageUrl = null;
        _pendingInitialSeek = false;
    }

    /// <summary>Full reset: called when leaving a lobby or when the plugin unloads.</summary>
    internal void ResetSession(string reason)
    {
        Plugin.Log.LogInfo($"Reset ({reason}).");
        // Leaving the lobby (or unloading) is the last chance to persist a pending change.
        _nextConfigSave = 0f;
        FlushConfigIfDue();
        TearDownScreen();
        Session.Reset();
        SleepGuard.Reset();
        CrosshairGuard.Reset();
        StatusLine = "Idle.";
        LastError = null;
        LastDrift = 0;
    }
}
