using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using ClearCast;

internal static class BackpressureTests
{
    internal static void Run(Action<bool, string> check, string root)
    {
        var gate = new ReceiverCommandGate();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int executed = 0;
        Task active = gate.RunAsync(() =>
        {
            Interlocked.Increment(ref executed);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Held command was not released.");
        });
        try
        {
            check(entered.Wait(TimeSpan.FromSeconds(5)), "slow receiver command starts");
            int rejected = 0;
            bool completedImmediately = true;
            for (int index = 0; index < 5000; index++)
            {
                Task overflow = gate.RunAsync(() => Interlocked.Increment(ref executed));
                completedImmediately &= overflow.IsCompleted;
                try { overflow.GetAwaiter().GetResult(); }
                catch (Win32Exception error) when (error.NativeErrorCode == 170) { rejected++; }
            }
            check(completedImmediately && rejected == 5000 && executed == 1,
                "command flood retains one admitted operation and rejects all overflow without queued workers");
            gate.Suspend();
            bool canceled = false;
            try { gate.RunAsync(() => Interlocked.Increment(ref executed)).GetAwaiter().GetResult(); }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223) { canceled = true; }
            check(canceled && executed == 1, "shutdown rejects commands without waiting for the held worker");
        }
        finally { release.Set(); active.GetAwaiter().GetResult(); }
        gate.Resume();
        gate.RunAsync(() => Interlocked.Increment(ref executed)).GetAwaiter().GetResult();
        check(executed == 2, "command admission resumes after a failed stop is recovered");
        bool failed = false;
        try { gate.RunAsync(() => throw new IOException("fixture command failure")).GetAwaiter().GetResult(); }
        catch (IOException) { failed = true; }
        gate.RunAsync(() => Interlocked.Increment(ref executed)).GetAwaiter().GetResult();
        check(failed && executed == 3, "command failure releases admission for the next operation");

        var cadence = new AdaptiveWorkCadence(33, 264);
        for (int sample = 0; sample < 1000; sample++) cadence.Observe(TimeSpan.FromMilliseconds(2));
        check(cadence.IntervalMilliseconds == 33 && !cadence.IsThrottled && cadence.Multiplier == 1,
            "healthy visual work preserves the normal sampling cadence without a quality downgrade");
        cadence.Observe(TimeSpan.FromMilliseconds(100));
        cadence.Observe(TimeSpan.FromMilliseconds(100));
        check(cadence.IntervalMilliseconds == 33, "isolated slow visual samples do not lower normal quality");
        cadence.Observe(TimeSpan.FromMilliseconds(100));
        check(cadence.IntervalMilliseconds == 66 && cadence.Multiplier == 2,
            "sustained measured visual overload applies bounded backpressure");
        for (int sample = 0; sample < 30; sample++) cadence.Observe(TimeSpan.FromSeconds(1));
        check(cadence.IntervalMilliseconds == 264 && cadence.Multiplier == 8,
            "continued overload cannot create an unbounded visual delay or sampling multiplier");
        for (int sample = 0; sample < 15; sample++) cadence.Observe(TimeSpan.FromMilliseconds(2));
        check(cadence.IntervalMilliseconds == 264, "recovery requires a sustained healthy window rather than oscillating after one fast frame");
        cadence.Observe(TimeSpan.FromMilliseconds(2));
        check(cadence.IntervalMilliseconds == 132, "visual recovery restores one quality tier at a time");
        for (int sample = 0; sample < 32; sample++) cadence.Observe(TimeSpan.FromMilliseconds(2));
        check(cadence.IntervalMilliseconds == 33 && !cadence.IsThrottled,
            "healthy work automatically returns to the exact normal cadence");
        for (int sample = 0; sample < 100; sample++)
        {
            cadence.Observe(TimeSpan.FromMilliseconds(100));
            cadence.Observe(TimeSpan.FromMilliseconds(2));
        }
        check(!cadence.IsThrottled, "intermittent isolated spikes do not accumulate into a permanent quality downgrade");
        cadence.Observe(TimeSpan.FromSeconds(1));
        cadence.Observe(TimeSpan.FromSeconds(1));
        cadence.Reset();
        cadence.Observe(TimeSpan.FromSeconds(1));
        check(!cadence.IsThrottled, "a new media generation resets stale pressure and its unfinished overload window");

        string fixture = Path.Combine(root, "backpressure");
        string state = Directory.CreateDirectory(Path.Combine(fixture, ".clearcast-state")).FullName;
        string preferences = Path.Combine(state, "preferences.json");
        File.WriteAllText(preferences, "{\"Decoder\":1}".PadRight(4096));
        check(ReceiverPreferences.Load(fixture).Decoder == DecoderMode.Software, "bounded preference record accepts the exact byte limit");
        File.AppendAllText(preferences, " ");
        check(ReceiverPreferences.Load(fixture) == new ReceiverPreferences(), "oversized preferences default instead of applying truncated JSON");
        File.WriteAllText(preferences, "{\"Decoder\":1}", new UTF8Encoding(true));
        check(ReceiverPreferences.Load(fixture).Decoder == DecoderMode.Software, "bounded preference load retains UTF-8 BOM compatibility");

        string clients = Path.Combine(state, "clients.txt");
        string retained = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        string removed = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        foreach (int padding in new[] { 0, 8190, 8191, 8192, 8193 })
        {
            string expected = new string('x', padding) + "\n" + retained + ",date,id,keep\r\n";
            File.WriteAllText(clients, expected + removed + "\r\n" + removed, new UTF8Encoding(false));
            AirPlayReceiver.RemovePairing(fixture, removed);
            check(File.ReadAllText(clients) == expected, "streamed pairing removal retains exact bytes at buffer boundary " + padding);
            File.WriteAllText(clients, expected + retained + ",date,id,Remove, 设备\n" + retained + "\r\n", new UTF8Encoding(false));
            AirPlayReceiver.RemovePairing(fixture, retained, "Remove, 设备");
            check(File.ReadAllText(clients) == expected,
                "name-scoped removal preserves another same-key name and exact bytes at buffer boundary " + padding);
        }
        File.WriteAllText(clients, retained + ",date,id," + new string('x', 8 * 1024 * 1024) + "\n", new UTF8Encoding(false));
        byte[] expectedHash;
        using (var input = File.OpenRead(clients)) expectedHash = SHA256.HashData(input);
        File.AppendAllText(clients, removed + "\n", new UTF8Encoding(false));
        long before = GC.GetAllocatedBytesForCurrentThread();
        AirPlayReceiver.RemovePairing(fixture, removed);
        long allocations = GC.GetAllocatedBytesForCurrentThread() - before;
        check(allocations < 256 * 1024, "pairing removal memory stays bounded for an eight-MiB damaged row");
        using (var input = File.OpenRead(clients))
            check(SHA256.HashData(input).SequenceEqual(expectedHash), "large unrelated pairing record remains byte-for-byte unchanged");
        File.AppendAllText(clients, retained + ",date,id,Remove\n" + retained + "\n", new UTF8Encoding(false));
        before = GC.GetAllocatedBytesForCurrentThread();
        AirPlayReceiver.RemovePairing(fixture, retained, "Remove");
        allocations = GC.GetAllocatedBytesForCurrentThread() - before;
        check(allocations < 256 * 1024, "name-scoped removal stays bounded beside an eight-MiB same-key metadata row");
        using (var input = File.OpenRead(clients))
            check(SHA256.HashData(input).SequenceEqual(expectedHash),
                "name-scoped removal preserves oversized ambiguous metadata while deleting the selected complete name");
        using (var oversized = File.Open(clients, FileMode.Create)) oversized.SetLength(16 * 1024 * 1024 + 1);
        before = GC.GetAllocatedBytesForCurrentThread();
        bool oversizedRejected = false;
        try { AirPlayReceiver.GetSavedPairings(fixture); } catch (IOException) { oversizedRejected = true; }
        check(oversizedRejected && GC.GetAllocatedBytesForCurrentThread() - before < 128 * 1024,
            "pairing load rejects an oversized registry before scanning or materializing it");
        using (var writer = new StreamWriter(clients, false, new UTF8Encoding(false)))
            for (int index = 0; index < 4097; index++)
                writer.WriteLine(Convert.ToBase64String(SHA256.HashData(BitConverter.GetBytes(index))));
        bool tooManyRejected = false;
        try { AirPlayReceiver.GetSavedPairings(fixture); } catch (IOException) { tooManyRejected = true; }
        check(tooManyRejected, "pairing load rejects more than 4096 distinct clients without returning a partial trust list");
        using (var writer = new StreamWriter(clients, false, new UTF8Encoding(false)))
            for (int index = 0; index < 4097; index++)
                writer.WriteLine(retained + ",date,shared-id,Fixture " + index);
        bool tooManyNamesRejected = false;
        try { AirPlayReceiver.GetSavedPairings(fixture); } catch (IOException) { tooManyNamesRejected = true; }
        check(tooManyNamesRejected, "pairing load bounds distinct saved names even when every name shares one trust key");
        File.Delete(clients);
        AirPlayReceiver.RemovePairing(fixture, removed);
        check(!File.Exists(clients), "streamed removal retains missing-file no-op semantics");

        string log = Path.Combine(state, "receiver.log");
        File.WriteAllText(log, new string('x', 64 * 1024));
        check(AirPlayReceiver.ReadDiagnostics(fixture).Length == 0, "diagnostic tail drops an incomplete leading line within its byte bound");
        File.WriteAllText(log, "\uFEFFlast diagnostic\n", new UTF8Encoding(false));
        check(AirPlayReceiver.ReadDiagnostics(fixture) == "last diagnostic\n", "bounded diagnostics retain BOM compatibility");
    }
}
