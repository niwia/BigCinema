using System;
using System.Globalization;
using System.Runtime.InteropServices;
using BigScreen.Video.Mpv;
using UnityEngine;

namespace BigScreen.Video;

/// <summary>
/// Universal playback backend powered by libmpv (mpv-2.dll on Windows/Proton, libmpv.so.2 on Linux).
/// Plays all modern containers (MKV, MP4, WebM), video codecs (HEVC, AV1, H.264, 10-bit HDR/DV),
/// and audio formats (AAC, AC3, EAC3, DTS, Opus, TrueHD) using GPU hardware acceleration.
///
/// Features dynamic 3D positional audio tracking based on distance and orientation relative to the screen.
/// </summary>
internal sealed class MpvVideoBackend : IVideoBackend
{
    private readonly GameObject _host;
    private readonly AudioSource _audio;
    private readonly RenderTexture _rt;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly int _bufferSize;
    private readonly IntPtr _pixelBuffer;
    private readonly Texture2D _texture;

    private IntPtr _mpv = IntPtr.Zero;
    private IntPtr _renderContext = IntPtr.Zero;

    private bool _loaded;
    private bool _hasFirstFrame;
    private double _knownDuration;
    private float _volume = 1f;
    private string _error;
    private bool _disposed;

    public MpvVideoBackend(GameObject host, AudioSource audio, RenderTexture target)
    {
        _host = host;
        _audio = audio;
        _rt = target;

        _width = target != null ? target.width : 1280;
        _height = target != null ? target.height : 720;
        _stride = _width * 4;
        _bufferSize = _stride * _height;

        _pixelBuffer = Marshal.AllocHGlobal(_bufferSize);
        _texture = new Texture2D(_width, _height, TextureFormat.RGBA32, false)
        {
            name = "BigScreen.MpvTex",
            filterMode = FilterMode.Bilinear
        };
    }

    public Texture Texture => _rt;
    public bool IsLoaded => _loaded;
    public bool IsReady => _loaded && _error == null && (_hasFirstFrame || Duration > 0.1);
    public bool IsPlaying => _loaded && _error == null && !MpvNative.GetPropertyBool(_mpv, "pause", true);

    public double Time
    {
        get
        {
            if (_mpv == IntPtr.Zero || !_loaded) return 0.0;
            return MpvNative.GetPropertyDouble(_mpv, "time-pos", 0.0);
        }
    }

    public double Duration
    {
        get
        {
            if (_mpv == IntPtr.Zero || !_loaded) return 0.0;
            double d = MpvNative.GetPropertyDouble(_mpv, "duration", 0.0);
            return d > 0.01 ? d : _knownDuration;
        }
    }

    public string Error => _error;

    private void EnsureInitialized()
    {
        if (_mpv != IntPtr.Zero) return;

        _mpv = MpvNative.mpv_create();
        if (_mpv == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create libmpv instance.");

        // Safe options before initialize
        MpvNative.mpv_set_option_string(_mpv, "hwdec", HwDecodeValue());
        MpvNative.mpv_set_option_string(_mpv, "vo", "libmpv");
        MpvNative.mpv_set_option_string(_mpv, "keep-open", "yes");
        MpvNative.mpv_set_option_string(_mpv, "idle", "yes");
        MpvNative.mpv_set_option_string(_mpv, "ytdl", "no");
        MpvNative.mpv_set_option_string(_mpv, "audio-pitch-correction", "yes");
        MpvNative.mpv_set_option_string(_mpv, "hr-seek", "yes");
        MpvNative.mpv_set_option_string(_mpv, "reset-on-next-file", "pause");
        // The software renderer does color conversion, scaling and OSD/subtitle compositing
        // on one CPU thread; sw-fast is libmpv's documented way to cut that cost.
        MpvNative.mpv_set_option_string(_mpv, "sw-fast",
            Plugin.SoftwareFastRender != null && Plugin.SoftwareFastRender.Value ? "yes" : "no");
        // Enough buffering to ride out a wobble on someone's connection instead of
        // stuttering; seconds are more intuitive than mpv's byte-based demuxer cache.
        float cacheSeconds = Plugin.DemuxerCacheSeconds?.Value ?? 0f;
        if (cacheSeconds > 0f)
            MpvNative.mpv_set_option_string(_mpv, "demuxer-max-bytes",
                ((int)Mathf.Clamp(cacheSeconds, 1f, 300f) * 1024 * 1024).ToString());
        ApplySubtitleOptions();

        int initErr = MpvNative.mpv_initialize(_mpv);
        if (initErr < 0)
            throw new InvalidOperationException($"mpv_initialize failed: {MpvNative.GetError(initErr)}");

        InitRenderContext();
    }

    /// <summary>
    /// Subtitle setup. libmpv's software renderer composites OSD and subtitles into the frame,
    /// so this needs no extra rendering work; the trade-off is CPU time, which is why
    /// Video.Subtitles exists.
    ///
    /// These must be set before mpv_initialize(): after that the options are frozen and the
    /// values we actually want on a loaded file (the track id) go through properties instead.
    /// </summary>
    private void ApplySubtitleOptions()
    {
        if (_mpv == IntPtr.Zero) return;

        bool show = Plugin.Subtitles == null || Plugin.Subtitles.Value;
        MpvNative.mpv_set_option_string(_mpv, "sub-visibility", show ? "yes" : "no");

        var lang = (Plugin.SubtitleLanguage?.Value ?? "").Trim();
        if (lang.Length > 0) MpvNative.mpv_set_option_string(_mpv, "slang", lang);
    }

    /// <summary>Picks a subtitle track for the file that was just loaded.</summary>
    private void ApplySubtitleTrack()
    {
        if (_mpv == IntPtr.Zero) return;

        bool show = Plugin.Subtitles == null || Plugin.Subtitles.Value;
        MpvNative.SetPropertyString(_mpv, "sub-visibility", show ? "yes" : "no");
        var lang = (Plugin.SubtitleLanguage?.Value ?? "").Trim();
        if (lang.Length > 0) MpvNative.SetPropertyString(_mpv, "slang", lang);
        // "auto" is mpv's own wording for "pick the matching default track, else none".
        MpvNative.SetPropertyString(_mpv, "sid", show ? "auto" : "no");
    }

    private unsafe void InitRenderContext()
    {
        if (_renderContext != IntPtr.Zero) return;

        // Software rendering configuration
        int advancedControl = 1;
        var initParams = new MpvNative.MpvRenderParam[]
        {
            new()
            {
                Type = MpvNative.MpvRenderParamType.AdvancedControl,
                Data = (IntPtr)(&advancedControl)
            },
            new() { Type = MpvNative.MpvRenderParamType.Invalid, Data = IntPtr.Zero }
        };

        int err = MpvNative.mpv_render_context_create(out _renderContext, _mpv, initParams);
        if (err < 0)
        {
            Plugin.Log.LogError($"mpv_render_context_create failed: {MpvNative.GetError(err)}");
            _error = "Could not initialize video renderer: " + MpvNative.GetError(err);
        }
    }

    public void Load(string directUrl, double knownDurationSeconds)
    {
        Stop();
        _error = null;
        _knownDuration = knownDurationSeconds > 0 ? knownDurationSeconds : 0.0;
        _hasFirstFrame = false;

        try
        {
            EnsureInitialized();

            Plugin.Log.LogInfo($"MpvVideoBackend: loading stream with libmpv: {directUrl}");
            int err = MpvNative.Command(_mpv, "loadfile", directUrl);
            if (err < 0)
            {
                _error = "Failed to load stream: " + MpvNative.GetError(err);
                Plugin.Log.LogError(_error);
                _loaded = false;
                return;
            }

            _loaded = true;
            // The track list only exists once the file is loaded, so subtitle selection has
            // to happen after the load command, not before it.
            ApplySubtitleTrack();
            ApplyVolumeAndPan();
        }
        catch (Exception e)
        {
            _error = "libmpv playback error: " + e.Message;
            Plugin.Log.LogError(_error + "\n" + e);
            _loaded = false;
        }
    }

    public void Play()
    {
        if (_mpv == IntPtr.Zero || !_loaded) return;
        MpvNative.SetPropertyBool(_mpv, "pause", false);
    }

    public void Pause()
    {
        if (_mpv == IntPtr.Zero || !_loaded) return;
        MpvNative.SetPropertyBool(_mpv, "pause", true);
    }

    public void Seek(double seconds)
    {
        if (_mpv == IntPtr.Zero || !_loaded) return;
        seconds = Math.Max(0.0, seconds);
        MpvNative.Command(_mpv, "seek", seconds.ToString(CultureInfo.InvariantCulture), "absolute");
    }

    public void Stop()
    {
        if (_mpv != IntPtr.Zero && _loaded)
        {
            MpvNative.Command(_mpv, "stop");
        }
        _loaded = false;
        _hasFirstFrame = false;
    }

    public void SetVolume(float volume01)
    {
        _volume = Mathf.Clamp01(volume01);
        ApplyVolumeAndPan();
    }

    public void Tick()
    {
        if (_disposed || _mpv == IntPtr.Zero) return;

        UpdateSpatialAudio();

        if (_renderContext != IntPtr.Zero && _loaded)
        {
            ulong flags = MpvNative.mpv_render_context_update(_renderContext);
            if ((flags & (ulong)MpvNative.MpvRenderContextFlag.UpdateFrame) != 0)
            {
                RenderFrameToTexture();
            }
        }
    }

    private unsafe void RenderFrameToTexture()
    {
        int[] size = [_width, _height];
        ulong strideVal = (ulong)_stride;
        IntPtr destBuf = _pixelBuffer;
        byte[] formatBytes = "rgba\0"u8.ToArray();

        fixed (int* pSize = size)
        fixed (byte* pFormat = formatBytes)
        {
            var renderParams = new MpvNative.MpvRenderParam[]
            {
                new() { Type = MpvNative.MpvRenderParamType.SwSize, Data = (IntPtr)pSize },
                new() { Type = MpvNative.MpvRenderParamType.SwFormat, Data = (IntPtr)pFormat },
                new() { Type = MpvNative.MpvRenderParamType.SwStride, Data = (IntPtr)(&strideVal) },
                new() { Type = MpvNative.MpvRenderParamType.SwPointer, Data = (IntPtr)(&destBuf) },
                new() { Type = MpvNative.MpvRenderParamType.Invalid, Data = IntPtr.Zero }
            };

            int err = MpvNative.mpv_render_context_render(_renderContext, renderParams);
            if (err >= 0)
            {
                _texture.LoadRawTextureData(_pixelBuffer, _bufferSize);
                _texture.Apply(false, false);
                if (_rt != null)
                {
                    Graphics.Blit(_texture, _rt);
                }
                _hasFirstFrame = true;
            }
        }
    }

    private void UpdateSpatialAudio()
    {
        if (_mpv == IntPtr.Zero || !_loaded) return;
        ApplyVolumeAndPan();
    }

    /// <summary>
    /// The hwdec option this build should use. "auto-copy" is deliberately the default:
    /// it decodes on the GPU and copies finished frames back into ordinary memory, which is
    /// the only hardware path that works when the embedder has no GPU context of its own to
    /// offer. Plain "auto" asks for direct hwdec, needs a real OpenGL/D3D context, and ends
    /// up failing over to software anyway - while printing errors that read like a bug.
    /// </summary>
    private static string HwDecodeValue() => Plugin.HwDecode?.Value switch
    {
        HwDecodeMode.No => "no",
        HwDecodeMode.Auto => "auto",
        _ => "auto-copy",
    };

    private void ApplyVolumeAndPan()
    {
        if (_mpv == IntPtr.Zero) return;

        Transform cam = null;
        try
        {
            var pc = WorldManager.localPlayerCharacter;
            if (pc != null) cam = pc.cameraTransform;
        }
        catch { }

        if (cam == null || _host == null)
        {
            MpvNative.SetPropertyDouble(_mpv, "volume", _volume * 100.0);
            MpvNative.SetPropertyDouble(_mpv, "balance", 0.0);
            return;
        }

        Vector3 screenPos = _host.transform.position;
        Vector3 camPos = cam.position;
        Vector3 toScreen = screenPos - camPos;
        float dist = toScreen.magnitude;

        float fullRadius = Plugin.AudioFullVolumeRadius != null ? Plugin.AudioFullVolumeRadius.Value : 1.5f;
        float maxDist = Plugin.AudioMaxDistance != null ? Plugin.AudioMaxDistance.Value : 25f;

        float volFactor;
        if (dist <= fullRadius)
            volFactor = 1.0f;
        else if (dist >= maxDist)
            volFactor = 0.0f;
        else
            volFactor = Mathf.Clamp01(fullRadius / dist);

        double targetVol = _volume * volFactor * 100.0;
        MpvNative.SetPropertyDouble(_mpv, "volume", targetVol);

        // Panning relative to camera heading
        Vector3 camRight = cam.right;
        Vector3 dir = dist > 0.001f ? toScreen / dist : Vector3.zero;
        float pan = Mathf.Clamp(Vector3.Dot(camRight, dir), -1.0f, 1.0f);
        MpvNative.SetPropertyDouble(_mpv, "balance", pan);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        if (_renderContext != IntPtr.Zero)
        {
            MpvNative.mpv_render_context_free(_renderContext);
            _renderContext = IntPtr.Zero;
        }

        if (_mpv != IntPtr.Zero)
        {
            MpvNative.mpv_terminate_destroy(_mpv);
            _mpv = IntPtr.Zero;
        }

        if (_pixelBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pixelBuffer);
        }

        if (_texture != null)
        {
            UnityEngine.Object.Destroy(_texture);
        }
    }
}
