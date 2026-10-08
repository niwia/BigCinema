# Architecture

## Goal

Several players sit around a screen in the world and watch a YouTube video together: same
picture, same audio, same moment, positional sound. Players without the mod are not affected.

## Decisions and why

**Everyone installs the mod (not host-only).** The screen and the video are rendered locally
on each machine; there is no way to make a vanilla client draw a texture it does not have.
Host-only designs that exist in this community (e.g. replicating fireworks through the game's
own RPCs) work because the *vanilla* game already knows how to draw a firework. It does not
know how to draw our screen.

**Host-authoritative state, not peer-to-peer.** The host already is Mirror's server. One
authority avoids conflicting play/pause/seek commands. Guests send *requests*; the host decides.

**Timeline anchors instead of position streaming.** State carries
`(AnchorNetTime, AnchorVideoTime, Playing)`. Anyone computes the expected position at time
`t` as `AnchorVideoTime + (t - AnchorNetTime)` while playing. `t` is `Mirror.NetworkTime.time`,
which Mirror already synchronises from the server to every client (with RTT compensation). The
host sends state on change and as a 5 s heartbeat; clients re-seek only when their local
position drifts beyond `Sync.DriftToleranceSeconds`. This tolerates jitter and stream buffering
without constant chatter.

**Each player resolves the video URL themselves.** YouTube's direct media URLs are bound to
the requesting IP and expire, so the host shares the *page* URL and each client runs `yt-dlp`
locally. Cost: a few seconds of `yt-dlp` on every machine, and yt-dlp must be kept current
(YouTube breaks old versions; the panel has an "Update yt-dlp" button).

**Unity's VideoPlayer as the first backend.** Zero extra native dependencies, hardware
decoding, and it routes audio into an `AudioSource` so the sound is 3D. Limitation: on Windows
it needs a single progressive MP4 (H.264 + AAC); it cannot play DASH/HLS manifests or
separate audio/video streams. yt-dlp's format `18` (360p muxed MP4) has survived every YouTube
format purge so far and is the reliable floor; the format selector prefers a muxed 720p when
YouTube offers one and falls back to 18.

**A private Mirror message instead of a custom transport.** Mirror's public `Send<T>` /
`RegisterHandler<T>` require message structs baked into the game's weaver tables, which a mod
cannot extend. But those generics only do two things: write a 16-bit id before the payload and
look the id up in a `Dictionary<ushort, NetworkMessageDelegate>`. Both the dictionary and the
raw `Send(ArraySegment<byte>)` are exposed by the interop assemblies, so we do exactly that.
Message id `2356` = fold16(fnv1a32("BigScreen.SyncMessage")), Mirror's own derivation.

**IMGUI for the panel.** It needs no assets, and it is what every other Big Walk overlay uses.

## Wire protocol

All messages: `[u16 messageId=2356][u8 protocolVersion=1][u8 kind][payload]`, reliable channel,
serialised with Mirror's own `NetworkWriterExtensions` primitives.

| Kind | Direction | Payload |
| --- | --- | --- |
| Hello (1) | guest -> host | `string modVersion` |
| State (2) | host -> guest | `int revision, bool hasScreen, Vector3 pos, float yaw, string pageUrl, string title, bool playing, double anchorNetTime, double anchorVideoTime, bool guestsCanControl` |
| Request (3) | guest -> host | `u8 action, string text, double value, Vector3 pos, float yaw` |

Sequence:

```text
guest connects, Mirror marks it ready
guest  --Hello-->  host          host records the connection as "modded", replies with State
host   --State-->  modded guests on every change (revision++), and every 5 s as heartbeat
guest  --Request-> host          only honoured if GuestsCanControl; host mutates, broadcasts State
```

The host never sends to vanilla clients (they never said Hello). If a vanilla client somehow got
one, Mirror logs "Unknown message id" and continues; it does not disconnect.

## Local reconciliation (same code on host and guests)

Every frame, `BigScreenController` makes the local world match `SyncState`:

1. `HasScreen` true and no local screen: build `ScreenObject` at the broadcast pose.
2. `VideoUrl` differs from what is loaded: cancel any resolve in flight, run yt-dlp off-thread,
   then `Load(directUrl)` on the backend.
3. Backend ready: seek to the expected position once, then keep `Play/Pause` matching `Playing`
   and re-seek when drift exceeds tolerance (rate limited to one seek per 2.5 s).
4. Expected position past the end: pause; the host also flips `Playing=false` in the state.
5. Leaving the lobby (Harmony hook on `HouseNetworkManager.OnStopHost/OnStopClient`, plus a
   polling fallback): destroy the screen, forget state, clear peers.

## Risks: the three things most likely to need adjusting after the first launch

### Risk 1: Unity's VideoModule may be stripped from the build

> **DID NOT HAPPEN — verified 2026-09-19.** `UnityEngine.VideoModule.dll` is present in the
> generated interop (81 KB). Better still, the game *itself* uses Unity's VideoPlayer:
> `VideoPlayerAudioAssigner` and `PeckEffectPipeVideoAudio` exist in `Assembly-CSharp`, which
> is presumably why the module survived stripping. The ffmpeg backend below is therefore not
> needed for v0.1 and stays a future HD option rather than a fallback.

IL2CPP builds strip engine modules the game never uses. If `BepInEx\interop\UnityEngine.VideoModule.dll`
does not exist, `UnityEngine.Video.VideoPlayer` does not exist at runtime and the Unity backend
cannot work. The build prints a warning if the file is missing.

Fallback plan (already designed for, not yet implemented): `FfmpegVideoBackend` behind the same
`IVideoBackend` interface. Spawn `ffmpeg.exe` with `-i <streamUrl> -f rawvideo -pix_fmt rgba
-s 1280x720 pipe:1` for video, read frames from stdout on a background thread, upload each frame
with `Texture2D.LoadRawTextureData` + `Apply()` on the main thread; a second process outputs
`-f s16le -ac 2 -ar 48000` PCM which is fed into back-to-back `AudioClip`s scheduled with
`AudioSource.PlayScheduled` (the callback-free way to stream audio through Unity). This backend
also removes the muxed-MP4 limitation (ffmpeg decodes anything yt-dlp returns, including
separate 1080p video + audio streams), at the cost of shipping/downloading ffmpeg (~100 MB) and
software decoding on the CPU. If Risk 1 materialises, this becomes the primary backend.

### Video backends as they now stand (update needed after the libmpv work)

The ffmpeg plan above was superseded: `Video/MpvVideoBackend.cs` uses libmpv through
`Video/Mpv/MpvNative.cs` instead, and it is the default when a library is found. It accepts
every container and codec yt-dlp (or a debrid service) can hand over, which is the change that
made Torbox usable at all - most of that library is MKV/HEVC.

Two facts about it are worth stating plainly, because they are the reason it is not the last
word:

1. It renders through libmpv's **software** render context (`MPV_RENDER_API_TYPE_SW`). There is
   no GPU context inside the game, so there is no hardware decoding: libmpv's own headers call
   that renderer "extremely slow, everything including color conversion, scaling and OSD is done
   on the CPU, single-threaded". `sw-fast` and a bounded demuxer cache are what keep it
   real-time at 720p. Hardware decoding needs an OpenGL render context sharing Unity's GL
   context, which is the obvious next step and a real chunk of work.
2. Its audio does not go through Unity's `AudioSource`; libmpv outputs directly and the backend
   feeds it distance and pan every frame. Positional audio therefore exists in both paths, but
   only the Unity backend respects the game's own mixer and volume stack.

`UnityVideoBackend` remains the fallback, still limited to a progressive muxed MP4 (hence the
yt-dlp `FormatSelector` default).

### Why not a zero-copy OpenGL render context

The first question anyone asks about the software renderer is "why not hand libmpv a real
OpenGL context and let it hardware-decode into a texture?" It is the right question, and the
answer came out of `mpv/client.h` and `mpv/render_gl.h`:

- `mpv_render_context_create` with `MPV_RENDER_API_TYPE_OPENGL` needs
  `MPV_RENDER_PARAM_OPENGL_INIT_PARAMS`, whose only real member is a `get_proc_address`
  function pointer. libmpv resolves no GL functions itself and links to no GL library, so
  without one there is no context. A managed delegate passed through
  `Marshal.GetFunctionPointerForDelegate` can serve as that pointer (CoreCLR supports it and
  the delegate is kept alive), so this part is solvable - it just is not the hard part.
- mpv requires the context to be current on the calling thread for *every*
  `mpv_render_context_*` call. Unity makes its GL context current on its render thread, not
  on the thread MonoBehaviour code runs on, so rendering has to be driven from inside the
  render pass.
- The frame has to land somewhere. `mpv_render_context_render` writes to an FBO named by
  `MPV_RENDER_PARAM_OPENGL_FBO`, and Unity exposes no way to create or name a raw GL
  framebuffer from managed code - so a shim is needed to make the FBO and to blit the result
  into the `RenderTexture` the screen already uses.
- On Windows mpv's GL path additionally requires **ANGLE**; Unity on Windows runs D3D11/D3D12,
  so the two GL contexts would not even be the same kind of context.

That is a small native library plus render-thread plumbing plus a Windows-specific GL stack,
for a gain that partly evaporates anyway: `hwdec=auto-copy` already removes the decode cost,
which is the expensive half. What is left on the CPU is colour conversion and scaling of one
frame per refresh cycle, which `sw-fast` handles. So the GPU path is deferred rather than
skipped, and the obstacle to record is the FBO, not the curiosity.

### Torbox / Torrentio

`Torbox/TorrentioClient.cs` searches Cinemeta, then asks the user's Torrentio addon for the
streams it holds. Configuration is either a Torbox API key (from which the mod builds the addon
URL itself) or a full addon URL. The host resolves, and only the finished direct stream URL goes
into the shared state, so guests need no debrid account - at the price of every guest downloading
independently from the same debrid CDN, which is where a shared LAN relay would pay off.

### Risk 2: hooking Mirror's handler table

> **PARTIALLY HAPPENED — verified 2026-09-19.** Registration works; both server and client
> handlers install successfully. But the delegate signature guess was wrong in the way this
> section anticipated: Big Walk's Mirror needs the **legacy `NetworkConnection`** first
> parameter, not the modern `NetworkConnectionToClient`. `MirrorChannel.Convert` tries modern
> first and falls back, and the fallback is what fires:
>
> ```
> Modern delegate signature rejected (... Mirror.NetworkConnection !=
> Mirror.NetworkConnectionToClient); trying legacy signature.
> Server message handler registered.
> ```
>
> Without that fallback path sync would not work at all. Keep both attempts. The Dissonance
> text-chat fallback below was not needed. See `docs/MIRROR-MESSAGING.md`.

`NetworkServer.handlers` / `NetworkClient.handlers` and `NetworkMessageDelegate` are Mirror
internals. They have been stable since 2021 but a Mirror upgrade inside a Big Walk patch could
rename them or change the delegate's first parameter type (`NetworkConnectionToClient` today,
`NetworkConnection` in older versions; the code tries both). If registration fails the log says
so and sync is disabled while the rest of the mod still works locally.

Fallback plan: Dissonance (the voice library the game ships) has a text-chat facility:
`DissonanceComms.Text.Send(room, message)` with `Text.MessageReceived`. All modded clients could
join a private room name and exchange the same payload as strings. Less clean, but independent
of Mirror internals.

### Risk 3: shaders and primitive meshes

> **DID NOT HAPPEN — verified 2026-09-19.** `Shader.Find` succeeded on the third candidate:
> `Screen shader: Sprites/Default`. No material cloning was required, the hand-built meshes
> render, and the screen is created and destroyed cleanly (`Reset (left lobby)`). The two
> URP candidates ahead of it in the list do not exist in this build, so leave `Sprites/Default`
> in the list even if the ordering is revisited.

Only shaders the game itself references survive the build. `ScreenObject` tries a list
(`Universal Render Pipeline/Unlit`, `Unlit/Texture`, `Sprites/Default`, ...) and, if none exists,
clones a material already rendering in the scene so the pipeline is guaranteed to accept it.
Meshes are built by hand because `GameObject.CreatePrimitive` depends on built-in resources
that may be absent. If the screen appears but is black/pink, this is where to look; the log
prints which shader was used. UnityExplorer will show you which shader the game's own flat
surfaces use; add that name to the top of the candidate list.

## Decisions taken during the first real run (2026-09-19)

These were not anticipated in the original design. Each one is a rule, not just a patch.

### Unity 6 "injected" string bindings cannot be called through the interop proxy

Unity 6 marshals strings for its internal-call bindings through a `ManagedSpanWrapper`.
Il2CppInterop reproduces that code in its generated proxies, and the reproduction calls
`Il2CppSystem.ReadOnlySpan<char>.GetPinnableReference()`. That method exists in the generated
metadata but IL2CPP inlined the real one away, so there is no callable native counterpart and
the proxy throws at runtime:

```
MissingMethodException: Method not found:
  '!0 ByRef Il2CppSystem.ReadOnlySpan`1.GetPinnableReference()'
   at UnityEngine.Video.VideoPlayer.set_url(String value)
```

**This is a pattern, not a one-off.** Any Unity API whose proxy resolves an `_Injected`
internal call *and takes a string* will fail the same way. `VideoPlayer.url` is simply the
first one we happened to need. Symptoms to recognise: a `MissingMethodException` naming a span
method, thrown from inside a property setter you did not write.

The workaround, in `UnityVideoBackend.SetUrl`: resolve the same internal call ourselves and
hand it the 16-byte `{ IntPtr begin; int length }` struct it expects, built by pinning a
managed string in our own assembly (where the real .NET 6 BCL applies and the problem does not
exist). Ordinary IL2CPP methods that take strings — `Shader.Find`, `Material.SetTexture` — are
unaffected and need no special handling.

### yt-dlp needs an explicit player client

YouTube now returns **no media formats at all** to yt-dlp's default player clients — only
storyboard images — which surfaces misleadingly as "Requested format is not available". No
format selector can fix that, and neither can updating yt-dlp.

`YtDlp.ExtractorArgs` (default `youtube:player_client=android`) is passed as
`--extractor-args`. It is configuration rather than a constant because YouTube rotates which
clients it starves; when `android` stops working, `tv` / `ios` / `web_safari` / `mweb` are the
alternatives to try, and a config edit beats a rebuild.

### We declare the nullable attributes ourselves

Il2CppInterop emits `NullableAttribute` and `NullableContextAttribute` into
`UnityEngine.CoreModule.dll`. Because we reference that assembly, Roslyn binds to those when
emitting metadata for `async` lambdas, and they lack the constructors it needs (`CS0656`).
`Util/CompilerShims.cs` declares both — a declaration inside the compilation wins. Do not
delete it because it looks unused; nothing references it directly, the compiler emits it.

### Toolchain is pinned, not floating

.NET SDK 10 locally, CI bumped to match (`10.0.x`), and `<LangVersion>` pinned to `12.0`
instead of `latest` so the accepted C# version cannot drift with whoever's SDK is newest.
`TargetFramework` stays `net6.0` — that is not a stale choice, it is what BepInEx 6 IL2CPP
hosts plugins on, confirmed by the loader's own boot line (`Runtime version: 6.0.7`).

## Smaller known gaps

- **Format availability.** If YouTube drops format 18 entirely, VideoPlayer has nothing muxed to
  play and only the ffmpeg backend can help. Watch yt-dlp's issue tracker.
- **Live streams** have no duration and infinite timelines; untested and probably need the
  drift logic relaxed.
- **Seek latency.** `VideoPlayer.time = x` on a network stream can take a second to settle; the
  2.5 s seek rate limit exists so we do not thrash. Tune `DriftToleranceSeconds` if sync feels
  loose.
- **Scene changes.** The screen is `DontDestroyOnLoad`; if Big Walk unloads/reloads sub-scenes
  around the player, the screen persists, which is what we want, but its position could end up
  inside re-streamed geometry.
- **Dark screen at night?** Unlit shaders ignore lighting so the picture is visible at night. If
  we end up on a Lit shader clone, emission is enabled so it still glows.

## Roadmap

1. **v0.1 (this scaffold):** compile against the game, verify the PLAYTEST.md checklist.
2. **v0.2:** ffmpeg backend if Risk 1 bites, or as an opt-in HD mode if it does not. A tiny
   playlist / queue in the state. Persist the last screen position per save.
3. **v0.3:** optional `ModSettingsMenu` integration (the community settings UI) so config is
   editable in-game; a Thunderstore release.
4. **Later:** a physical "remote control" prop, chapter markers, subtitles via yt-dlp
   `--write-subs`, a second screen.
