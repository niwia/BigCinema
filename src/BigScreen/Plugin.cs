using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace BigScreen;

/// <summary>
/// BigScreen: a shared in-world screen for watching YouTube together.
///
/// Entry point. BepInEx constructs this once at game start (before any scene loads),
/// we bind config, register our MonoBehaviour with the IL2CPP runtime and spin up a
/// persistent host object that drives everything else from Update()/OnGUI().
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "dev.h223chen.bigscreen";
    public const string Name = "BigScreen";
    public const string Version = "1.0.6";

    internal static Plugin Instance { get; private set; }
    // `new` because BasePlugin exposes an instance Log; ours is the static shortcut the
    // rest of the mod uses. Hiding is intentional.
    internal static new ManualLogSource Log;

    // --- Config -----------------------------------------------------------------------
    internal static ConfigEntry<KeyCode> ToggleUiKey;
    internal static ConfigEntry<int> UiFontSize;
    internal static ConfigEntry<float> Volume;
    internal static ConfigEntry<float> AudioMaxDistance;
    internal static ConfigEntry<float> AudioFullVolumeRadius;
    internal static ConfigEntry<float> ScreenWidthMeters;
    internal static ConfigEntry<float> ScreenGroundClearance;
    internal static ConfigEntry<bool> ShowConsole;
    internal static ConfigEntry<float> ConsoleOffset;
    internal static ConfigEntry<float> ConsoleHeight;
    internal static ConfigEntry<float> ConsoleReach;
    internal static ConfigEntry<KeyCode> InteractKey;
    internal static ConfigEntry<string> RewiredAction;
    internal static ConfigEntry<int> RenderWidth;
    internal static ConfigEntry<int> RenderHeight;
    internal static ConfigEntry<string> YtDlpPath;
    internal static ConfigEntry<bool> YtDlpAutoDownload;
    internal static ConfigEntry<string> FormatSelector;
    internal static ConfigEntry<string> ExtractorArgs;
    internal static ConfigEntry<string> CookiesFromBrowser;
    internal static ConfigEntry<float> DriftTolerance;
    internal static ConfigEntry<bool> GuestsCanControl;
    internal static ConfigEntry<bool> AutoPlay;
    internal static ConfigEntry<bool> AutoAdvance;
    internal static ConfigEntry<World.ComfortMode> KeepAwake;
    internal static ConfigEntry<float> KeepAwakeRadius;
    internal static ConfigEntry<World.ComfortMode> HideCrosshair;
    internal static ConfigEntry<float> HideCrosshairDelay;
    internal static ConfigEntry<VideoBackendType> PreferredBackend;
    internal static ConfigEntry<string> MpvPath;
    internal static ConfigEntry<bool> Subtitles;
    internal static ConfigEntry<string> SubtitleLanguage;
    internal static ConfigEntry<bool> SoftwareFastRender;
    internal static ConfigEntry<float> DemuxerCacheSeconds;
    internal static ConfigEntry<HwDecodeMode> HwDecode;
    internal static ConfigEntry<string> TorrentioAddonUrl;
    internal static ConfigEntry<string> TorboxApiKey;
    internal static ConfigEntry<string> TorboxSort;
    internal static ConfigEntry<bool> Diagnostics;

    // Development helpers. Off by default; see BigScreen.Dev.AutoStart.
    internal static ConfigEntry<bool> AutoHost;
    internal static ConfigEntry<string> AutoHostSaveSlot;
    internal static ConfigEntry<int> AutoHostPlayerCount;
    internal static ConfigEntry<float> AutoHostDelay;
    internal static ConfigEntry<string> AutoLoadUrl;
    internal static ConfigEntry<float> AutoLoadDelay;
    internal static ConfigEntry<string> DevDirectUrl;
    internal static ConfigEntry<bool> AutoDismissMenus;
    internal static ConfigEntry<string> SpawnPosition;
    internal static ConfigEntry<string> ScreenPose;
    internal static ConfigEntry<bool> ShowDevTools;

    private Harmony _harmony;

    /// <summary>
    /// Writes the config file immediately. During normal play the controller batches saves;
    /// this is for settings a user just entered by hand (an API key), where losing the value
    /// to a crash would be infuriating.
    /// </summary>
    internal static void SaveConfig()
    {
        try { Instance?.Config.Save(); }
        catch (Exception e) { Log.LogWarning($"Saving the config failed: {e.Message}"); }
    }

    public override void Load()
    {
        Instance = this;
        Log = base.Log;

        ToggleUiKey = Config.Bind("Keys", "ToggleUI", KeyCode.F8,
            "Opens/closes the BigScreen control panel.");

        InteractKey = Config.Bind("Keys", "Interact", KeyCode.None,
            "Optional extra key for pressing the console button you are looking at. Left click " +
            "always works and is the game's own interact button, so this is off by default. " +
            "Avoid E: Big Walk uses it to raise the player's right arm.");

        RewiredAction = Config.Bind("Keys", "RewiredInteractAction", "",
            "Name of the game's own interact action, used so the console responds to a controller " +
            "as well as the mouse. Empty auto-detects it; the log lists every action the game " +
            "defines on the first attempt if the guess is wrong.");

        UiFontSize = Config.Bind("UI", "FontSize", 16,
            new ConfigDescription("Font size for the control panel. The panel picks up a change the " +
                                  "next time it is drawn, so you can edit this while the game runs.",
                new AcceptableValueRange<int>(8, 40)));

        Volume = Config.Bind("Audio", "Volume", 0.8f,
            new ConfigDescription("Local playback volume for the screen's audio (0-1). Only affects you.",
                new AcceptableValueRange<float>(0f, 1f)));
        AudioMaxDistance = Config.Bind("Audio", "MaxDistance", 25f,
            new ConfigDescription("Distance in meters at which the screen's audio fades to silence.",
                new AcceptableValueRange<float>(5f, 200f)));
        AudioFullVolumeRadius = Config.Bind("Audio", "FullVolumeRadius", 1.5f,
            new ConfigDescription("Distance in meters within which the screen plays at full volume. This is " +
                                  "the main control over how local the sound feels: beyond it the volume " +
                                  "falls off as 1/distance, so halving this roughly halves the volume you " +
                                  "hear at any given spot. At the default 1.5 m you hear about a third of " +
                                  "full volume standing 4 m back. Raise it to fill more of the area.",
                new AcceptableValueRange<float>(0.5f, 20f)));

        KeepAwake = Config.Bind("Comfort", "KeepAwake", World.ComfortMode.WhileWatching,
            new ConfigDescription("Stop the game sending you to sleep - the slow dimming that happens when " +
                                  "you stand still too long - while you are watching something. " +
                                  "WhileWatching only holds it off when a video is playing and you are " +
                                  "within KeepAwakeRadius of the screen, so sleep still works normally " +
                                  "everywhere else. Always holds it off for the whole lobby. Off leaves " +
                                  "the game alone. Only affects you; other players still see you sleep " +
                                  "if they walk away from their own keyboards."));
        KeepAwakeRadius = Config.Bind("Comfort", "KeepAwakeRadius", 15f,
            new ConfigDescription("How close to the screen you have to be for KeepAwake=WhileWatching to " +
                                  "hold sleep off, in meters. Measured to the middle of the screen, in " +
                                  "every direction, so standing off to one side still counts.",
                new AcceptableValueRange<float>(1f, 100f)));

        HideCrosshair = Config.Bind("Comfort", "HideCrosshair", World.ComfortMode.WhileWatching,
            new ConfigDescription("Hide the crosshair once you have stood still long enough, and bring it " +
                                  "back the moment you move or look around. A dot in the middle of the " +
                                  "picture is most annoying exactly when you are standing still watching " +
                                  "something. WhileWatching only does it near a playing screen; Always does " +
                                  "it anywhere in a lobby; Off leaves the crosshair alone."));
        HideCrosshairDelay = Config.Bind("Comfort", "HideCrosshairDelaySeconds", 5f,
            new ConfigDescription("How long you have to stand still before the crosshair goes, in seconds. " +
                                  "Short is fine here because it only applies right in front of a playing " +
                                  "screen, and the crosshair comes back the moment you move - so losing it " +
                                  "early costs nothing. 0 follows the game's own sleep delay instead, which " +
                                  "is around half a minute and long enough that a twitch of the mouse keeps " +
                                  "resetting it.",
                new AcceptableValueRange<float>(0f, 600f)));

        ScreenWidthMeters = Config.Bind("Screen", "WidthMeters", 4f,
            new ConfigDescription("Physical width of the screen in the world (16:9, so height follows).",
                new AcceptableValueRange<float>(1f, 20f)));
        ScreenGroundClearance = Config.Bind("Screen", "GroundClearance", -0.3f,
            new ConfigDescription("How high the bottom edge of the picture sits above the spot the screen " +
                                  "was placed at, in meters. Lower this to bring the screen down; 0 puts it " +
                                  "on the ground and negative values sink it. The stand hides itself when " +
                                  "there is no gap left for it. Editing this file mid-game does nothing - " +
                                  "BepInEx does not re-read it - so use the panel's Raise/Lower buttons, " +
                                  "which write the value back here.",
                new AcceptableValueRange<float>(-2f, 5f)));
        ShowConsole = Config.Bind("Screen", "ShowConsole", true,
            "Put a control podium beside the screen with skip, play/pause and paste buttons. " +
            "Turn it off to keep just the screen; the F8 panel always works either way.");
        ConsoleOffset = Config.Bind("Screen", "ConsoleOffsetMeters", 2.8f,
            new ConfigDescription("How far to the right of the screen's centre the console stands. " +
                                  "Negative puts it on the left. The default clears a 4 m screen.",
                new AcceptableValueRange<float>(-15f, 15f)));
        ConsoleHeight = Config.Bind("Screen", "ConsoleHeightMeters", 0.5f,
            new ConfigDescription("Height of the console's panel above the BOTTOM EDGE of the screen, so the " +
                                  "controls follow the screen when it is raised or lowered. It never drops " +
                                  "below 0.35 m off the ground, so sinking the screen does not bury it.",
                new AcceptableValueRange<float>(0f, 3f)));
        ConsoleReach = Config.Bind("Screen", "ConsoleReachMeters", 4f,
            new ConfigDescription("How close you must be for the console's buttons to respond to your aim.",
                new AcceptableValueRange<float>(1f, 20f)));

        RenderWidth = Config.Bind("Screen", "RenderWidth", 1280,
            "Width of the texture the video is decoded into. 1280x720 is plenty for a 4 m screen.");
        RenderHeight = Config.Bind("Screen", "RenderHeight", 720,
            "Height of the texture the video is decoded into.");

        YtDlpPath = Config.Bind("YtDlp", "Path", "",
            "Full path to yt-dlp.exe. Leave empty to use BepInEx\\plugins\\BigScreen\\yt-dlp.exe " +
            "(downloaded automatically when AutoDownload is on).");
        YtDlpAutoDownload = Config.Bind("YtDlp", "AutoDownload", true,
            "Download yt-dlp.exe from its official GitHub releases the first time it is needed.");
        FormatSelector = Config.Bind("YtDlp", "FormatSelector",
            "best[ext=mp4][vcodec^=avc1][acodec!=none][height<=720]/best[ext=mp4][acodec!=none]/18/best[acodec!=none][protocol^=http]",
            "yt-dlp -f expression. Unity's VideoPlayer needs a single progressive MP4 (H.264+AAC) " +
            "that already contains audio, so the default only picks muxed formats. Change with care.");
        ExtractorArgs = Config.Bind("YtDlp", "ExtractorArgs", "youtube:player_client=android",
            "Passed to yt-dlp as --extractor-args. YouTube's default player clients currently return no " +
            "playable formats at all (only storyboard images), which surfaces as 'Requested format is not " +
            "available'. The android client still serves format 18, the muxed H.264+AAC MP4 that Unity's " +
            "VideoPlayer needs. If YouTube blocks this client too, try tv / ios / web_safari / mweb, or " +
            "clear this to use yt-dlp's own defaults.");

        CookiesFromBrowser = Config.Bind("YtDlp", "CookiesFromBrowser", "",
            "Passed to yt-dlp as --cookies-from-browser. Leave empty to request anonymously. " +
            "YouTube blocks repeated anonymous requests from one address with 'Sign in to confirm " +
            "you're not a bot'; naming the browser you watch YouTube in (chrome, firefox, edge, " +
            "brave, vivaldi, opera, safari, chromium) lets yt-dlp reuse your signed-in session and " +
            "clears that. yt-dlp reads that browser's cookie database directly; nothing is sent " +
            "anywhere except to YouTube, exactly as your browser would.");

        DriftTolerance = Config.Bind("Sync", "DriftToleranceSeconds", 1.0f,
            new ConfigDescription("How far your playback may drift from the host's timeline before we re-seek.",
                new AcceptableValueRange<float>(0.25f, 10f)));
        GuestsCanControl = Config.Bind("Sync", "GuestsCanControl", true,
            "Host only: let guests with the mod load videos, play/pause, seek and move the screen. " +
            "On by default: watching together is the point, and only players who also have the mod " +
            "can send anything at all.");
        AutoPlay = Config.Bind("Sync", "AutoPlay", true,
            "Host only: start playing as soon as a loaded video is ready.");
        AutoAdvance = Config.Bind("Sync", "AutoAdvance", true,
            "Host only: roll straight into the next queued item when the current one ends. " +
            "Off leaves the screen paused at the end of each film until someone picks the next one.");

        Diagnostics = Config.Bind("Debug", "Diagnostics", false,
            "Write a verbose status line to the BepInEx log every couple of seconds.");

        PreferredBackend = Config.Bind("Video", "Backend", VideoBackendType.Auto,
            "Video playback backend: Auto (prefers libmpv, falls back to Unity), Mpv, or Unity. " +
            "Mpv supports all video containers (MKV, MP4, WebM), codecs (HEVC, AV1, H.264), and audio formats.");
        MpvPath = Config.Bind("Video", "MpvPath", "",
            "Custom path to mpv-2.dll (Windows) or libmpv.so.2 (Linux). Leave empty for auto-detection.");
        Subtitles = Config.Bind("Video", "Subtitles", true,
            "Show embedded subtitles when a stream carries them. libmpv renders them into the video " +
            "frame, so no extra files are needed. Off if you find them distracting or want more FPS.");
        SubtitleLanguage = Config.Bind("Video", "SubtitleLanguage", "en",
            "Preferred subtitle language (ISO 639-2/B code, e.g. en, eng, spa, fre). Left empty means " +
            "whatever the file marks as its default track.");
        SoftwareFastRender = Config.Bind("Video", "SoftwareFastRender", true,
            "Use libmpv's faster software renderer (sw-fast). libmpv has no GPU context inside the game, " +
            "so frames are converted on the CPU; this trades a little accuracy for frame rate.");
        DemuxerCacheSeconds = Config.Bind("Video", "DemuxerCacheSeconds", 60f,
            "How much video libmpv buffers ahead, in seconds (0 = libmpv default). Raise it on a bad " +
            "connection to smooth out stuttering.");
        HwDecode = Config.Bind("Video", "HwDecode", HwDecodeMode.AutoCopy,
            "How libmpv decodes: AutoCopy decodes on the GPU and copies each frame back to system " +
            "memory (the fastest option this path supports), Auto tries direct hardware decoding, " +
            "No forces software decoding. AutoCopy is the default because this mod has no GPU " +
            "context to hand libmpv, so Auto mostly falls back to software and then fails.");

        TorrentioAddonUrl = Config.Bind("Torbox", "TorrentioAddonUrl", "",
            "Full URL to your Torrentio Stremio addon manifest or base URL. Leave empty to build one " +
            "from Torbox.ApiKey. Do not share this URL or your log file: it contains your debrid token.");
        TorboxApiKey = Config.Bind("Torbox", "ApiKey", "",
            "Your Torbox API key (torbox.app/settings). The mod turns it into a Torrentio addon URL " +
            "itself, so you never have to hand-build one. Leave both this and TorrentioAddonUrl empty " +
            "to disable cloud search. Keep it private: it is your Torbox account.");
        TorboxSort = Config.Bind("Torbox", "SortBy", "",
            "Torrentio sort order for search results: 'quality' (best first), 'qualitysize', " +
            "'size' or 'seeders'. Empty uses Torrentio's own default.");

        AutoHost = Config.Bind("Dev", "AutoHost", false,
            "Host a save slot straight from the main menu, skipping Host Game / slot / player count. " +
            "Development convenience; it drives the game's own menu code and may break on a game update.");
        AutoHostSaveSlot = Config.Bind("Dev", "AutoHostSaveSlot", "",
            "Name of the save slot to host. Empty means the most recently played one. " +
            "If the name does not match, the log lists the slots that were found.");
        AutoHostPlayerCount = Config.Bind("Dev", "AutoHostPlayerCount", 3,
            new ConfigDescription("Player count for the hosted session.", new AcceptableValueRange<int>(2, 4)));
        AutoHostDelay = Config.Bind("Dev", "AutoHostDelaySeconds", 3f,
            new ConfigDescription("How long to wait after the main menu appears before hosting.",
                new AcceptableValueRange<float>(0f, 60f)));

        AutoLoadUrl = Config.Bind("Dev", "AutoLoadUrl", "",
            "Placed and loaded automatically once you are hosting. Empty disables it.");
        AutoDismissMenus = Config.Bind("Dev", "AutoDismissMenus", false,
            "Click through the splash and mic-check screens and open the host menu automatically. " +
            "Needed for an unattended test run; nothing else can get past those screens.");

        SpawnPosition = Config.Bind("Dev", "SpawnPosition", "",
            "Teleport to this pose once after entering a session, as \"x,y,z\" or \"x,y,z,yaw\". " +
            "Empty disables it. Use the panel's 'Set spawn here' button to fill this in from " +
            "where you are standing and which way you are looking. The yaw matters: the screen " +
            "is auto-placed in front of you, so without one it lands somewhere different on " +
            "every run.");

        ShowDevTools = Config.Bind("Dev", "ShowDevTools", false,
            "Show development-only controls in the F8 panel, such as 'Set spawn here'. These write " +
            "to the [Dev] settings above and are of no use in normal play, so they are hidden.");

        // The default is a spot in the starting area, so a fresh install gets a screen there
        // without anyone having to place one. Placing or moving the screen overwrites it.
        ScreenPose = Config.Bind("Dev", "ScreenPose", "-227.06,33.01,-505.14,313.0",
            "Where the host's screen is put on entering a session, as \"x,y,z,yaw\". Placing or " +
            "moving the screen updates this, so it comes back where you left it. The default is " +
            "in the starting area. Empty means no screen until someone places one.");

        DevDirectUrl = Config.Bind("Dev", "DirectUrl", "",
            "Skip yt-dlp and hand this URL straight to the VideoPlayer. Must be a progressive MP4 " +
            "(H.264 + AAC). Use it to test video playback on its own, without running yt-dlp.");

        AutoLoadDelay = Config.Bind("Dev", "AutoLoadDelaySeconds", 6f,
            new ConfigDescription("How long to wait after entering the world before placing the screen and loading.",
                new AcceptableValueRange<float>(0f, 120f)));

        // MonoBehaviours written in managed code must be registered with the IL2CPP
        // domain before Unity will accept them via AddComponent.
        ClassInjector.RegisterTypeInIl2Cpp<BigScreenController>();

        var host = new GameObject("BigScreen");
        host.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<BigScreenController>();

        // Tear down cleanly when the player leaves a lobby. The controller also polls
        // Mirror's state as a fallback in case these hooks are missing after a game update.
        _harmony = new Harmony(Guid);
        TryPatchPostfix(typeof(HouseNetworkManager), "OnStopHost", nameof(Patches.NetworkStoppedPostfix));
        TryPatchPostfix(typeof(HouseNetworkManager), "OnStopClient", nameof(Patches.NetworkStoppedPostfix));

        Log.LogInfo($"{Name} v{Version} loaded. Press {ToggleUiKey.Value} in a lobby to open the panel.");
    }

    private void TryPatchPostfix(Type type, string method, string postfixName)
    {
        try
        {
            var original = AccessTools.Method(type, method, Type.EmptyTypes);
            if (original == null)
            {
                Log.LogWarning($"Could not find {type.Name}.{method} to patch (game update?). Falling back to polling.");
                return;
            }
            _harmony.Patch(original, postfix: new HarmonyMethod(typeof(Patches), postfixName));
        }
        catch (Exception e)
        {
            Log.LogWarning($"Patch of {type.Name}.{method} failed: {e.Message}. Falling back to polling.");
        }
    }

    public override bool Unload()
    {
        BigScreenController.Instance?.ResetSession("plugin unload");
        _harmony?.UnpatchSelf();
        return true;
    }
}

internal static class Patches
{
    internal static void NetworkStoppedPostfix()
    {
        try { BigScreenController.Instance?.ResetSession("network stopped"); }
        catch (Exception e) { Plugin.Log.LogWarning($"Reset on network stop failed: {e.Message}"); }
    }
}

public enum VideoBackendType
{
    Auto,
    Mpv,
    Unity
}

/// <summary>
/// How libmpv decodes video. See <c>Video.HwDecode</c> for what each one means here.
/// </summary>
public enum HwDecodeMode
{
    No,
    Auto,
    AutoCopy,
}
