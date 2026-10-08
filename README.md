# BigScreen

A shared, in-world screen for [Big Walk](https://store.steampowered.com/app/1478500/Big_Walk/).
The host places a screen somewhere on the island, pastes a YouTube link, and everyone with
the mod sees and hears the same video at the same moment. Sit around it, heckle, walk off
and let the sound fade behind you.

> **Status: alpha, runs in-game.** As of 2026-09-19 the mod compiles against Big Walk
> (Unity 6000.3.17f1, BepInEx 6.0.0-be.755), loads, hooks Mirror, and places a working screen
> that syncs and tears down correctly. Video playback is the remaining unverified step.
> All three risks in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) are now resolved or retired
> and annotated with what actually happened; [docs/PLAYTEST.md](docs/PLAYTEST.md) tracks what
> is still unverified.

## How it works, in one paragraph

Big Walk is a Unity (IL2CPP) game networked with Mirror. BigScreen is a BepInEx plugin.
Every player who wants to watch installs it. The host owns a tiny piece of shared state
(where the screen is, which video, playing or paused, and a timeline anchor); it is sent
to modded guests over the game's own Mirror connection on a private message id. Each
player resolves the YouTube link to a direct stream URL locally with `yt-dlp` and plays it
with Unity's built-in VideoPlayer onto a quad in the world, with positional audio. Everyone
keeps their playback locked to the host's timeline using Mirror's shared clock. Players
without the mod are unaffected: they see nothing and receive nothing.

## Requirements

- Big Walk on Steam (Windows, or Linux through Proton)
- [BepInExPack IL2CPP](https://thunderstore.io/c/big-walk/p/BepInEx/BepInExPack_IL2CPP/)
  (installed for you by a mod manager such as Gale or r2modman)
- `yt-dlp.exe`. BigScreen downloads it automatically the first time it is needed
  (from the official yt-dlp GitHub releases), or set `YtDlp.Path` in the config.

## Install

**With a mod manager (recommended):** in Gale or r2modman, select Big Walk, create a profile,
install `BepInExPack_IL2CPP`, then import the BigScreen zip (or install it from Thunderstore
once it is published). Launch the game from the manager.

**Manually:** install BepInExPack IL2CPP into the game folder, launch the game once (the first
launch takes a few minutes while it generates interop files), then drop `BigScreen.dll` into
`BepInEx\plugins\BigScreen\`.

Everyone who wants to watch needs the mod. The host does not need to do anything special.

## Use

1. Host or join a walk.
2. Press **F8** to open the panel.
3. **Place screen here** puts a screen 4 m in front of you, facing you.
4. Paste a YouTube URL and press **Load**. Everyone with the mod resolves and starts the
   video in sync a few seconds later.
5. Play/pause and seek from the panel. Guests can only control playback if the host ticks
   "Let modded guests control playback".
6. **Queue up what to watch next** while something is playing. Paste a URL and press **Queue**,
   or press **Queue** beside a Torrentio stream. The list is shared: everyone sees it, anyone
   with control can reorder-play or remove from it, and the next film rolls in by itself when
   the current one ends (`Sync.AutoPlay` / `Sync.AutoAdvance`).

The panel also has an optional **Torbox Cloud / Torrentio** tab, set up in one of two private
ways:

1. **Torbox API key (recommended)** - paste your key from `torbox.app/settings` into the panel's
   Torbox tab (or `Torbox -> ApiKey` in the config). The mod builds the same addon URL the
   Torrentio site builds for that key, so there is nothing to assemble by hand. Guests never need
   Torbox: they receive the finished direct stream URL from the host and just play that.
2. **Full addon URL** - paste the manifest or base URL from the Torrentio configuration page into
   `Torbox -> TorrentioAddonUrl`. This takes precedence over the key, and is the way to use
   RealDebrid, Premiumize or AllDebrid instead of Torbox.

Both are credentials: never share them, your `BepInEx.cfg`, or your `LogOutput.log`. Without a
debrid account Torrentio only returns magnet links, which nothing in the game can open; the panel
says so explicitly when that happens, because a rejected key otherwise looks exactly like
"no results found".

Subtitles embedded in the file are shown by default (`Video -> Subtitles`).

Your own volume slider is local. The audio is positional: stand close to hear it, walk away
and it fades (configurable range).

## libmpv

The libmpv backend is what plays the files Torbox hands you, which are usually MKV and often
HEVC - Unity's VideoPlayer can only play a muxed H.264/AAC MP4, so without libmpv most of the
library is unavailable. The mod looks for it in this order:

1. `Video.MpvPath` from the config, if it exists.
2. `mpv-2.dll` (Windows) or `libmpv.so.2` (Linux/macOS) next to `BigScreen.dll`.
3. The usual system locations: `mpv-2.dll` on PATH, `/usr/lib/libmpv.so.2`, and friends.

**Packaging for Windows** bundles `mpv-2.dll` by default, so testers get a working install with
no extra step. To build that bundle yourself:

```powershell
# Once: get the DLL from https://mpv.io/installation/ (the installer ships mpv-2.dll) or
# from a release build, and drop it in deps\mpv-2.dll
# Then: packaging picks it up automatically
.\scripts\package.ps1
# ...or point at one you already have
.\scripts\package.ps1 -LibmpvPath C:\path\to\mpv-2.dll
# Package without it (players fall back to Unity's VideoPlayer)
.\scripts\package.ps1 -SkipLibmpv
```

`deps/` is gitignored: the binary is ~60-80 MB, so it is downloaded once per machine (or per CI
run from the `mpv` release asset) rather than committed.

**Linux** needs the distro package instead:

```bash
sudo apt install libmpv2        # or libmpv.so.2 from your distro
```

## Configuration

`BepInEx\config\dev.h223chen.bigscreen.cfg` (created on first launch). Highlights:

| Section | Key | Default | Meaning |
| --- | --- | --- | --- |
| Keys | ToggleUI | F8 | Open/close the panel |
| Audio | Volume / MaxDistance | 0.8 / 25 | Local volume; distance at which the screen goes silent |
| Screen | WidthMeters | 4 | Physical screen width (16:9) |
| Video | Backend / MpvPath | Auto / "" | Prefer libmpv when available; optionally point at `mpv-2.dll` or `libmpv.so.2` |
| Video | HwDecode | AutoCopy | GPU decode with a copy back into memory (see below), direct hardware decode, or off |
| Video | Subtitles / SubtitleLanguage | true / en | Show embedded subtitles; preferred language |
| Video | SoftwareFastRender | true | libmpv's faster software renderer (this render path is CPU-only) |
| Video | DemuxerCacheSeconds | 60 | How much video is buffered ahead; raise it on a bad connection |
| YtDlp | Path / AutoDownload | "" / true | Where yt-dlp.exe lives; download it if missing |
| YtDlp | FormatSelector | muxed-MP4 chain | yt-dlp `-f` expression. Unity needs a single H.264+AAC MP4 |
| Torbox | ApiKey | "" | Your Torbox API key; the mod builds the Torrentio addon URL from it |
| Torbox | TorrentioAddonUrl | "" | Full addon URL instead of a key (RealDebrid and friends). Keep it private |
| Torbox | SortBy | "" | Torrentio result order: quality, qualitysize, size or seeders |
| Sync | DriftToleranceSeconds | 1.0 | Re-seek when you are further than this from the host's timeline |
| Sync | GuestsCanControl / AutoPlay | true / true | Host-side permissions |
| Sync | AutoAdvance | true | Roll into the next queued item when the current one ends |
| Debug | Diagnostics | false | Verbose log line every 2 s |

## Building from source

**Prerequisites:**
1. [.NET SDK 10](https://dotnet.microsoft.com/download) or newer
2. A mod manager: [Gale](https://github.com/minotaurmoon/Gale) or [r2modman](https://thunderstore.io/tools/r2modman/)
3. [BepInExPack_IL2CPP](https://thunderstore.io/c/big-walk/p/BepInEx/BepInExPack_IL2CPP/) installed via your chosen mod manager
4. Big Walk launched modded at least once so that `BepInEx\interop` exists

**Setup:**

```powershell
# 1. Install and launch the game modded through Gale or r2modman
#    (this generates the interop files you need to build)

# 2. Verify .NET SDK is installed
dotnet --version

# 3. Build and deploy the mod
.\scripts\build.ps1 -Deploy    # build and copy into <BepInEx>\plugins\BigScreen

# Alternative: create a Thunderstore-ready package
.\scripts\package.ps1          # creates dist\BigScreen-*.zip
```

The build auto-detects the Gale profile, the r2modman profile, and the Steam game folder in
that order. For a custom BepInEx path: copy `Config.Build.user.props.template` to
`Config.Build.user.props` and set `BepInExPath`.

New to Unity or game modding? Start with [docs/MODDING-PRIMER.md](docs/MODDING-PRIMER.md).

## Docs

- [docs/MODDING-PRIMER.md](docs/MODDING-PRIMER.md): how Big Walk modding works, the
  toolchain, the dev loop, publishing.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): design, sync protocol, risks, roadmap.
- [docs/MIRROR-MESSAGING.md](docs/MIRROR-MESSAGING.md): how the networking works and why it
  bypasses Mirror's public message API, for readers new to Mirror.
- [docs/TASK_LIST.md](docs/TASK_LIST.md): planned improvements, and what the game's own
  interaction systems offer.
- [docs/PLAYTEST.md](docs/PLAYTEST.md): what to verify on the first in-game runs.

## Credits

Built on the shoulders of the Big Walk modding community, in particular
[dougwithseismic/bigwalk-mods](https://github.com/dougwithseismic/bigwalk-mods) (toolchain and
reverse-engineering guide), [iameli/big-walk-practice](https://github.com/iameli/big-walk-practice)
(Mirror ownership notes, CI pattern) and the Trifocals mods (in-game patterns for
players, audio and effects). BepInEx, Il2CppInterop, Harmony, Mirror and yt-dlp do the heavy
lifting. Not affiliated with House House or Panic.

## License

MIT. See `LICENSE`.
