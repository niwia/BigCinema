using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BigScreen.Torbox;

/// <summary>
/// Client for searching movies/shows via Cinemeta and fetching Torbox-cached streams
/// from the configured Torrentio addon.
/// </summary>
internal static class TorrentioClient
{
    private static readonly Regex IdPattern = new(@"^(tt\d+)(?::\d{1,4}:\d{1,4})?$", RegexOptions.CultureInvariant);
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5
    })
    {
        Timeout = TimeoutSeconds
    };

    private static readonly TimeSpan TimeoutSeconds = TimeSpan.FromSeconds(20);
    private const int SearchCacheMinutes = 5;
    private static readonly Dictionary<string, (DateTime When, List<CatalogItem> Items)> SearchCache = new();

    public sealed class CatalogItem
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string Year { get; set; }

        public override string ToString() => $"{Name} ({Year}) [{Type}]";
    }

    public sealed class TorrentioStream
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("behaviorHints")]
        public StreamBehaviorHints BehaviorHints { get; set; }

        /// <summary>
        /// Present on torrent-only results. A stream with an infoHash and no https url is a
        /// magnet link, which neither libmpv nor Unity can play, so we surface it as a
        /// configuration problem instead of silently dropping it.
        /// </summary>
        [JsonPropertyName("infoHash")]
        public string InfoHash { get; set; }

        public bool IsDirect => Uri.TryCreate(Url, UriKind.Absolute, out var u) &&
                                u.Scheme == Uri.UriSchemeHttps;

        public string CleanTitle
        {
            get
            {
                if (!string.IsNullOrEmpty(BehaviorHints?.Filename))
                    return BehaviorHints.Filename;
                if (string.IsNullOrEmpty(Title)) return Name ?? "Stream";
                var lines = Title.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                return lines.Length > 0 ? lines[0] : Title;
            }
        }

        public string Quality
        {
            get
            {
                if (string.IsNullOrEmpty(Name)) return "";
                var parts = Name.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 ? parts[1].Trim() : parts[0].Trim();
            }
        }
    }

    public sealed class StreamBehaviorHints
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; }

        [JsonPropertyName("bingeGroup")]
        public string BingeGroup { get; set; }
    }

    private sealed class StreamResponse
    {
        [JsonPropertyName("streams")]
        public List<TorrentioStream> Streams { get; set; }
    }

    /// <summary>
    /// A finished Torrentio query. <see cref="Playable"/> is what the panel lists, and
    /// <see cref="Problem"/> explains a result that yields nothing playable - the two
    /// important ones being "no addon URL/key configured" and "only magnet links",
    /// which means the debrid account rejected the key.
    /// </summary>
    public sealed class StreamQueryResult
    {
        public List<TorrentioStream> Playable { get; } = new();
        public int MagnetOnly { get; set; }
        public string Problem { get; set; }
    }

    private sealed class CinemetaSearchResponse
    {
        [JsonPropertyName("metas")]
        public List<CinemetaMeta> Metas { get; set; }
    }

    private sealed class CinemetaMeta
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("releaseInfo")]
        public string ReleaseInfo { get; set; }
    }

    public const string TorrentioBaseUrl = "https://torrentio.strem.fun";

    /// <summary>"Never configured" / "configured" without touching the config API directly.</summary>
    public static bool IsConfigured => AddonBaseUrl.Length > 0;

    /// <summary>
    /// The addon base URL, i.e. the manifest URL without its /manifest.json suffix and
    /// without a trailing slash, ready to have "/stream/movie/tt0111161.json" appended.
    ///
    /// Two ways to configure it:
    ///   - TorrentioAddonUrl: a full addon URL pasted from the Torrentio site (works for any
    ///     debrid provider, not just Torbox). This always wins, so it can override the key.
    ///   - Torbox.ApiKey: the mod builds the same URL the Torrentio configuration page builds
    ///     for that key, so nobody has to hand-assemble query strings.
    /// </summary>
    public static string AddonBaseUrl
    {
        get
        {
            var explicitUrl = (Plugin.TorrentioAddonUrl?.Value ?? "").Trim();
            if (explicitUrl.Length > 0) return Normalize(explicitUrl);

            // The Torrentio configuration page emits exactly this shape:
            //   https://torrentio.strem.fun/sort=quality|torbox=YOUR_KEY/manifest.json
            // where the debrid API key is passed as "<provider>=<key>". Keeping the order
            // (sort first, key last) matches it byte for byte.
            var key = (Plugin.TorboxApiKey?.Value ?? "").Trim();
            if (key.Length == 0) return "";

            var sb = new System.Text.StringBuilder(TorrentioBaseUrl);
            sb.Append('/');
            var sort = (Plugin.TorboxSort?.Value ?? "").Trim();
            if (sort.Length > 0)
                sb.Append("sort=").Append(Uri.EscapeDataString(sort)).Append('|');
            sb.Append("torbox=").Append(Uri.EscapeDataString(key));
            return Normalize(sb.ToString());
        }
    }

    private static string Normalize(string url)
    {
        url = url.Trim();
        if (url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
            url = url.Substring(0, url.Length - "/manifest.json".Length);
        return url.TrimEnd('/');
    }

    private static Uri GetAddonBaseUri()
    {
        var value = AddonBaseUrl;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                "No Torbox addon configured. Put your Torbox API key in Torbox -> ApiKey, " +
                "or paste a full addon URL into Torbox -> TorrentioAddonUrl (see the panel).");

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException(
                "The addon URL must be an absolute HTTPS URL (TorrentioAddonUrl or a Torbox API key).");

        return uri;
    }

    /// <summary>
    /// Matches the credential-bearing options Torrentio puts in the URL path, for example
    /// /sort=quality|torbox=SECRET/stream/…. The leading character is asserted with a
    /// lookbehind rather than consumed, so "|" and "/" survive the redaction.
    /// </summary>
    private static readonly Regex SecretOption = new(
        @"(?<![^/|])(torbox|realdebrid|premiumize|alldebrid|debridlink|easydebrid|offcloud|putio|highway|apikey|api_key|token)=[^/|]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A URL summary that is safe to paste into a bug report.
    ///
    /// The debrid token lives in the *path* (…/torbox=SECRET/stream/movie/tt…json), so dropping
    /// the query string is not enough: every credential-shaped option is replaced, and the
    /// query and fragment are dropped whole. This works on the raw request string because
    /// Uri.AbsolutePath decodes escapes, which would let a carefully chosen token through.
    /// </summary>
    public static string DescribeUri(Uri uri)
    {
        var raw = uri.OriginalString;
        int cut = raw.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) raw = raw.Substring(0, cut);
        return SecretOption.Replace(raw, "$1=<redacted>");
    }

    /// <summary>
    /// Searches Cinemeta for movies and series matching the query string.
    ///
    /// Results are cached for a few minutes: a lobby full of people deciding what to watch
    /// searches the same handful of titles over and over, and Cinemeta is rate limited.
    /// </summary>
    public static async Task<List<CatalogItem>> SearchCinemetaAsync(string query, CancellationToken ct = default)
    {
        var results = new List<CatalogItem>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        query = query.Trim().ToLowerInvariant();
        var cacheKey = query;
        lock (SearchCache)
        {
            if (SearchCache.TryGetValue(cacheKey, out var hit) &&
                (DateTime.UtcNow - hit.When).TotalMinutes < SearchCacheMinutes)
                return hit.Items;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var escaped = Uri.EscapeDataString(query);

        string[] endpoints =
        [
            $"https://v3-cinemeta.strem.io/catalog/movie/top/search={escaped}.json",
            $"https://v3-cinemeta.strem.io/catalog/series/top/search={escaped}.json"
        ];

        foreach (var ep in endpoints)
        {
            try
            {
                var json = await Http.GetStringAsync(ep, ct).ConfigureAwait(false);
                var resp = JsonSerializer.Deserialize<CinemetaSearchResponse>(json);
                if (resp?.Metas != null)
                {
                    foreach (var m in resp.Metas)
                    {
                        if (string.IsNullOrEmpty(m.Id)) continue;
                        var item = new CatalogItem
                        {
                            Id = m.Id,
                            Type = m.Type ?? "movie",
                            Name = m.Name ?? m.Id,
                            Year = m.ReleaseInfo ?? ""
                        };
                        if (seen.Add($"{item.Type}:{item.Id}")) results.Add(item);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Cinemeta search failed for {ep}: {e.Message}");
            }
        }

        lock (SearchCache)
        {
            SearchCache[cacheKey] = (DateTime.UtcNow, results);
            // Cinemeta returns 100+ entries per query; keeping every query forever would
            // grow without bound in a long session.
            if (SearchCache.Count > 64) SearchCache.Clear();
        }

        return results;
    }

    /// <summary>
    /// Queries the Torrentio addon for streams matching a movie or series ID.
    /// Format for movies: tt0111161. Format for series: tt0903747:1:1 (imdb:season:episode).
    ///
    /// Returns the direct (HTTPS) streams that can actually be played. If the addon only
    /// answered with magnet links, <see cref="StreamQueryResult.Problem"/> says so - that is
    /// what a rejected or empty debrid account looks like from here.
    /// </summary>
    public static async Task<StreamQueryResult> GetStreamsAsync(string type, string id, CancellationToken ct = default)
    {
        if (!string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "series", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Stream type must be movie or series.", nameof(type));
        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id.Trim()))
            throw new ArgumentException("The media id is not a valid IMDB id.", nameof(id));

        var result = new StreamQueryResult();

        var baseUri = GetAddonBaseUri();
        // The Torrentio configuration is encoded in the path (for example
        // /sort=quality|torbox=...); Uri(base, relative) would replace that path.
        var url = new Uri($"{baseUri.AbsoluteUri.TrimEnd('/')}/stream/{type.ToLowerInvariant()}/{id.Trim()}.json");

        string json;
        try
        {
            json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Torrentio stream request failed for {DescribeUri(url)}: {e.Message}");
            throw;
        }

        Plugin.Log.LogInfo($"Querying Torrentio streams: {DescribeUri(url)}");

        var resp = JsonSerializer.Deserialize<StreamResponse>(json);
        var all = resp?.Streams ?? new List<TorrentioStream>();

        foreach (var s in all)
        {
            if (s == null) continue;
            if (s.IsDirect) result.Playable.Add(s);
            else if (!string.IsNullOrEmpty(s.InfoHash)) result.MagnetOnly++;
        }

        if (result.Playable.Count > 0)
        {
            // Best first: sort by the label Torrentio already computed when asked to.
            if (string.Equals((Plugin.TorboxSort?.Value ?? "").Trim(), "quality",
                    StringComparison.OrdinalIgnoreCase))
            {
                result.Playable.Sort((a, b) =>
                    string.Compare(b.Quality, a.Quality, StringComparison.Ordinal));
            }
            if (result.Playable.Count > 50)
                result.Playable.RemoveRange(50, result.Playable.Count - 50);
        }
        else if (result.MagnetOnly > 0)
        {
            result.Problem =
                $"Torrentio returned {result.MagnetOnly} torrent link(s) and no playable stream, " +
                "which means it did not accept your Torbox API key. Check Torbox -> ApiKey " +
                "(your key from torbox.app/settings), or set TorrentioAddonUrl to the addon URL " +
                "the Torrentio site gives you. Magnet links cannot be played by the game.";
        }
        else if (all.Count == 0)
        {
            result.Problem = "No streams found for that title. It may not be cached by Torbox yet.";
        }

        return result;
    }

    /// <summary>
    /// Follows the Torrentio 302/307 redirects to get the direct CDN streaming URL.
    /// </summary>
    public static async Task<string> ResolveDirectCdnUrlAsync(string streamUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var input) ||
            input.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The stream URL must be HTTPS.", nameof(streamUrl));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, input);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.RequestMessage?.RequestUri != null)
                return resp.RequestMessage.RequestUri.ToString();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Redirect check failed for {DescribeUri(input)}, returning original URL: {e.Message}");
        }
        return streamUrl;
    }
}
