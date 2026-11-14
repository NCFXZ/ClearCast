using System.IO;
using System.Text.Json;

namespace ClearCast;

internal enum AppTheme { System, Light, Dark }
internal enum AppLanguage { SimplifiedChinese, English }

internal sealed record ReceiverPreferences(
    DecoderMode Decoder = DecoderMode.Automatic,
    bool OriginalSize = false,
    AppTheme Theme = AppTheme.System,
    AppLanguage Language = AppLanguage.SimplifiedChinese,
    bool AutoFullScreen = true,
    bool ShowLyrics = true)
{
    private static string FilePath(string root) => Path.Combine(root, ".clearcast-state", "preferences.json");

    internal static ReceiverPreferences Load(string root)
    {
        try
        {
            // This small local record must not make a damaged or concurrently
            // growing file consume unbounded memory. Never apply a truncated JSON.
            using var stream = new FileStream(FilePath(root), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] bytes = new byte[4097];
            int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count > 4096) return new ReceiverPreferences();
            ReadOnlySpan<byte> json = bytes.AsSpan(0, count);
            if (json.StartsWith("\uFEFF"u8)) json = json[3..];
            var preferences = JsonSerializer.Deserialize<ReceiverPreferences>(json);
            if (preferences is not null)
                return preferences with
                {
                    Decoder = Enum.IsDefined(preferences.Decoder) ? preferences.Decoder : DecoderMode.Automatic,
                    Theme = Enum.IsDefined(preferences.Theme) ? preferences.Theme : AppTheme.System,
                    Language = Enum.IsDefined(preferences.Language) ? preferences.Language : AppLanguage.SimplifiedChinese
                };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new ReceiverPreferences();
    }

    internal void Save(string root)
    {
        string path = FilePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
