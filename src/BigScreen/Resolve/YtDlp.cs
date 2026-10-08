using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BigScreen.Resolve;

/// <summary>
/// Turns a YouTube (or any yt-dlp supported) page URL into a direct media URL that
/// Unity's VideoPlayer can stream.
///
/// Why each player resolves for themselves: the googlevideo.com URLs yt-dlp returns are
/// bound to the IP address that requested them and expire after a few hours. The host
/// therefore shares the *page* URL and every peer runs yt-dlp locally.
///
/// Runs on a dedicated background thread and must not touch Unity/IL2CPP objects; the
/// caller marshals the result back with <see cref="Util.MainThread"/>.
///
/// Everything here is synchronous on purpose. An earlier version used async/await with
/// Process.WaitForExitAsync and ReadToEndAsync, and the game died twice with a fatal access
/// violation inside coreclr.dll (same fault offset both times) while yt-dlp was running.
/// coreclr only ever runs BepInEx and plugin code - the game itself is IL2CPP - so the fault
/// was in our code, and this resolve was the only concurrency the mod owned. WaitForExitAsync
/// sets EnableRaisingEvents, which registers a wait on the runtime thread pool and calls back
/// on a pool thread; blocking reads on dedicated threads avoid that machinery completely.
/// </summary>
internal static class YtDlp
{
    public sealed class Result
    {
        public bool Ok;
        public string Error;
        public string Title;
        public double Duration;
        public string DirectUrl;
        public string YtDlpVersion;
    }

    private const string WindowsDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string LinuxDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp";

    private static readonly object DownloadLock = new();

    public static string PluginDirectory =>
        Path.GetDirectoryName(typeof(YtDlp).Assembly.Location) ?? BepInEx.Paths.PluginPath;

    public static string BinaryPath
    {
        get
        {
            var configured = Plugin.YtDlpPath.Value;
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
            return Path.Combine(PluginDirectory, name);
        }
    }

    public static bool IsAvailable => File.Exists(BinaryPath);

    public static bool LooksLikeUrl(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for a URL that already points at a media file, so there is nothing for yt-dlp to
    /// resolve. Pasting a direct MP4 link should just play it.
    /// </summary>
    public static bool LooksLikeDirectMedia(string url)
    {
        if (!LooksLikeUrl(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("tb-cdn.pw") || host.Contains("torbox.app") || uri.AbsolutePath.Contains("/resolve/torbox/"))
            return true;

        var ext = Path.GetExtension(uri.AbsolutePath);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".avi", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ts", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Makes sure the binary exists, downloading it if allowed. Thread-safe.</summary>
    public static string EnsureBinary(CancellationToken ct)
    {
        var path = BinaryPath;
        if (File.Exists(path)) return null;
        if (!Plugin.YtDlpAutoDownload.Value)
            return $"yt-dlp not found at {path} and AutoDownload is off. Download it from https://github.com/yt-dlp/yt-dlp/releases and put it there.";

        lock (DownloadLock)
        {
            try
            {
                if (File.Exists(path)) return null;
                var url = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsDownloadUrl : LinuxDownloadUrl;
                Plugin.Log.LogInfo($"Downloading yt-dlp from {url} ...");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromMinutes(3);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BigScreen-BigWalkMod/" + Plugin.Version);
                // Blocking on a dedicated thread; see the note on this class about async.
                var bytes = http.GetByteArrayAsync(url, ct).GetAwaiter().GetResult();
                if (bytes.Length < 1_000_000) return "Downloaded yt-dlp looks too small to be real; refusing to use it.";

                var tmp = path + ".download";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
                // A fresh download on Linux/macOS is not executable: the zip/tarball ships it
                // mode 644, and .NET's File.WriteAllBytes creates it the same way. Without this
                // the first resolve dies with "Permission denied" on a native Linux install,
                // which is exactly the install most people trying this outside Windows have.
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) TryMakeExecutable(path);
                Plugin.Log.LogInfo($"yt-dlp saved to {path} ({bytes.Length / 1024 / 1024} MB).");
                return null;
            }
            catch (Exception e)
            {
                return "Could not download yt-dlp: " + e.Message;
            }
        }
    }

    /// <summary>
    /// Sets the executable bit. On Unix this is a metadata flag, not a file operation, so a
    /// failure would mean something odd about the install; we only warn.
    /// </summary>
    private static void TryMakeExecutable(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/chmod", $"+x \"{path}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Could not mark yt-dlp executable ({e.Message}); " +
                                  "run 'chmod +x " + path + "' by hand if resolving fails.");
        }
    }

    private sealed class ProcessOutput
    {
        public bool Exited;
        public int ExitCode = -1;
        public string StdOut = "";
        public string StdErr = "";
    }

    /// <summary>
    /// Runs a process to completion and collects its output, with no async machinery.
    ///
    /// Both pipes are drained by their own threads. Reading one stream to the end before the
    /// other deadlocks as soon as the child fills the pipe it is not being read from, and
    /// yt-dlp writes progress to stderr while it works.
    /// </summary>
    private static ProcessOutput RunProcess(ProcessStartInfo psi, int timeoutSeconds)
    {
        var output = new ProcessOutput();
        using var proc = new Process { StartInfo = psi };
        proc.Start();

        string stdout = "", stderr = "";
        var outReader = new Thread(() => { try { stdout = proc.StandardOutput.ReadToEnd(); } catch { } })
        { IsBackground = true, Name = "BigScreen-ytdlp-stdout" };
        var errReader = new Thread(() => { try { stderr = proc.StandardError.ReadToEnd(); } catch { } })
        { IsBackground = true, Name = "BigScreen-ytdlp-stderr" };
        outReader.Start();
        errReader.Start();

        output.Exited = proc.WaitForExit(timeoutSeconds * 1000);
        if (!output.Exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }

        // Join gives us the reader threads' writes; the pipes end when the process does.
        outReader.Join(5000);
        errReader.Join(5000);
        output.StdOut = stdout;
        output.StdErr = stderr;
        if (output.Exited)
        {
            try { output.ExitCode = proc.ExitCode; } catch { }
        }
        return output;
    }

    /// <summary>Resolves a page URL to a direct stream URL using the configured format selector.</summary>
    public static Result Resolve(string pageUrl, CancellationToken ct)
    {
        var result = new Result();
        if (!LooksLikeUrl(pageUrl))
        {
            result.Error = "That does not look like a URL (must start with http:// or https://).";
            return result;
        }

        var ensureError = EnsureBinary(ct);
        if (ensureError != null)
        {
            result.Error = ensureError;
            return result;
        }

        var psi = new ProcessStartInfo
        {
            FileName = BinaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // yt-dlp writes UTF-8. Without this the console's legacy codepage mangles any
            // non-ASCII character, so a video title or an error message comes back with
            // "you're" rendered as "youÆre".
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--no-color");
        psi.ArgumentList.Add("--socket-timeout");
        psi.ArgumentList.Add("20");
        // Without this, YouTube's default player clients return only storyboard images and no
        // media formats at all, so every selector below fails with "Requested format is not
        // available". See the ExtractorArgs config entry.
        var extractorArgs = Plugin.ExtractorArgs.Value;
        if (!string.IsNullOrWhiteSpace(extractorArgs))
        {
            psi.ArgumentList.Add("--extractor-args");
            psi.ArgumentList.Add(extractorArgs.Trim());
        }
        // YouTube gates repeated anonymous requests from one address behind "Sign in to
        // confirm you're not a bot". Cookies from a signed-in browser make the request look
        // like that account rather than an anonymous client, which is what clears it.
        var cookieBrowser = Plugin.CookiesFromBrowser.Value;
        if (!string.IsNullOrWhiteSpace(cookieBrowser))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(cookieBrowser.Trim());
        }
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(Plugin.FormatSelector.Value);
        // --print implies --simulate: nothing is downloaded. Three lines come back in this order.
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(title)s");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(duration)s");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(urls)s");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(pageUrl.Trim());

        try
        {
            var run = RunProcess(psi, 60);
            if (!run.Exited)
            {
                result.Error = "yt-dlp took too long (60 s) and was stopped.";
                return result;
            }

            var stdout = run.StdOut;
            var stderr = run.StdErr;

            if (run.ExitCode != 0)
            {
                var line = FirstUsefulLine(stderr) ?? $"exit code {run.ExitCode}";
                result.Error = "yt-dlp failed: " + line + HintFor(line);
                return result;
            }

            var lines = stdout.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 3)
            {
                result.Error = "yt-dlp returned an unexpected response: " + stdout.Trim();
                return result;
            }

            result.Title = lines[0].Trim();
            double.TryParse(lines[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result.Duration);

            // %(urls)s is newline-separated when the selector picks video+audio separately.
            // We only ask for muxed formats, so more than one URL means the selector was edited badly.
            var urlLines = Array.FindAll(lines[2..], l => LooksLikeUrl(l));
            if (urlLines.Length == 0)
            {
                result.Error = "yt-dlp did not return a stream URL.";
                return result;
            }
            if (urlLines.Length > 1)
            {
                result.Error = "The format selector picked separate video and audio streams. Unity's VideoPlayer " +
                               "needs a single muxed MP4; reset YtDlp.FormatSelector to its default.";
                return result;
            }

            result.DirectUrl = urlLines[0].Trim();
            result.Ok = true;
            return result;
        }
        catch (Exception e)
        {
            result.Error = "Could not run yt-dlp: " + e.Message;
            return result;
        }
    }

    /// <summary>Runs `yt-dlp -U` (self-update). YouTube breaks old versions regularly.</summary>
    public static string Update(CancellationToken ct)
    {
        var ensureError = EnsureBinary(ct);
        if (ensureError != null) return ensureError;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = BinaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-U");

            var run = RunProcess(psi, 180);
            if (!run.Exited) return "yt-dlp -U took too long (180 s) and was stopped.";
            return FirstUsefulLine(run.StdOut) ?? FirstUsefulLine(run.StdErr) ?? $"exit code {run.ExitCode}";
        }
        catch (Exception e)
        {
            return "Update failed: " + e.Message;
        }
    }

    /// <summary>
    /// Turns yt-dlp's own error text into something that says what to do about it. The two
    /// failures worth explaining are the ones YouTube actually serves us.
    /// </summary>
    private static string HintFor(string error)
    {
        if (error.Contains("not a bot", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(Plugin.CookiesFromBrowser.Value)
                ? " (YouTube is rate-limiting anonymous requests from this address. Set " +
                  "YtDlp.CookiesFromBrowser in the config to the browser you watch YouTube in - " +
                  "chrome, firefox, edge or brave - so yt-dlp can use your signed-in session. " +
                  "Otherwise wait an hour or so and it clears on its own.)"
                : $" (Cookies from '{Plugin.CookiesFromBrowser.Value}' were sent but YouTube still refused. " +
                  "Check you are signed in to YouTube in that browser, and that the browser is closed - " +
                  "it can hold a lock on its own cookie database.)";
        }

        if (error.Contains("not available", StringComparison.OrdinalIgnoreCase))
        {
            return " (YouTube is refusing to list formats for this player client. Try 'Update yt-dlp' " +
                   "in the panel, then change YtDlp.ExtractorArgs in the config - " +
                   "youtube:player_client=tv / ios / web_safari are the usual alternatives.)";
        }

        return "";
    }

    private static string FirstUsefulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) return line.Substring(6).Trim();
            return line;
        }
        return null;
    }
}
