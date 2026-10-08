using System;
using UnityEngine;

namespace BigScreen.UI;

/// <summary>
/// Immediate-mode (IMGUI) control panel. IMGUI is the one UI toolkit that needs no
/// assets shipped with the mod, which is why every Big Walk mod uses it for overlays.
/// All state lives on the controller; this class only draws and forwards clicks.
///
/// The default IMGUI window background is partly transparent, which is unreadable over the
/// red starting area. We draw our own opaque background instead, and keep error text amber
/// rather than red so it stays legible against that same area.
/// </summary>
internal sealed class ControlPanel
{
    private readonly BigScreenController _c;
    private Rect _window = new Rect(40f, 40f, 760f, 740f);
    private string _urlInput = "";

    private int _tab; // 0 = Direct / YouTube, 1 = Torbox Cloud
    private string _torboxSearch = "";
    private string _torboxKeyInput = "";
    private string _torboxKeyStatus = "";
    private bool _isSearching;
    private string _searchStatus = "";
    private System.Collections.Generic.List<Torbox.TorrentioClient.CatalogItem> _searchResults = new();
    private Torbox.TorrentioClient.CatalogItem _selectedCatalog;
    private int _seriesSeason = 1;
    private int _seriesEpisode = 1;
    private System.Collections.Generic.List<Torbox.TorrentioClient.TorrentioStream> _streams = new();
    private Vector2 _scrollPos;
    private Vector2 _queueScroll;

    private GUIStyle _windowStyle;
    private GUIStyle _label;
    private GUIStyle _error;
    private GUIStyle _button;
    private GUIStyle _textField;
    private GUIStyle _toggle;
    private Texture2D _bgTexture;
    private int _builtForFontSize = -1;

    private float _decoderProbeAt = -999f;
    private string _decoderLabel;

    private const int WindowId = 0x8153;

    // How far one Left/Right/Back/Forward click slides the screen. Height uses a finer step:
    // getting the height wrong is more obvious than being a few centimetres off sideways.
    private const float NudgeMeters = 0.25f;

    public ControlPanel(BigScreenController controller)
    {
        _c = controller;
    }

    /// <summary>A 1x1 texture used as a flat background fill.</summary>
    private static Texture2D SolidTexture(Color color)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        t.SetPixel(0, 0, color);
        t.Apply();
        // Keep Unity from unloading it between scenes; we never destroy it.
        t.hideFlags = HideFlags.HideAndDontSave;
        return t;
    }

    /// <summary>
    /// Builds the styles. Re-runs if the configured font size changed, so the size can be
    /// tuned from the config file without restarting the game.
    /// </summary>
    private void EnsureStyles()
    {
        int size = Mathf.Clamp(Plugin.UiFontSize.Value, 8, 40);
        if (_windowStyle != null && _builtForFontSize == size) return;
        _builtForFontSize = size;

        if (_bgTexture == null) _bgTexture = SolidTexture(new Color(0.05f, 0.05f, 0.07f, 0.97f));

        _windowStyle = new GUIStyle(GUI.skin.window)
        {
            fontSize = size + 2,
            padding = new RectOffset(12, 12, size + 14, 12),
        };
        _windowStyle.normal.background = _bgTexture;
        _windowStyle.onNormal.background = _bgTexture;
        _windowStyle.normal.textColor = Color.white;
        _windowStyle.onNormal.textColor = Color.white;

        _label = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true };
        _label.normal.textColor = new Color(0.92f, 0.92f, 0.94f);

        // Amber, not red: the starting area is mostly red and red-on-red disappears.
        _error = new GUIStyle(_label) { fontSize = size, wordWrap = true };
        _error.normal.textColor = new Color(1f, 0.78f, 0.25f);

        _button = new GUIStyle(GUI.skin.button) { fontSize = size };
        _textField = new GUIStyle(GUI.skin.textField) { fontSize = size };
        _toggle = new GUIStyle(GUI.skin.toggle) { fontSize = size };
    }

    private float ButtonHeight => _builtForFontSize + 16f;

    // Built once and kept alive for the life of the panel.
    //
    // Casting a managed method to GUI.WindowFunction creates an IL2CPP delegate wrapping a
    // managed trampoline. Doing that inline in Draw() made a new one every frame while the
    // panel was open, with no managed reference kept. Unity holds the native side across the
    // call, so once the GC collected one the process died with an access violation on the
    // main thread - intermittent, and only ever with the panel open.
    //
    // MirrorChannel keeps its converted handlers alive for the same reason.
    private GUI.WindowFunction _drawWindow;

    public void Draw()
    {
        EnsureStyles();
        _drawWindow ??= (GUI.WindowFunction)DrawWindow;
        _window = GUI.Window(WindowId, _window, _drawWindow,
                             "BigScreen - watch together", _windowStyle);
    }

    private void DrawWindow(int id)
    {
        try
        {
            DrawBody();
        }
        catch (Exception e)
        {
            GUILayout.Label("UI error: " + e.Message, _error);
        }
        GUI.DragWindow(new Rect(0f, 0f, 10000f, ButtonHeight));
    }

    private void DrawBody()
    {
        var session = _c.Session;
        bool inLobby = session.IsHost || session.IsConnectedClient;
        bool canControl = _c.CanControl;

        // --- Status ---------------------------------------------------------------
        if (!inLobby)
        {
            GUILayout.Label("Not in a lobby. Host or join a walk first.", _label);
        }
        else
        {
            string role = session.IsHost ? $"Host (modded guests: {session.ModdedPeerCount})"
                                         : (session.HelloSent ? "Guest (connected to host)" : "Guest (waiting for host...)");
            GUILayout.Label($"Role: {role}", _label);
            if (!session.IsHost && !session.State.GuestsCanControl)
                GUILayout.Label("The host has not enabled guest control; you can watch and adjust your own volume.", _label);
        }
        GUILayout.Label(_c.StatusLine, _label);
        if (!string.IsNullOrEmpty(_c.LastError))
            GUILayout.Label(_c.LastError, _error);
        GUILayout.Space(8f);

        // --- Screen placement -------------------------------------------------------
        GUILayout.BeginHorizontal();
        GUI.enabled = inLobby && canControl;
        if (GUILayout.Button(_c.Session.State.HasScreen ? "Move screen here" : "Place screen here", _button, GUILayout.Height(ButtonHeight)))
            _c.UserPlaceScreen();
        GUI.enabled = inLobby && canControl && _c.Session.State.HasScreen;
        if (GUILayout.Button("Remove screen", _button, GUILayout.Height(ButtonHeight)))
            _c.UserRemoveScreen();
        if (Plugin.ShowDevTools.Value)
        {
            GUI.enabled = inLobby;
            if (GUILayout.Button("Set spawn here", _button, GUILayout.Height(ButtonHeight)))
                _c.UserSetSpawnHere();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        // Height is local geometry rather than shared state, so this works whether or not
        // the host has handed out control. Editing the config file mid-game does nothing;
        // BepInEx does not re-read it, so these buttons are the way to dial the height in.
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Screen height: {_c.Session.State.ScreenClearance:F2} m", _label,
                        GUILayout.Width(_builtForFontSize * 13f));
        // Height is the host's geometry, so the buttons are the host's to press.
        GUI.enabled = _c.Session.State.HasScreen && _c.Session.IsHost;
        if (GUILayout.Button("Lower", _button, GUILayout.Width(_builtForFontSize * 6f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreenHeight(-0.1f);
        if (GUILayout.Button("Raise", _button, GUILayout.Width(_builtForFontSize * 6f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreenHeight(+0.1f);
        GUI.enabled = true;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        // Sliding the screen moves it for everyone, so unlike the height this needs control.
        // Directions are relative to the screen's own facing, not the world axes.
        var screenPos = _c.Session.State.ScreenPosition;
        GUILayout.BeginHorizontal();
        GUILayout.Label(_c.Session.State.HasScreen
                            ? $"Screen at ({screenPos.x:F1}, {screenPos.y:F1}, {screenPos.z:F1})"
                            : "Screen at (none placed)",
                        _label, GUILayout.Width(_builtForFontSize * 13f));
        GUI.enabled = inLobby && canControl && _c.Session.State.HasScreen;
        if (GUILayout.Button("Left", _button, GUILayout.Width(_builtForFontSize * 5f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreen(-NudgeMeters, 0f);
        if (GUILayout.Button("Right", _button, GUILayout.Width(_builtForFontSize * 5f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreen(+NudgeMeters, 0f);
        if (GUILayout.Button("Back", _button, GUILayout.Width(_builtForFontSize * 5f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreen(0f, -NudgeMeters);
        if (GUILayout.Button("Forward", _button, GUILayout.Width(_builtForFontSize * 7f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreen(0f, +NudgeMeters);
        GUI.enabled = true;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        // --- Media Source Tabs ---------------------------------------------------------
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(_tab == 0, " YouTube / Direct URL ", _button, GUILayout.Height(ButtonHeight))) _tab = 0;
        if (GUILayout.Toggle(_tab == 1, " Torbox Cloud / Torrentio ", _button, GUILayout.Height(ButtonHeight))) _tab = 1;
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);

        if (_tab == 0)
        {
            GUILayout.Label("YouTube URL (or direct MP4/MKV stream link):", _label);
            GUILayout.BeginHorizontal();
            GUI.enabled = inLobby && canControl;
            _urlInput = GUILayout.TextField(_urlInput ?? "", _textField, GUILayout.ExpandWidth(true), GUILayout.Height(ButtonHeight));
            if (GUILayout.Button("Load", _button, GUILayout.Width(90f), GUILayout.Height(ButtonHeight)))
                _c.UserLoad(_urlInput);
            if (GUILayout.Button("Queue", _button, GUILayout.Width(90f), GUILayout.Height(ButtonHeight)))
                _c.UserQueueAdd(_urlInput);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
        else
        {
            DrawTorboxBrowser(inLobby, canControl);
        }

        GUI.enabled = inLobby && canControl;
        var st = session.State;
        if (!string.IsNullOrEmpty(st.VideoUrl))
        {
            GUILayout.Label($"Now: {(string.IsNullOrEmpty(st.Title) ? st.VideoUrl : st.Title)}", _label);
            double pos = _c.LocalVideoTime;
            double len = _c.LocalVideoDuration;
            GUILayout.Label($"{Fmt(pos)} / {(len > 0 ? Fmt(len) : "?")}   {(st.Playing ? "playing" : "paused")}   drift {_c.LastDrift:+0.00;-0.00}s", _label);
        }

        DrawQueue(inLobby, canControl);

        GUILayout.BeginHorizontal();
        GUI.enabled = inLobby && canControl && !string.IsNullOrEmpty(st.VideoUrl);
        if (GUILayout.Button(st.Playing ? "Pause" : "Play", _button, GUILayout.Height(ButtonHeight))) _c.UserTogglePlay();
        if (GUILayout.Button("-30s", _button, GUILayout.Height(ButtonHeight))) _c.UserSeekRelative(-30);
        if (GUILayout.Button("-5s", _button, GUILayout.Height(ButtonHeight))) _c.UserSeekRelative(-5);
        if (GUILayout.Button("+5s", _button, GUILayout.Height(ButtonHeight))) _c.UserSeekRelative(5);
        if (GUILayout.Button("+30s", _button, GUILayout.Height(ButtonHeight))) _c.UserSeekRelative(30);
        if (GUILayout.Button("Restart", _button, GUILayout.Height(ButtonHeight))) _c.UserSeekTo(0);
        if (GUILayout.Button("Stop", _button, GUILayout.Height(ButtonHeight))) _c.UserStop();
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        // --- Local settings ----------------------------------------------------------------
        GUILayout.BeginHorizontal();
        GUILayout.Label("My volume", _label, GUILayout.Width(_builtForFontSize * 7f));
        float v = GUILayout.HorizontalSlider(Plugin.Volume.Value, 0f, 1f, GUILayout.Width(220f));
        if (Math.Abs(v - Plugin.Volume.Value) > 0.001f) { Plugin.Volume.Value = v; _c.ApplyLocalAudioConfig(); }
        GUILayout.Label($"{(int)(v * 100)}%", _label, GUILayout.Width(_builtForFontSize * 4f));
        GUILayout.EndHorizontal();

        if (session.IsHost)
        {
            bool g = GUILayout.Toggle(Plugin.GuestsCanControl.Value, " Let modded guests control playback", _toggle);
            if (g != Plugin.GuestsCanControl.Value) Plugin.GuestsCanControl.Value = g;
            bool a = GUILayout.Toggle(Plugin.AutoPlay.Value, " Auto-play when a video is ready", _toggle);
            if (a != Plugin.AutoPlay.Value) Plugin.AutoPlay.Value = a;
        }

        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Decoder: {DecoderLabel()}", _label);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Update yt-dlp", _button, GUILayout.Width(_builtForFontSize * 10f), GUILayout.Height(ButtonHeight))) _c.UserUpdateYtDlp();
        if (GUILayout.Button($"Close [{Plugin.ToggleUiKey.Value}]", _button, GUILayout.Width(_builtForFontSize * 10f), GUILayout.Height(ButtonHeight))) _c.SetPanelVisible(false);
        GUILayout.EndHorizontal();
    }

    /// <summary>
    /// The one thing standing between a fresh install and cloud playback is a Torbox API key.
    /// Rather than sending people to hunt for it in the config file, we paste it here.
    /// </summary>
    /// <summary>
    /// Which decoder is actually in use. libmpv renders into a plain memory buffer here
    /// (no GPU context exists inside the game), so the honest label says "software render":
    /// nothing about this path is hardware accelerated, and the old "HW Accel" label was
    /// lying about the one thing people would use to decide between backends.
    /// </summary>
    private string DecoderLabel()
    {
        // IsAvailable() loads and unloads the native library, so cache the answer instead of
        // doing that every IMGUI redraw.
        if (Time.unscaledTime - _decoderProbeAt > 2f || _decoderLabel == null)
        {
            _decoderProbeAt = Time.unscaledTime;
            bool mpv = Video.Mpv.MpvNative.IsAvailable;
            bool forced = Plugin.PreferredBackend.Value == VideoBackendType.Unity;
            _decoderLabel = mpv
                ? (forced
                    ? "Unity VideoPlayer (libmpv is available)"
                    : "libmpv, " + HwDecodeDescription())
                : "Unity VideoPlayer";
        }
        return _decoderLabel;
    }

    /// <summary>
    /// The shared queue: what is playing, what is next, and the controls for it.
    ///
    /// "Up next" is the part that turns this from a screen into movie night - decide what to
    /// watch while something else is still playing, instead of one person fumbling with a
    /// URL during the credits. The whole list is host state, so it is identical for everyone.
    /// </summary>
    private void DrawQueue(bool inLobby, bool canControl)
    {
        var queue = _c.Session.State.Queue;
        int current = _c.Session.State.QueueIndex;
        int waiting = 0;
        for (int i = 0; i < queue.Count; i++) if (i > current) waiting++;

        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();
        string auto = _c.Session.State.AutoAdvance ? "on" : "off";
        GUILayout.Label($"Up next ({queue.Count} queued, {waiting} after this one, auto-advance {auto})", _label);
        GUILayout.FlexibleSpace();
        GUI.enabled = inLobby && canControl && queue.Count > 0;
        if (GUILayout.Button("Prev", _button, GUILayout.Width(_builtForFontSize * 4f), GUILayout.Height(ButtonHeight)))
            _c.UserQueueStep(-1);
        if (GUILayout.Button("Next", _button, GUILayout.Width(_builtForFontSize * 4f), GUILayout.Height(ButtonHeight)))
            _c.UserQueueStep(+1);
        if (GUILayout.Button("Clear", _button, GUILayout.Width(_builtForFontSize * 5f), GUILayout.Height(ButtonHeight)))
            _c.UserQueueClear();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (queue.Count == 0)
        {
            GUILayout.Label("Nothing queued. Paste a URL and press Queue, or add a Torrentio stream.", _label);
            return;
        }

        _queueScroll = GUILayout.BeginScrollView(_queueScroll, GUILayout.Height(Mathf.Min(120f, 26f * queue.Count + 6f)));
        for (int i = 0; i < queue.Count; i++)
        {
            var entry = queue[i];
            if (entry == null) continue;
            bool isCurrent = i == current;

            GUILayout.BeginHorizontal(isCurrent ? GUI.skin.box : GUIStyle.none);
            string label = !string.IsNullOrEmpty(entry.Title) ? entry.Title : entry.Url;
            if (label.Length > 60) label = label.Substring(0, 57) + "...";
            GUILayout.Label((isCurrent ? "> " : (i + 1) + ". ") + label, _label, GUILayout.ExpandWidth(true));

            GUI.enabled = inLobby && canControl;
            if (GUILayout.Button(isCurrent ? "Replay" : "Play", _button, GUILayout.Width(70f), GUILayout.Height(ButtonHeight)))
                _c.UserQueuePlay(i);
            if (GUILayout.Button("X", _button, GUILayout.Width(28f), GUILayout.Height(ButtonHeight)))
                _c.UserQueueRemove(i);
            GUI.enabled = true;

            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    /// <summary>
    /// What the decoder is actually doing. Distributed by value because the truth is
    /// "GPU decode, then a copy into memory and a software blit", not "hardware accelerated".
    /// </summary>
    private static string HwDecodeDescription() => Plugin.HwDecode?.Value switch
    {
        HwDecodeMode.No => "software decoding",
        HwDecodeMode.Auto => "direct hardware decode",
        _ => "GPU decode + software blit",
    };

    private void DrawTorboxSetup()
    {
        string addonUrl = (Plugin.TorrentioAddonUrl?.Value ?? "").Trim();
        string apiKey = (Plugin.TorboxApiKey?.Value ?? "").Trim();

        if (Torbox.TorrentioClient.IsConfigured)
        {
            string what = addonUrl.Length > 0
                ? "Custom addon URL"
                : "Torbox API key";
            GUILayout.Label($"Torrentio ready ({what}). Guests get the finished stream, they do not need Torbox.", _label);
            return;
        }

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("Torbox is not set up yet.", _label);
        GUILayout.Label("Paste your Torbox API key (torbox.app/settings). It is used to turn torrents into " +
                        "direct streams; without it you only get magnet links, which cannot be played.", _label);
        GUILayout.BeginHorizontal();
        _torboxKeyInput = GUILayout.TextField(_torboxKeyInput ?? "", _textField, GUILayout.ExpandWidth(true), GUILayout.Height(ButtonHeight));
        if (GUILayout.Button("Save key", _button, GUILayout.Width(110f), GUILayout.Height(ButtonHeight)))
        {
            var trimmed = (_torboxKeyInput ?? "").Trim();
            if (trimmed.Length == 0)
            {
                _torboxKeyStatus = "Paste a key first.";
            }
            else
            {
                Plugin.TorboxApiKey.Value = trimmed;
                Plugin.SaveConfig();
                _torboxKeyStatus = "Saved. You can search now. The key is stored in BepInEx.cfg - do not share that file or your log.";
                Plugin.Log.LogInfo("Torbox API key saved.");
            }
        }
        GUILayout.EndHorizontal();
        if (_torboxKeyStatus.Length > 0) GUILayout.Label(_torboxKeyStatus, _error);
        GUILayout.Label("Or set Torbox -> TorrentioAddonUrl to a full addon URL from torrentio.strem.fun " +
                        "(needed for RealDebrid / Premiumize / AllDebrid instead of Torbox).", _label);
        GUILayout.EndVertical();
    }

    private void DrawTorboxBrowser(bool inLobby, bool canControl)
    {
        DrawTorboxSetup();

        GUILayout.BeginHorizontal();
        GUI.enabled = !_isSearching;
        _torboxSearch = GUILayout.TextField(_torboxSearch ?? "", _textField, GUILayout.ExpandWidth(true), GUILayout.Height(ButtonHeight));
        if (GUILayout.Button(_isSearching ? "Searching..." : "Search / IMDB ID", _button, GUILayout.Width(170f), GUILayout.Height(ButtonHeight)))
        {
            SearchTorbox();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(_searchStatus))
            GUILayout.Label(_searchStatus, _label);

        // Catalog choices
        if (_searchResults.Count > 0)
        {
            GUILayout.Label("Search Results (click to choose):", _label);
            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(150f));
            foreach (var item in _searchResults)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{item.Name} ({item.Year}) [{item.Type.ToUpperInvariant()}]", _label);
                if (GUILayout.Button("Select", _button, GUILayout.Width(80f), GUILayout.Height(ButtonHeight)))
                {
                    SelectCatalogItem(item);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        // Series season/episode picker
        if (_selectedCatalog != null && _selectedCatalog.Type == "series")
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Season: {_seriesSeason}", _label, GUILayout.Width(100f));
            if (GUILayout.Button("-", _button, GUILayout.Width(35f), GUILayout.Height(ButtonHeight)) && _seriesSeason > 1) _seriesSeason--;
            if (GUILayout.Button("+", _button, GUILayout.Width(35f), GUILayout.Height(ButtonHeight))) _seriesSeason++;

            GUILayout.Space(12f);
            GUILayout.Label($"Episode: {_seriesEpisode}", _label, GUILayout.Width(100f));
            if (GUILayout.Button("-", _button, GUILayout.Width(35f), GUILayout.Height(ButtonHeight)) && _seriesEpisode > 1) _seriesEpisode--;
            if (GUILayout.Button("+", _button, GUILayout.Width(35f), GUILayout.Height(ButtonHeight))) _seriesEpisode++;

            GUILayout.Space(12f);
            if (GUILayout.Button("Find Episodes", _button, GUILayout.Height(ButtonHeight)))
            {
                FetchStreamsForSelected();
            }
            GUILayout.EndHorizontal();
        }

        // Stream list
        if (_streams.Count > 0)
        {
            GUILayout.Label("Available Torbox Streams (cached in cloud):", _label);
            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(180f));
            foreach (var s in _streams)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                string qual = string.IsNullOrEmpty(s.Quality) ? "Stream" : s.Quality;
                GUILayout.Label($"[{qual}] {s.CleanTitle}", _label, GUILayout.ExpandWidth(true));
                GUI.enabled = inLobby && canControl;
                if (GUILayout.Button("Play", _button, GUILayout.Width(75f), GUILayout.Height(ButtonHeight)))
                {
                    _c.UserLoad(s.Url);
                }
                if (GUILayout.Button("Queue", _button, GUILayout.Width(75f), GUILayout.Height(ButtonHeight)))
                {
                    // Queue under the filename we already have, so the entry is readable in
                    // the list before anything has been resolved.
                    _c.UserQueueAdd(s.Url, s.CleanTitle);
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }
    }

    private void SearchTorbox()
    {
        string q = (_torboxSearch ?? "").Trim();
        if (string.IsNullOrEmpty(q)) return;

        _isSearching = true;
        _searchStatus = "Searching Cinemeta...";
        _searchResults.Clear();
        _streams.Clear();
        _selectedCatalog = null;

        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                if (!Torbox.TorrentioClient.IsConfigured)
                {
                    Util.MainThread.Post(() =>
                    {
                        _isSearching = false;
                        _searchStatus = "Set up Torbox first (API key or addon URL) - see above.";
                    });
                    return;
                }

                if (q.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    await LoadStreamsAsync("movie", q);
                    return;
                }

                var items = await Torbox.TorrentioClient.SearchCinemetaAsync(q);
                Util.MainThread.Post(() =>
                {
                    _searchResults = items;
                    _isSearching = false;
                    _searchStatus = items.Count > 0 ? $"Found {items.Count} titles." : "No titles found.";
                });
            }
            catch (Exception e)
            {
                Util.MainThread.Post(() =>
                {
                    _isSearching = false;
                    _searchStatus = "Search failed: " + e.Message;
                });
            }
        });
    }

    private void SelectCatalogItem(Torbox.TorrentioClient.CatalogItem item)
    {
        _selectedCatalog = item;
        _searchResults.Clear();
        if (item.Type == "series")
        {
            _searchStatus = $"Selected {item.Name}. Choose season and episode.";
        }
        else
        {
            FetchStreamsForSelected();
        }
    }

    private void FetchStreamsForSelected()
    {
        if (_selectedCatalog == null) return;
        string type = _selectedCatalog.Type;
        string id = type == "series"
            ? $"{_selectedCatalog.Id}:{_seriesSeason}:{_seriesEpisode}"
            : _selectedCatalog.Id;

        _isSearching = true;
        _searchStatus = $"Fetching Torbox streams for {id}...";
        _streams.Clear();

        System.Threading.Tasks.Task.Run(async () =>
        {
            await LoadStreamsAsync(type, id);
        });
    }

    private async System.Threading.Tasks.Task LoadStreamsAsync(string type, string id)
    {
        try
        {
            if (!Torbox.TorrentioClient.IsConfigured) throw new InvalidOperationException(
                "No Torbox addon configured. Save an API key above, or set TorrentioAddonUrl.");

            var query = await Torbox.TorrentioClient.GetStreamsAsync(type, id);
            Util.MainThread.Post(() =>
            {
                _streams = query.Playable;
                _isSearching = false;
                _searchStatus = query.Problem ?? (query.Playable.Count > 0
                    ? $"Found {query.Playable.Count} playable streams."
                    : "No streams found.");
            });
        }
        catch (Exception e)
        {
            Util.MainThread.Post(() =>
            {
                _isSearching = false;
                _searchStatus = "Stream error: " + e.Message;
            });
        }
    }

    private static string Fmt(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
