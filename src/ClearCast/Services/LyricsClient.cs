using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClearCast;

internal readonly record struct LyricsQuery(string Title, string Artist, string Album, long DurationMs);

internal readonly record struct LyricsResult(Lyrics? Lyrics, bool Failed);

internal sealed record LrclibRecord(
    string? TrackName = null,
    string? ArtistName = null,
    string? AlbumName = null,
    double? Duration = null,
    bool Instrumental = false,
    string? PlainLyrics = null,
    string? SyncedLyrics = null);

/// <summary>Looks up lyrics on LRCLIB. Only track text and duration are sent; results stay in memory.
/// Use from one thread (the UI dispatcher).</summary>
internal sealed partial class LyricsClient : IDisposable
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumSearchResults = 20;
    private const int MaximumCachedTracks = 64;
    private const int MaximumLookupRequests = 10;
    private const long DurationToleranceMs = 2000;
    private const uint SimplifiedChinese = 0x02000000;
    private const uint TraditionalChinese = 0x04000000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object ChineseMappingGate = new();
    private static ChineseTransliterator? _simplifiedMapper, _traditionalMapper;
    private static bool _icuUnavailable;
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;
    private readonly Dictionary<LyricsQuery, Lyrics?> _cache = [];

    internal LyricsClient(HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            BaseAddress = new Uri("https://lrclib.net/api/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        Version? version = typeof(LyricsClient).Assembly.GetName().Version;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClearCast/{version?.ToString() ?? "1.0"} (+https://github.com/NCFXZ/ClearCast)");
    }

    public void Dispose() => _http.Dispose();

    internal async Task<LyricsResult> FindAsync(LyricsQuery query, CancellationToken token)
    {
        if (query.Title.Length == 0) return new LyricsResult(null, false);
        if (_cache.TryGetValue(query, out Lyrics? cached)) return new LyricsResult(cached, false);
        CancellationToken callerToken = token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_requestTimeout);
        token = deadline.Token;
        try
        {
            Lyrics? lyrics = null;
            int remainingRequests = MaximumLookupRequests;
            bool hasArtist = !string.IsNullOrWhiteSpace(query.Artist);
            long seconds = (query.DurationMs + 500) / 1000;
            if (seconds > 0 && hasArtist)
            {
                --remainingRequests;
                var exact = await GetJsonAsync<LrclibRecord>(
                    $"get?track_name={Escape(query.Title)}&artist_name={Escape(query.Artist)}" +
                    $"&album_name={Escape(query.Album)}&duration={seconds}", token);
                if (exact is not null) lyrics = Select([exact], query.DurationMs, requireDuration: false, query.Album);
            }
            var variants = QueryVariants(query).ToArray();
            var searched = new HashSet<string>(StringComparer.Ordinal);
            // Keep the album-specific search first, but do not lose songs whose
            // database artist/album spelling differs from the sender's metadata.
            if (hasArtist)
                foreach (bool includeAlbum in string.IsNullOrWhiteSpace(query.Album) ? new[] { false } : new[] { true, false })
                {
                    foreach (var variant in variants)
                    {
                        if (lyrics is not null || remainingRequests == 0) break;
                        string search = $"track_name={Escape(variant.Title)}&artist_name={Escape(variant.Artist)}" +
                            (includeAlbum ? "&album_name=" + Escape(variant.Album) : "");
                        if (searched.Add(search))
                        {
                            --remainingRequests;
                            lyrics = Select(await SearchAsync(search, token), query.DurationMs, requireDuration: false, query.Album);
                        }
                    }
                    // Native script mappings can choose different traditional forms.
                    // Album discovery avoids needing a private phrase dictionary.
                    if (includeAlbum && lyrics is null && variants.Length > 1)
                        foreach (var variant in variants)
                        {
                            if (remainingRequests == 0) break;
                            string search = "q=" + Escape(variant.Artist + " " + variant.Album);
                            if (searched.Add(search))
                            {
                                --remainingRequests;
                                lyrics = Select(await SearchAsync(search, token), query, requireDuration: query.DurationMs > 0);
                            }
                            if (lyrics is not null) break;
                        }
                    if (lyrics is not null) break;
                }
            // A broad title fallback needs a real duration and, when supplied,
            // an artist match; punctuation and Chinese script aliases are allowed.
            if (lyrics is null && query.DurationMs > 0)
                foreach (var variant in variants)
                {
                    if (remainingRequests == 0) break;
                    string title = CleanTitle(variant.Title);
                    if (title.Length == 0) continue;
                    string search = $"track_name={Escape(title)}";
                    if (searched.Add(search))
                    {
                        --remainingRequests;
                        lyrics = Select(await SearchAsync(search, token), query with { Title = CleanTitle(query.Title) }, requireDuration: true);
                    }
                    if (lyrics is not null) break;
                }
            if (_cache.Count >= MaximumCachedTracks) _cache.Clear();
            _cache[query] = lyrics;
            return new LyricsResult(lyrics, false);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or InvalidDataException ||
            error is OperationCanceledException && !callerToken.IsCancellationRequested)
        {
            return new LyricsResult(null, true);
        }
    }

    internal static Lyrics? ToLyrics(LrclibRecord record) =>
        Lyrics.ParseSynced(record.SyncedLyrics) ?? (record.Instrumental ? Lyrics.Instrumental : Lyrics.ParsePlain(record.PlainLyrics));

    internal static Lyrics? Select(IEnumerable<LrclibRecord>? records, LyricsQuery query, bool requireDuration)
    {
        string title = NormalizeMetadata(CleanTitle(query.Title)), artist = NormalizeMetadata(query.Artist);
        if (title.Length == 0) return null;
        return Select(records?.Take(MaximumSearchResults).Where(record => record is not null && record.TrackName is { } candidateTitle &&
            string.Equals(NormalizeMetadata(CleanTitle(candidateTitle)), title, StringComparison.OrdinalIgnoreCase) &&
            (artist.Length == 0 || record.ArtistName is { } candidateArtist && ArtistMatches(candidateArtist, artist))),
            query.DurationMs, requireDuration, query.Album);
    }

    /// <summary>Prefers the requested album, then synced lyrics and the closest valid duration.</summary>
    internal static Lyrics? Select(IEnumerable<LrclibRecord>? records, long durationMs, bool requireDuration, string? album = null)
    {
        if (records is null || requireDuration && durationMs <= 0) return null;
        string? expectedAlbum = string.IsNullOrWhiteSpace(album) ? null : NormalizeMetadata(album);
        return records.Take(MaximumSearchResults)
            .Where(record => record is not null && (record.Duration is null || record.Duration is > 0 && double.IsFinite(record.Duration.Value)))
            .Select(record => (Record: record, Difference: durationMs > 0 && record.Duration is double seconds && seconds > 0 && double.IsFinite(seconds)
                ? Math.Abs(seconds * 1000 - durationMs) : (double?)null))
            .Where(item => item.Difference is null ? !requireDuration : item.Difference <= DurationToleranceMs)
            .Select(item => (Lyrics: ToLyrics(item.Record), item.Difference,
                AlbumMatch: expectedAlbum is not null && item.Record.AlbumName is { } candidate &&
                    string.Equals(NormalizeMetadata(candidate), expectedAlbum, StringComparison.OrdinalIgnoreCase)))
            .Where(item => item.Lyrics is not null)
            .OrderByDescending(item => item.AlbumMatch)
            .ThenBy(item => item.Lyrics!.Kind == LyricsKind.Synced ? 0 : 1)
            .ThenBy(item => item.Difference ?? double.MaxValue)
            .Select(item => item.Lyrics)
            .FirstOrDefault();
    }

    internal static string CleanTitle(string title) => BracketedSuffix().Replace(title, "").Trim();

    private static string NormalizeMetadata(string value) => MapChinese(value.Normalize(NormalizationForm.FormKC).Trim(), SimplifiedChinese);

    private static IEnumerable<LyricsQuery> QueryVariants(LyricsQuery query)
    {
        var seen = new HashSet<LyricsQuery> { query };
        yield return query;
        foreach (uint script in new[] { SimplifiedChinese, TraditionalChinese })
        {
            var variant = query with { Title = MapChinese(query.Title, script), Artist = MapChinese(query.Artist, script),
                Album = MapChinese(query.Album, script) };
            if (seen.Add(variant)) yield return variant;
        }
        if (seen.Count < 3)
        {
            var variant = query with { Title = MapChineseNls(query.Title, TraditionalChinese),
                Artist = MapChineseNls(query.Artist, TraditionalChinese), Album = MapChineseNls(query.Album, TraditionalChinese) };
            if (seen.Add(variant)) yield return variant;
        }
    }

    private static bool ArtistMatches(string candidate, string expected)
    {
        candidate = NormalizeMetadata(candidate);
        static string Fold(string text) => new(text.Where(char.IsLetterOrDigit).ToArray());
        string folded = Fold(candidate), foldedExpected = Fold(expected);
        if (folded.Length > 0 && string.Equals(folded, foldedExpected, StringComparison.OrdinalIgnoreCase)) return true;
        static bool Alias(string text, string name)
        {
            if (name.Length == 0) return false;
            int start = 0;
            while ((start = text.IndexOf(name, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = start + name.Length;
                if ((start == 0 || !char.IsLetterOrDigit(text[start - 1]) || IsChinese(text[start - 1]) != IsChinese(name[0])) &&
                    (end == text.Length || !char.IsLetterOrDigit(text[end]) || IsChinese(text[end]) != IsChinese(name[^1])))
                {
                    string other = text.Remove(start, name.Length);
                    bool nameChinese = name.Any(IsChinese), otherChinese = other.Any(IsChinese);
                    if (nameChinese != otherChinese || other.IndexOfAny(['&', '/', ';', '(', ')', '[', ']', '（', '）', '【', '】', '、', '|', '-']) >= 0)
                        return true;
                }
                ++start;
            }
            return false;
        }
        return Alias(candidate, expected) || Alias(expected, candidate) ||
            Alias(folded, foldedExpected) || Alias(foldedExpected, folded);
    }

    private static string MapChinese(string value, uint script)
    {
        if (!OperatingSystem.IsWindows() || !value.Any(IsChinese)) return value;
        return MapChineseIcu(value, script) ?? MapChineseNls(value, script);
    }

    private static bool IsChinese(char character) => character is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';

    private static string? MapChineseIcu(string value, uint script)
    {
        lock (ChineseMappingGate)
        {
            if (_icuUnavailable) return null;
            try
            {
                ref var mapper = ref (script == SimplifiedChinese ? ref _simplifiedMapper : ref _traditionalMapper);
                if (mapper is null)
                {
                    string id = script == SimplifiedChinese ? "Traditional-Simplified" : "Simplified-Traditional";
                    int error = 0;
                    var created = new ChineseTransliterator(utrans_openU(id, id.Length, 0, 0, 0, 0, ref error));
                    if (created.IsInvalid || error > 0) { created.Dispose(); return null; }
                    mapper = created;
                }
                var buffer = new char[checked(value.Length * 2 + 16)];
                value.CopyTo(0, buffer, 0, value.Length);
                int length = value.Length, limit = length, status = 0;
                utrans_transUChars(mapper, buffer, ref length, buffer.Length, 0, ref limit, ref status);
                return status <= 0 && length >= 0 && length <= buffer.Length ? new string(buffer, 0, length) : null;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                _icuUnavailable = true;
                _simplifiedMapper?.Dispose();
                _traditionalMapper?.Dispose();
                _simplifiedMapper = _traditionalMapper = null;
                return null;
            }
        }
    }

    private static string MapChineseNls(string value, uint script)
    {
        if (!OperatingSystem.IsWindows() || !value.Any(IsChinese)) return value;
        int length = LCMapStringEx("zh-CN", script, value, value.Length, null, 0, 0, 0, 0);
        if (length <= 0) return value;
        var mapped = new char[length];
        int written = LCMapStringEx("zh-CN", script, value, value.Length, mapped, mapped.Length, 0, 0, 0);
        return written > 0 ? new string(mapped, 0, written) : value;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        [Out] char[]? destination, int destinationLength, nint version, nint reserved, nint sortHandle);

    private sealed class ChineseTransliterator : SafeHandle
    {
        internal ChineseTransliterator(nint value) : base(0, ownsHandle: true) => SetHandle(value);
        public override bool IsInvalid => handle == 0;
        protected override bool ReleaseHandle() { utrans_close(handle); return true; }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("icu.dll", CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint utrans_openU(string id, int idLength, int direction, nint rules, int rulesLength, nint parseError, ref int error);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("icu.dll", CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void utrans_transUChars(ChineseTransliterator mapper, [In, Out] char[] text, ref int length,
        int capacity, int start, ref int limit, ref int error);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("icu.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void utrans_close(nint mapper);

    private async Task<IReadOnlyList<LrclibRecord>?> SearchAsync(string query, CancellationToken token) =>
        await GetJsonAsync<LrclibRecord[]>("search?" + query, token);

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken token) where T : class
    {
        // ResponseHeadersRead ends HttpClient's timeout at the headers. Bound the body read too.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_requestTimeout);
        token = deadline.Token;
        using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Lyrics response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var body = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (body.Length + read > MaximumResponseBytes) throw new InvalidDataException("Lyrics response is too large.");
            body.Write(chunk, 0, read);
        }
        return JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, (int)body.Length), JsonOptions);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    [GeneratedRegex(@"\s*[\(\[（【][^\)\]）】]*[\)\]）】]")]
    private static partial Regex BracketedSuffix();
}
