# Changelog

## 1.0.6

### Added
- Torbox setup no longer needs a hand-built addon URL: `Torbox.ApiKey` holds the key from
  torbox.app/settings and the mod constructs the same URL the Torrentio configuration page
  constructs, so the API key field in the panel is the whole setup. `TorrentioAddonUrl`
  still wins when set, which keeps RealDebrid / Premiumize / AllDebrid usable, and
  `Torbox.SortBy` orders results by quality, qualitysize, size or seeders.
- Embedded subtitles play through libmpv (`Video.Subtitles`, `Video.SubtitleLanguage`).
- libmpv buffers ahead: `Video.DemuxerCacheSeconds` sets how much video is held, and
  `Video.SoftwareFastRender` turns on libmpv's `sw-fast` path, which is what keeps the
  single-threaded software renderer real-time.
- Cinemeta searches are cached for five minutes, since a lobby deciding what to watch types
  the same titles repeatedly and Cinemeta is rate limited.

### Added
- **Shared queue.** Anything can be queued while something else is playing - paste a URL and press
  Queue, or press Queue next to a Torrentio stream - and the list is host-owned state like the
  screen itself, so everyone sees the same thing. Items can be played out of order or removed by
  anyone the host trusts, the item playing is marked, and `Sync.AutoAdvance` rolls into the next
  one when the current film ends.
- **libmpv is bundled into the package.** `package.ps1` now packs `mpv-2.dll` by default (from
  `deps/`, the dev profile, or the repo's `mpv` release asset), with a size sanity check so a
  truncated download cannot ship. `-SkipLibmpv` and `-LibmpvPath` exist for the exceptions. This
  matters more than its size suggests: without libmpv a Windows tester silently falls back to
  Unity's VideoPlayer, which cannot play the MKV/HEVC files Torbox returns at all.
- **GPU decoding (`Video.HwDecode`).** `AutoCopy` is the default: libmpv decodes on the GPU and
  copies finished frames back into ordinary memory, which is the only hardware path that works
  when the embedder has no GPU context to offer. `Auto` (direct hwdec) and `No` are still there.
  The panel now says "GPU decode + software blit" instead of claiming hardware acceleration.
- A real test project: `tests/BigScreen.Tests` covers the queue rules and the state wire format
  with no game references (29 assertions, run in CI).

### Changed
- The sync protocol is version 3. The state message gained the queue and the auto-advance flag,
  so a 1.0.5 peer is rejected with the existing protocol-version warning rather than misreading
  the trailing bytes. Both players need to be on this version.

### Fixed
- The Torbox token was written into the log. Torrentio carries the debrid key in the URL
  *path*, not the query, so stripping the query was not enough; credential-shaped options
  are redacted now and the query and fragment are dropped whole. Anyone who pasted a
  BepInEx log while debugging had their key in it.
- "No streams found" is no longer the answer for a rejected Torbox key. A result made of
  magnet links is now reported as exactly that, because a key the addon did not accept and
  a genuinely missing file look identical from here.
- yt-dlp is marked executable after download on Linux and macOS. The zip ships it 644, the
  mod writes it 644, and the first resolve then died with "Permission denied" on a native
  Linux install - the install most likely to hit it is a non-Windows one.
- The decoder line claimed "HW Accel". libmpv's software render context has no GPU context,
  and libmpv's own headers describe it as deliberately slow and CPU-only; it now says
  "libmpv (software render)".
- `MpvNative.IsAvailable` loads and unloads the native library, and the panel called it once
  per redraw. The answer is cached and refreshed a couple of times a second.

## 1.0.5

### Changed
- A fresh install now puts the host's screen in the starting area on entering a session.
  `Dev.ScreenPose` defaults to a spot there instead of empty, so no one has to press
  **Place screen here** before there is something to paste a link at. Existing configs keep
  whatever value they already hold.
- The Thunderstore package ships `thunderstore/README.md`, a player-facing listing, instead of
  the developer README.

## 1.0.4

### Fixed
- `package.ps1` wrote `manifest.json` with a UTF-8 BOM: `Set-Content -Encoding UTF8` means
  "with BOM" on Windows PowerShell 5.1. Thunderstore accepted it anyway, so this was never
  the upload failure it was first taken for - but a BOM has no business in a JSON file, and
  every package built before this carried one. Written through `WriteAllText` with an
  explicit no-BOM encoder now.

## 1.0.3

Both players must be on this version: the sync message gained two fields, so the protocol
version went to 2 and a 1.0.2 peer is rejected outright rather than misreading the message.

### Changed
- Screen geometry (`GroundClearance`, `WidthMeters`) is now part of the shared state and
  owned by the host, the way position and yaw already were. A guest joining a lobby renders
  the host's screen at the host's height and width instead of their own, so everyone is
  looking at the same thing. Your own config still decides the screen for lobbies YOU host.
- Raise/Lower is a host control now, and moves the screen for the whole lobby. A guest
  pressing it is told the host sets the height; the panel shows the shared value.
- `GroundClearance` now defaults to -0.3 rather than 0.6, which puts the screen near the
  ground and hides the stand. A fresh install and a tuned one no longer disagree by almost a
  metre. Existing configs keep whatever value they already hold - BepInEx does not rewrite a
  setting that is already there, so a host on the old default still needs to lower theirs
  once.

## 1.0.2

### Fixed
- Guests never received anything from the host, so the screen only ever existed on the
  hosting machine. Mirror's message delegate hands us the connection typed as
  `NetworkConnection`, and the legacy-signature path cast it with `as`, which compares IL2CPP
  interop wrapper types rather than the native type and so produced `null`. The host then
  dropped every guest's Hello without logging anything, and with no peer registered it never
  broadcast state. Uses `TryCast` now, and a Hello that still arrives without a connection
  says so in the log instead of vanishing.

  Only reachable with two machines: a solo host never sends itself a Hello, so no amount of
  single-machine testing could have hit it.

## 1.0.1

Comfort tweaks for actually sitting and watching something, rather than standing in front of
a screen fighting the game's idle handling.

### Added
- `Comfort.KeepAwake`, which holds off the game's idle sleep - the dimming that creeps in
  when you stand still - while a video is playing and you are within
  `Comfort.KeepAwakeRadius` (15 m) of the screen. Sleep still works normally away from the
  screen. Set it to `Always` to hold it off for the whole lobby, or `Off` for stock
  behaviour. Local only: it resets your own idle timer and changes nothing for other
  players.
- `Comfort.HideCrosshair`, which takes the crosshair off the picture once you have stood
  still for `Comfort.HideCrosshairDelaySeconds` (5 s), and brings it back the instant you move
  or look around. Same conditions as `KeepAwake`: only in front of a playing screen, and only
  within `Comfort.KeepAwakeRadius`. Set the delay to 0 to follow the game's own sleep timer
  instead.

## 1.0.0 - alpha

First version verified end to end in game: a YouTube link plays on a shared screen with
positional audio, driven from an in-world control podium.

### Added
- In-world control console beside the screen: skip back/forward 10 s, play/pause, and a
  paste bar that loads whatever YouTube link is on the clipboard. Aimed at and pressed with
  the game's own interact button, on mouse or controller.
- Status lamp on the console: grey idle, amber while resolving, green ready, red failed.
  Colour is the only feedback channel available - the mod ships no font.
- Screen positioning from the panel: raise/lower, and slide left/right/forward/back along
  the screen's own axes. Placement is remembered across restarts.
- `YtDlp.CookiesFromBrowser`, for when YouTube blocks anonymous requests with
  "Sign in to confirm you're not a bot".

### Changed
- Audio falls off as 1/distance rescaled to reach silence at `MaxDistance`, instead of
  linear. Standing 4 m from the screen was previously 97% of full volume.
- Guests with the mod can control playback by default.

### Known limitations
- YouTube serves only one muxed format to the client yt-dlp can still reach, so playback is
  capped at 360p. Higher resolutions are separate video and audio streams, which Unity's
  VideoPlayer cannot take. Lifting this needs the ffmpeg backend in `docs/ARCHITECTURE.md`.
- Two modded players staying in sync has not been tested; there has only ever been one
  machine. See milestone 3 in `docs/HANDOFF.md`.

## 0.1.0 (unreleased)

- Initial scaffold: BepInEx 6 IL2CPP plugin, Mirror-based sync channel, yt-dlp resolver,
  Unity VideoPlayer backend, in-world screen with positional audio, IMGUI control panel.
- Not yet verified in-game. See docs/PLAYTEST.md.
