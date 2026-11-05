using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace PlainCommander.Core.Settings;

/// <summary>Reads and writes settings.json. A broken file never stops the app: it is set aside and defaults are used.</summary>
public sealed class SettingsStore
{
    public SettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlainCommander", "settings.json");

    public string FilePath { get; }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Settings file {Path} is corrupt, using defaults", FilePath);
            TryMoveAside();
            return new AppSettings();
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Cannot read settings {Path}", FilePath);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var dir = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Write next to the target and swap, so a crash mid-write never leaves half a file.
        var temp = FilePath + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
        }

        File.Move(temp, FilePath, overwrite: true);
    }

    private void TryMoveAside()
    {
        try
        {
            File.Move(FilePath, FilePath + ".bad", overwrite: true);
        }
        catch (IOException)
        {
            // best effort; defaults are used either way
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
