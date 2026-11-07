using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClearCast;

using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These tests require Windows file-sharing semantics.");
string? output = args.Length == 2 && args[0] == "--output" ? Path.GetFullPath(args[1]) : null;
string disposable = Directory.CreateTempSubdirectory("ClearCast-managed-state-").FullName;
try
{
    var result = RunTests(disposable);
    string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (output is not null) { Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, json); }
}
catch (Exception error)
{
    string json = JsonSerializer.Serialize(new { status = "fail", failure = error.ToString() }, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (output is not null) { Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, json); }
    Environment.ExitCode = 1;
}
finally { Directory.Delete(disposable, recursive: true); }

static object RunTests(string root)
{
    Directory.CreateDirectory(Path.Combine(root, ".clearcast-state"));
    string state = Path.Combine(root, ".clearcast-state");
    string clients = Path.Combine(state, "clients.txt");
    int checks = 0;
    void Check(bool test, string name) { ++checks; if (!test) throw new InvalidOperationException(name); }
    string Key(int shift) => Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + shift)).ToArray());
    var watch = Stopwatch.StartNew();
    string key1 = Key(0), key2 = Key(1), key3 = Key(2), key4 = Key(3);

    File.WriteAllText(clients, key1 + "\r\n" + key2 + ",2026-11-07 00:00:00,AA:BB,设备,名称\n" + key2 + ",date,id,\n" + "invalid-row\n", new UTF8Encoding(false));
    var pairings = AirPlayReceiver.GetSavedPairings(root);
    Check(pairings.Count == 2, "valid unique keys only");
    Check(pairings.Single(p => p.Id == key2).Name == "设备,名称", "non-empty UTF-8 metadata and commas retained");
    Check(pairings.Single(p => p.Id == key1).Name is null, "legacy CRLF key-only row");
    AirPlayReceiver.RemovePairing(root, key2);
    Check(File.ReadAllText(clients) == key1 + "\r\ninvalid-row\n", "remove duplicate key while preserving unrelated bytes");
    byte[] beforeMissing = File.ReadAllBytes(clients);
    AirPlayReceiver.RemovePairing(root, key4);
    Check(File.ReadAllBytes(clients).SequenceEqual(beforeMissing), "missing key leaves file unchanged");
    Check(Directory.GetFiles(state, "*.tmp").Length == 0, "temporary files removed");

    string identity = Path.Combine(state, "receiver.pem");
    string retiredIdentity = Path.Combine(state, "receiver.unpaired.pem");
    byte[] liveKey = Encoding.UTF8.GetBytes("fixture-live-identity");
    byte[] oldBackup = Encoding.UTF8.GetBytes("fixture-previous-retired-identity");
    string rowA = key1 + ",date,id-A,设备 A\n";
    string rowB = key2 + ",date,id-B,设备 B,名称\r\n";
    void PrepareRemoval()
    {
        File.WriteAllText(clients, rowA + rowB + "unrelated legacy row\n", new UTF8Encoding(false));
        File.WriteAllBytes(identity, liveKey);
        File.WriteAllBytes(retiredIdentity, oldBackup);
    }
    void CheckRollback(string name)
    {
        Check(File.ReadAllText(clients) == rowA + rowB + "unrelated legacy row\n", name + " clients unchanged");
        Check(File.ReadAllBytes(identity).SequenceEqual(liveKey), name + " live identity restored");
        Check(File.ReadAllBytes(retiredIdentity).SequenceEqual(oldBackup), name + " old backup restored");
        Check(Directory.GetFiles(state, "*.tmp").Length == 0, name + " no staging files remain");
    }
    PrepareRemoval();
    AirPlayReceiver.RemovePairing(root, key1);
    Check(File.ReadAllText(clients) == rowB + "unrelated legacy row\n", "A removed, B bytes preserved");
    Check(AirPlayReceiver.GetSavedPairings(root).Single().Name == "设备 B,名称", "B name preserved");
    Check(!File.Exists(identity) && File.ReadAllBytes(retiredIdentity).SequenceEqual(liveKey), "successful removal retires server identity");
    Check(Directory.GetFiles(state, "*.tmp").Length == 0, "committed removal cleans unique backup staging");
    PrepareRemoval();
    AirPlayReceiver.RemovePairing(root, key4);
    CheckRollback("missing ID");
    PrepareRemoval();
    using (var heldIdentity = new FileStream(identity, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool refused = false;
        try { AirPlayReceiver.RemovePairing(root, key1); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
        Check(refused, "locked live identity aborts removal");
    }
    CheckRollback("live identity move failure");
    PrepareRemoval();
    using (var heldBackup = new FileStream(retiredIdentity, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool refused = false;
        try { AirPlayReceiver.RemovePairing(root, key1); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
        Check(refused, "locked retired backup aborts removal");
    }
    CheckRollback("retired backup move failure");
    PrepareRemoval();
    using (var heldClients = new FileStream(clients, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool refused = false;
        try { AirPlayReceiver.RemovePairing(root, key1); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
        Check(refused, "locked clients replacement aborts removal");
    }
    CheckRollback("clients atomic replacement failure");
    PrepareRemoval();
    File.Delete(identity);
    AirPlayReceiver.RemovePairing(root, key1);
    Check(File.ReadAllText(clients) == rowB + "unrelated legacy row\n", "removal without existing server key still revokes A");
    Check(File.ReadAllBytes(retiredIdentity).SequenceEqual(oldBackup), "missing live identity preserves retired backup");

    File.WriteAllText(clients, key1 + "\n" + key2, new UTF8Encoding(false));
    Check(AirPlayReceiver.GetSavedPairings(root).Count == 2, "last key without LF");
    File.WriteAllText(clients, key1 + "!\n" + "!" + key2[1..] + "\n" + key2 + ",date,id,Valid\r\n", new UTF8Encoding(false));
    Check(AirPlayReceiver.GetSavedPairings(root).Count == 1, "reject invalid Base64 and key delimiters");

    string oversized = key3 + ",date,id," + new string('x', 2 * 1024 * 1024) + "\n";
    File.WriteAllText(clients, oversized + key4 + ",date,id,Following\n", new UTF8Encoding(false));
    long oversizedReadBytes = new FileInfo(clients).Length;
    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    pairings = AirPlayReceiver.GetSavedPairings(root);
    long oversizedReadAllocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    Check(pairings.Count == 2 && pairings.Single(p => p.Id == key4).Name == "Following", "oversized row prefix does not split into extra records");
    Check(pairings.Single(p => p.Id == key3).Name!.Length <= 4096, "oversized metadata bounded");
    Check(oversizedReadAllocations < 128 * 1024, "large damaged file read allocations bounded");
    AirPlayReceiver.RemovePairing(root, key4);
    Check(File.ReadAllText(clients) == oversized, "removal preserves oversized unrelated row byte-for-byte");

    using (var held = new FileStream(Path.Combine(state, "receiver.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        bool refused = false;
        try { AirPlayReceiver.ClearPairings(root); } catch (IOException) { refused = true; }
        Check(refused && File.Exists(clients), "active receiver lock prevents pairing mutation");
    }
    AirPlayReceiver.ClearPairings(root);
    Check(!File.Exists(clients) && AirPlayReceiver.GetSavedPairings(root).Count == 0, "clear pairings and missing file");

    string preferences = Path.Combine(state, "preferences.json");
    File.WriteAllText(preferences, "{\"Decoder\":999,\"Theme\":999,\"Language\":999,\"OriginalSize\":true}");
    var settings = ReceiverPreferences.Load(root);
    Check(settings.Decoder == DecoderMode.Automatic && settings.Theme == AppTheme.System && settings.Language == AppLanguage.SimplifiedChinese && settings.OriginalSize, "invalid saved enums replaced with defaults");
    File.WriteAllText(preferences, "{broken json");
    Check(ReceiverPreferences.Load(root) == new ReceiverPreferences(), "malformed settings recover");
    settings = new ReceiverPreferences(DecoderMode.Software, true, AppTheme.Dark, AppLanguage.English);
    settings.Save(root);
    Check(ReceiverPreferences.Load(root) == settings, "settings round trip");
    settings = settings with { Theme = AppTheme.Light };
    settings.Save(root);
    Check(ReceiverPreferences.Load(root) == settings && Directory.GetFiles(state, "*.tmp").Length == 0, "settings atomic overwrite and cleanup");

    var texts = new UiText();
    texts.SetReceiverName("Test-PC");
    texts.SetEnglish(true);
    var properties = typeof(UiText).GetProperties().Where(p => p.PropertyType == typeof(string)).ToArray();
    foreach (var property in properties)
    {
        string value = (string)property.GetValue(texts)!;
        Check(!string.IsNullOrWhiteSpace(value), "nonempty UI string " + property.Name);
        if (property.Name != nameof(UiText.ChineseLanguage))
            Check(!Regex.IsMatch(value, "[\\p{IsCJKUnifiedIdeographs}]"), "English UI string " + property.Name);
    }
    var dynamicEnglish = (Dictionary<string, string>)typeof(UiText).GetField("DynamicEnglish", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    foreach (var entry in dynamicEnglish)
    {
        Check(texts.Translate(entry.Key) == entry.Value && !Regex.IsMatch(entry.Value, "[\\p{IsCJKUnifiedIdeographs}]"), "dynamic English " + entry.Key);
    }
    texts.SetEnglish(false);
    foreach (string message in dynamicEnglish.Keys) Check(texts.Translate(message) == message, "Chinese dynamic " + message);

    File.WriteAllText(Path.Combine(state, "receiver.log"), string.Join('\n', Enumerable.Range(0, 500).Select(i => $"entry-{i:D4} " + new string('x', 80))) + "\n", new UTF8Encoding(false));
    string diagnostics = AirPlayReceiver.ReadDiagnostics(root);
    Check(diagnostics.Length <= 16384 && diagnostics.EndsWith("\n") && diagnostics.Contains("entry-0499") && !diagnostics.Contains("entry-0000"), "bounded latest diagnostic log tail");

    var report = new { status = "pass", checks, elapsedMilliseconds = watch.ElapsedMilliseconds, oversizedReadBytes, oversizedReadAllocations, localizationProperties = properties.Length, localizedDynamicStates = dynamicEnglish.Count, stateDirectory = root };
    return report;
}
