using System.Net;
using System.Net.Http;

namespace ClearCast;

internal static class LyricsTests
{
    internal static void Run(Action<bool, string> check)
    {
        var synced = Lyrics.ParseSynced("[ti:Song]\n[ar:Artist]\n[00:05.20]Second\r\n[00:01.5]First\n[00:09]\n[01:02.345][00:12.00]Twice\nno timestamp\n[xx:yy]bad");
        check(synced is { Kind: LyricsKind.Synced }, "synced LRC parses");
        check(synced!.Lines.Select(line => line.TimeMs).SequenceEqual([1500L, 5200, 9000, 12000, 62345]),
            "timestamps accept 1-3 fraction digits, repeat per line and sort");
        check(synced.Lines[0].Text == "First" && synced.Lines[2].Text == "" && synced.Lines[4].Text == "Twice",
            "text is trimmed and blank interlude entries are kept for timing");
        check(synced.ActiveIndex(0) == -1 && synced.ActiveIndex(1500) == 0 && synced.ActiveIndex(5199) == 0 &&
            synced.ActiveIndex(9500) == 2 && synced.ActiveIndex(999_999) == 4, "active line is the last one at or before the position");
        check(synced.NextChangeMs(0) == 1500 && synced.NextChangeMs(1500) == 5200 && synced.NextChangeMs(62345) == -1,
            "next change is strictly after the position");

        var offset = Lyrics.ParseSynced("[offset:+500]\n[00:01.00]A\n[00:00.20]B");
        check(offset!.Lines[0].TimeMs == 0 && offset.Lines[1].TimeMs == 500, "positive offset shows lines earlier without going negative");
        check(Lyrics.ParseSynced("[00:01.00]\n[00:02.00]  ") is null && Lyrics.ParseSynced("") is null,
            "lyrics without visible text are rejected");
        var cleaned = Lyrics.ParseSynced("[00:01.00]a\u0007b" + new string('x', 1000));
        check(cleaned!.Lines[0].Text.StartsWith("ab") && cleaned.Lines[0].Text.Length == Lyrics.MaximumLineLength,
            "control characters are removed and lines are length-bounded");
        string many = string.Concat(Enumerable.Range(0, Lyrics.MaximumLines + 50).Select(i => $"[00:{i % 60:00}.00]L{i}\n"));
        check(Lyrics.ParseSynced(many)!.Lines.Count == Lyrics.MaximumLines, "line count is bounded");

        string plainFlood = string.Concat(Enumerable.Repeat("x\n", 300_000));
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var boundedPlain = Lyrics.ParsePlain(plainFlood);
        long plainAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        check(boundedPlain?.Lines.Count == Lyrics.MaximumLines && plainAllocated < 1_000_000,
            "plain lyrics stop parsing at the line limit without allocating the oversized tail");
        string syncedFlood = string.Concat(Enumerable.Repeat("[00:01.00]x\n", 50_000)) + "[offset:+500]";
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var boundedSynced = Lyrics.ParseSynced(syncedFlood);
        long syncedAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        check(boundedSynced?.Lines.Count == Lyrics.MaximumLines && syncedAllocated < 1_000_000,
            "synced lyrics retain bounded allocations when timestamped lines exceed the limit");
        check(boundedSynced!.Lines.All(line => line.TimeMs == 500),
            "an offset after the line limit still applies to retained synced lyrics");
        string repeatedTimestamp = string.Concat(Enumerable.Repeat("[00:01.00]", 100_000)) + "Repeated timestamp";
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var boundedTimestamps = Lyrics.ParseSynced(repeatedTimestamp);
        long timestampAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        check(boundedTimestamps?.Lines.Count == Lyrics.MaximumLines && timestampAllocated < 1_000_000,
            "a single network-sized lyric line cannot allocate an unbounded timestamp list");

        var plain = Lyrics.ParsePlain("One\n\nTwo");
        check(plain is { Kind: LyricsKind.Plain } && plain.Lines.Count == 3 && plain.ActiveIndex(10_000) == -1 &&
            plain.NextChangeMs(0) == -1, "plain lyrics have no active line or timer");

        var records = new[]
        {
            new LrclibRecord(Duration: 200, PlainLyrics: "plain close"),
            new LrclibRecord(Duration: 203.5, SyncedLyrics: "[00:01.00]synced far"),
            new LrclibRecord(Duration: 201.5, SyncedLyrics: "[00:01.00]synced near"),
            new LrclibRecord(Duration: 300, SyncedLyrics: "[00:01.00]wrong version"),
        };
        check(LyricsClient.Select(records, 200_000, requireDuration: false)!.Lines[0].Text == "synced near",
            "selection prefers synced lyrics within the duration tolerance");
        check(LyricsClient.Select(records, 0, requireDuration: true) is null, "title-only matches require a known duration");
        check(LyricsClient.Select([new LrclibRecord(Duration: 120, SyncedLyrics: "[00:01.00]x")], 200_000, false) is null,
            "a different recording length is rejected");
        check(LyricsClient.Select([new LrclibRecord(SyncedLyrics: "[00:01.00]x")], 200_000, requireDuration: true) is null &&
            LyricsClient.Select([new LrclibRecord(SyncedLyrics: "[00:01.00]x")], 200_000, requireDuration: false) is not null,
            "records without duration are only accepted with an artist match");
        var editions = new[]
        {
            new LrclibRecord(TrackName: "Find You", ArtistName: "邓紫棋", AlbumName: "Revelación",
                Duration: 256, SyncedLyrics: "[00:01.00]different album"),
            new LrclibRecord(TrackName: "FIND YOU", ArtistName: "邓紫棋", AlbumName: "启示录",
                Duration: 256.112333, PlainLyrics: "correct album"),
        };
        check(LyricsClient.Select(editions, 0, requireDuration: false, album: "启示录")?.Lines[0].Text == "correct album",
            "the requested album outranks a different language edition before duration arrives or synced preference");
        check(LyricsClient.Select(editions, 256_000, requireDuration: true, album: "启示录")?.Lines[0].Text == "correct album",
            "a matching title and duration cannot make a different album outrank the requested album");
        check(LyricsClient.Select([editions[0]], 256_000, false, "启示录") is not null &&
            LyricsClient.Select([editions[0] with { AlbumName = null }], 256_000, false, "启示录") is not null,
            "different or missing database albums do not make otherwise usable lyrics unavailable");
        check(LyricsClient.Select([editions[1] with { AlbumName = "　ＡＬＢＵＭ　" }], 256_000, false, "album") is not null,
            "album matching accepts compatibility characters, surrounding whitespace and casing differences");
        check(LyricsClient.Select([editions[0], editions[1] with { AlbumName = "Revelacion" }],
                256_000, false, "Revelacion")?.Lines[0].Text == "correct album" &&
            LyricsClient.Select([editions[0], editions[1] with { AlbumName = "REVELATION" }],
                256_000, false, "REVELATION")?.Lines[0].Text == "correct album",
            "album preference preserves accents and distinct language edition names");
        check(LyricsClient.Select([editions[0]], 256_000, false, "  ") is not null,
            "blank album metadata retains the existing duration-based selection");
        var identityQuery = new LyricsQuery("Song", "Artist", "Album", 200_000);
        var identityRecord = new LrclibRecord("Song", "Artist", "Album", 200, PlainLyrics: "matched identity");
        check(LyricsClient.Select([identityRecord with { TrackName = "Song Two" }], identityQuery, false) is null &&
            LyricsClient.Select([identityRecord with { ArtistName = "Another Artist" }], identityQuery, false) is null,
            "broad title searches retain title and artist checks instead of matching a different singer");
        check(LyricsClient.Select([identityRecord with { TrackName = null }], identityQuery, false) is null &&
            LyricsClient.Select([identityRecord with { ArtistName = null }], identityQuery, false) is null,
            "missing title or known artist cannot establish recording identity");
        check(LyricsClient.Select([identityRecord with { Duration = null }], identityQuery, false) is not null &&
            LyricsClient.Select([identityRecord with { Duration = 0 }], identityQuery, false) is null,
            "scoped records may omit duration but an explicitly invalid duration is rejected");
        check(LyricsClient.Select([identityRecord with { Duration = 0 }], identityQuery with { DurationMs = 1000 }, false) is null &&
            LyricsClient.Select([identityRecord with { Duration = -0.25 }], identityQuery with { DurationMs = 1000 }, false) is null,
            "short recording tolerance cannot make a nonpositive candidate duration valid");
        check(LyricsClient.Select([identityRecord], identityQuery with { Artist = "", Album = "" }, true) is not null,
            "a duration-backed title fallback remains available when sender identity metadata is absent");
        check(LyricsClient.Select([identityRecord with { TrackName = "　ＳＯＮＧ　", ArtistName = " ＡＲＴＩＳＴ " }],
                identityQuery, false)?.Lines[0].Text == "matched identity",
            "recording identity accepts compatibility characters, casing and surrounding whitespace");
        check(LyricsClient.Select([identityRecord], identityQuery with { Artist = "" }, true) is not null &&
            LyricsClient.Select([identityRecord], identityQuery with { Album = "" }, true) is not null,
            "a matching known album or artist can establish identity when the other field is absent");
        check(LyricsClient.Select(Enumerable.Repeat(identityRecord with { ArtistName = "Another Artist" }, 20)
                .Append(identityRecord), identityQuery, false) is null,
            "metadata filtering cannot bypass the twenty-record search inspection limit");
        check(LyricsClient.Select([identityRecord with { ArtistName = "Artist.Name" }],
                identityQuery with { Artist = "ARTIST NAME" }, true) is not null &&
            LyricsClient.Select([identityRecord with { ArtistName = "G.E.M. 鄧紫棋" }],
                identityQuery with { Artist = "邓紫棋" }, true) is not null,
            "broad fallback accepts punctuation, whitespace and a bounded Chinese artist alias");
        check(LyricsClient.Select([identityRecord with { ArtistName = "G.E.M.鄧紫棋" }],
                identityQuery with { Artist = "G.E.M." }, true) is not null &&
            LyricsClient.Select([identityRecord with { ArtistName = "Elton John" }],
                identityQuery with { Artist = "John" }, true) is null,
            "cross-script artist aliases do not imply matching an unrelated English first or last name");
        check(LyricsClient.Select([identityRecord with { TrackName = "終於", ArtistName = "周華健", AlbumName = "啟示錄" }],
                new LyricsQuery("终于", "周华健", "启示录", 200_000), true) is not null,
            "traditional and simplified Chinese title, artist and album metadata compare equivalently");
        check(LyricsClient.Select([editions[0], editions[1] with { AlbumName = "啟示錄" }],
                256_000, false, "启示录")?.Lines[0].Text == "correct album",
            "traditional album spelling keeps the requested language edition ahead of synced alternatives");
        check(ReferenceEquals(LyricsClient.ToLyrics(new LrclibRecord(Instrumental: true)), Lyrics.Instrumental),
            "instrumental records map to the instrumental state");
        check(LyricsClient.CleanTitle("Song (Remastered 2011) [Live]") == "Song" && LyricsClient.CleanTitle("光年之外（电影版）") == "光年之外",
            "bracketed title suffixes are removed for the fallback search");
        string unclosedTitle = new('(', 900_000);
        var titleClock = System.Diagnostics.Stopwatch.StartNew();
        check(LyricsClient.CleanTitle(unclosedTitle) == unclosedTitle && titleClock.Elapsed < TimeSpan.FromSeconds(5),
            "network-sized unclosed title brackets retain their text without a quadratic fallback-search stall");

        var json = System.Text.Json.JsonSerializer.Deserialize<LrclibRecord>(
            """{"id":1,"trackName":"T","artistName":"A","albumName":"B","duration":233.0,"instrumental":false,"plainLyrics":"p","syncedLyrics":"[00:01.00]s"}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        check(json is { TrackName: "T", Duration: 233.0 } && LyricsClient.ToLyrics(json)!.Kind == LyricsKind.Synced,
            "LRCLIB JSON field names bind");

        check(new ReceiverPreferences().ShowLyrics, "automatic lyrics display defaults on");
        CheckRequestsAsync(check).GetAwaiter().GetResult();
    }

    private static async Task CheckRequestsAsync(Action<bool, string> check)
    {
        var query = new LyricsQuery("Song (Remastered)", "Artist", "Album", 200_000);
        var paths = new List<Uri>();
        using (var client = new LyricsClient(new RequestHandler((request, _) =>
        {
            paths.Add(request.RequestUri!);
            return Task.FromResult(paths.Count < 4
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json("""[{"trackName":"Song","artistName":"Artist","albumName":"Album","duration":200,"syncedLyrics":"[00:01.00]fallback"}]"""));
        })))
        {
            var result = await client.FindAsync(query, CancellationToken.None);
            check(!result.Failed && result.Lyrics?.Lines[0].Text == "fallback" && paths.Count == 4 &&
                paths[0].AbsolutePath == "/api/get" && paths[1].Query.Contains("artist_name=Artist") &&
                paths[1].Query.Contains("album_name=Album") && paths[2].Query.Contains("artist_name=Artist") &&
                !paths[2].Query.Contains("album_name=") && paths[3].Query == "?track_name=Song",
                "HTTP lookup tries album-scoped then original artist search before its cleaned-title fallback");
            var cached = await client.FindAsync(query, CancellationToken.None);
            check(ReferenceEquals(cached.Lyrics, result.Lyrics) && paths.Count == 4, "successful lyrics lookups are cached");
        }

        foreach (long duration in new[] { 0L, 256_000L })
        {
            var requests = new List<Uri>();
            using var client = new LyricsClient(new RequestHandler((request, _) =>
            {
                requests.Add(request.RequestUri!);
                return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/get")
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json("""[{"trackName":"Find You","artistName":"邓紫棋","albumName":"Revelación","duration":256,"syncedLyrics":"[00:01.00]different edition"},{"trackName":"FIND YOU","artistName":"邓紫棋","albumName":"启示录","duration":256.112333,"plainLyrics":"correct album line"}]"""));
            }));
            var result = await client.FindAsync(new LyricsQuery("FIND YOU", "邓紫棋", "启示录", duration), CancellationToken.None);
            check(!result.Failed && result.Lyrics?.Lines[0].Text == "correct album line" &&
                requests.Count == (duration == 0 ? 1 : 2) && requests.All(uri =>
                    Uri.UnescapeDataString(uri.Query).Contains("album_name=启示录", StringComparison.Ordinal)),
                "FIND YOU selects its requested album instead of a same-duration language edition " +
                    (duration == 0 ? "before duration arrives" : "after the exact lookup misses"));
        }

        var identityQuery = new LyricsQuery("Song", "Artist", "Album", 200_000);
        var identityRecord = new LrclibRecord("Song", "Artist", "Album", 200, PlainLyrics: "matched identity");
        foreach (var candidate in new[]
        {
            (Name: "an alternate API title", Record: identityRecord with { TrackName = "Translated Title" }, Accept: true),
            (Name: "an alternate API artist", Record: identityRecord with { ArtistName = "Artist Alias" }, Accept: true),
            (Name: "another album", Record: identityRecord with { AlbumName = "Another Album" }, Accept: true),
            (Name: "missing duration", Record: identityRecord with { Duration = null }, Accept: true),
            (Name: "nonpositive duration", Record: identityRecord with { Duration = 0 }, Accept: false),
            (Name: "a different recording length", Record: identityRecord with { Duration = 205 }, Accept: false),
        })
        {
            int requests = 0;
            using var client = new LyricsClient(new RequestHandler((request, _) =>
            {
                ++requests;
                return Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("/get")
                    ? System.Text.Json.JsonSerializer.Serialize(candidate.Record)
                    : System.Text.Json.JsonSerializer.Serialize(new[] { candidate.Record })));
            }));
            var result = await client.FindAsync(identityQuery, CancellationToken.None);
            check(!result.Failed && (result.Lyrics is not null) == candidate.Accept && requests == (candidate.Accept ? 1 : 4),
                "scoped API results " + (candidate.Accept ? "retain " : "reject ") + candidate.Name);
        }
        using (var client = new LyricsClient(new RequestHandler((_, _) => Task.FromResult(Json(
            """[{"trackName":"Song","artistName":"Artist","albumName":"Album","duration":200,"plainLyrics":"duration fallback"}]""")))))
        {
            var result = await client.FindAsync(identityQuery with { Artist = "", Album = "" }, CancellationToken.None);
            check(!result.Failed && result.Lyrics?.Lines[0].Text == "duration fallback",
                "an HTTP title and duration fallback remains available when sender identity metadata is absent");
        }

        foreach (var record in new[]
        {
            identityRecord with { ArtistName = "Artist Alias", AlbumName = "Another Album" },
            identityRecord with { AlbumName = null },
            identityRecord with { Duration = null },
        })
        {
            var requests = new List<Uri>();
            using var client = new LyricsClient(new RequestHandler((request, _) =>
            {
                requests.Add(request.RequestUri!);
                return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/get") || request.RequestUri.Query.Contains("album_name=")
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json(System.Text.Json.JsonSerializer.Serialize(new[] { record })));
            }));
            var result = await client.FindAsync(identityQuery, CancellationToken.None);
            check(!result.Failed && result.Lyrics?.Lines[0].Text == "matched identity" && requests.Count == 3 &&
                requests[2].Query.Contains("artist_name=Artist") && !requests[2].Query.Contains("album_name="),
                "original artist search restores coverage after album-specific metadata does not match");
        }

        using (var client = new LyricsClient(new RequestHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/get") || request.RequestUri.Query.Contains("artist_name=")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json("""[{"trackName":"Song","artistName":"AnotherArtist","duration":200,"syncedLyrics":"[00:01.00]different singer"},{"trackName":"Song","artistName":"Artist.Name","albumName":"Alternate Album","duration":200,"plainLyrics":"artist alias fallback"}]""")))))
        {
            var result = await client.FindAsync(identityQuery with { Artist = "ARTIST NAME" }, CancellationToken.None);
            check(!result.Failed && result.Lyrics?.Lines[0].Text == "artist alias fallback",
                "broad fallback accepts an artist punctuation alias without preferring an unrelated synced singer");
        }

        var scriptRequests = new List<string>();
        using (var client = new LyricsClient(new RequestHandler((request, _) =>
        {
            string search = Uri.UnescapeDataString(request.RequestUri!.Query);
            scriptRequests.Add(search);
            return Task.FromResult(search.Contains("track_name=愛你") && search.Contains("artist_name=周華健") &&
                search.Contains("album_name=啟示錄")
                    ? Json("""[{"trackName":"愛你","artistName":"周華健","albumName":"啟示錄","duration":200,"plainLyrics":"traditional lookup"}]""")
                    : Json("[]"));
        })))
        {
            var result = await client.FindAsync(new LyricsQuery("爱你", "周华健", "启示录", 0), CancellationToken.None);
            check(!result.Failed && result.Lyrics?.Lines[0].Text == "traditional lookup" && scriptRequests.Count == 3 &&
                scriptRequests[0].Contains("track_name=爱你") && scriptRequests[^1].Contains("track_name=愛你"),
                "outgoing searches retry traditional title, artist and album forms before dropping the album constraint");
        }

        var contextualRequests = new List<string>();
        using (var client = new LyricsClient(new RequestHandler((request, _) =>
        {
            string search = Uri.UnescapeDataString(request.RequestUri!.Query);
            contextualRequests.Add(search);
            return Task.FromResult(search == "?q=周華健 啟示錄"
                ? Json("""[{"trackName":"終於","artistName":"周華健","albumName":"啟示錄","duration":200,"plainLyrics":"contextual traditional title"}]""")
                : Json("[]"));
        })))
        {
            var result = await client.FindAsync(new LyricsQuery("终于", "周华健", "启示录", 0), CancellationToken.None);
            check(!result.Failed && result.Lyrics?.Lines[0].Text == "contextual traditional title" &&
                contextualRequests.Count == 6 && contextualRequests.Take(3).All(search => search.Contains("track_name=")) &&
                contextualRequests[^1] == "?q=周華健 啟示錄",
                "album discovery recovers contextual traditional titles that character-only outbound conversion cannot spell");
        }

        var boundedRequests = new List<string>();
        using (var client = new LyricsClient(new RequestHandler((request, _) =>
        {
            boundedRequests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        })))
        {
            var result = await client.FindAsync(new LyricsQuery("爱愛你", "周华健", "启示录", 200_000), CancellationToken.None);
            check(result is { Failed: false, Lyrics: null } && boundedRequests.Count == 10 &&
                boundedRequests.Distinct(StringComparer.Ordinal).Count() == boundedRequests.Count,
                "original and two canonical Chinese scripts retain ten distinct sequential HTTP attempts at most");
        }

        int delayedRequests = 0, activeRequests = 0, maximumActive = 0;
        using (var client = new LyricsClient(new RequestHandler(async (_, token) =>
        {
            ++delayedRequests;
            maximumActive = Math.Max(maximumActive, ++activeRequests);
            try { await Task.Delay(90, token); return new HttpResponseMessage(HttpStatusCode.NotFound); }
            finally { --activeRequests; }
        }), TimeSpan.FromMilliseconds(150)))
        {
            var result = await client.FindAsync(identityQuery, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            check(result is { Failed: true, Lyrics: null } && delayedRequests < 4 && maximumActive == 1 && activeRequests == 0,
                "one lookup deadline bounds sequential slow misses instead of granting every search variant a new budget");
        }

        int attempts = 0;
        using (var stalled = new StalledReadStream())
        using (var client = new LyricsClient(new RequestHandler((_, _) => Task.FromResult(++attempts == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled) }
            : Json("""{"trackName":"Song (Remastered)","artistName":"Artist","albumName":"Album","duration":200,"syncedLyrics":"[00:01.00]retry"}"""))), TimeSpan.FromMilliseconds(100)))
        {
            var result = await client.FindAsync(query, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            check(stalled.ReadStarted.Task.IsCompleted && result is { Failed: true, Lyrics: null },
                "request deadline covers a body that stalls after successful response headers");
            var retry = await client.FindAsync(query, CancellationToken.None);
            check(!retry.Failed && retry.Lyrics?.Lines[0].Text == "retry" && attempts == 2,
                "timed-out lyrics requests are not cached and can be retried");
        }

        using (var stalled = new StalledReadStream())
        using (var caller = new CancellationTokenSource())
        using (var client = new LyricsClient(new RequestHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled) }))))
        {
            var lookup = client.FindAsync(query, caller.Token);
            await stalled.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            bool canceled = false;
            try { await lookup.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled, "caller cancellation escapes instead of becoming a failed lyrics result");
        }

        int missingRequests = 0;
        using (var client = new LyricsClient(new RequestHandler((_, _) =>
        {
            ++missingRequests;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        })))
        {
            var missing = await client.FindAsync(query, CancellationToken.None);
            int requests = missingRequests;
            await client.FindAsync(query, CancellationToken.None);
            check(missing is { Failed: false, Lyrics: null } && requests == 4 && missingRequests == requests,
                "a completed no-match lookup is cached without repeating HTTP requests");
        }

        using (var client = new LyricsClient(new RequestHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/get")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json("""[null,{"trackName":"Song (Remastered)","artistName":"Artist","albumName":"Album","duration":200,"syncedLyrics":"[00:01.00]valid after null"}]""")))))
        {
            var result = await client.FindAsync(query, CancellationToken.None);
            check(!result.Failed && result.Lyrics?.Lines[0].Text == "valid after null",
                "null search records are ignored without disabling lyrics or discarding valid records");
        }

        using (var body = new GeneratedReadStream(8 * 1024 * 1024))
        using (var client = new LyricsClient(new RequestHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }))))
        {
            var result = await client.FindAsync(query, CancellationToken.None);
            check(result is { Failed: true, Lyrics: null } && body.BytesRead <= 1024 * 1024 + 16 * 1024,
                "an unknown-length lyrics response is rejected after the bounded read instead of buffering its entire body");
        }

        using (var body = new StalledReadStream())
        using (var client = new LyricsClient(new RequestHandler((_, _) =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = 1024 * 1024 + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        })))
        {
            var result = await client.FindAsync(query, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            check(result is { Failed: true, Lyrics: null } && !body.ReadStarted.Task.IsCompleted,
                "an oversized advertised lyrics response is rejected before reading the body");
        }

        using (var client = new LyricsClient(new RequestHandler((_, _) => Task.FromResult(Json("[broken JSON")))))
        {
            var result = await client.FindAsync(query, CancellationToken.None);
            check(result is { Failed: true, Lyrics: null }, "malformed lyrics JSON reports a lookup failure without escaping");
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class RequestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class GeneratedReadStream(int length) : Stream
    {
        private int _remaining = length;
        internal int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(buffer.Length, _remaining);
            buffer.Span[..count].Fill((byte)' ');
            _remaining -= count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledReadStream : Stream
    {
        internal readonly TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
