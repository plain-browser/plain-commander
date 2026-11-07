using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
            var settings = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            Sanitize(settings, SettingsJsonContext.Default.AppSettings);
            return settings;
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Settings file {Path} is corrupt, using defaults", FilePath);
            TryMoveAside();
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not corrupt, only unreadable now: keep the file where it is.
            Log.Warning(ex, "Cannot read settings {Path}", FilePath);
            return new AppSettings();
        }
    }

    /// <summary>
    /// A JSON null replaces a section or list with null; put the default back in its place and drop null list items,
    /// so the rest of the app never sees a null where the settings classes promise a value.
    /// </summary>
    private static void Sanitize(object value, JsonTypeInfo info)
    {
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object:
                var fresh = info.CreateObject?.Invoke();
                foreach (var property in info.Properties)
                {
                    if (property.Get is null || property.Set is null)
                    {
                        continue;
                    }

                    var current = property.Get(value);
                    if (current is null)
                    {
                        // Defaults come from a new instance; a property that defaults to null (Window) stays null.
                        if (fresh is not null && property.Get(fresh) is { } fallback)
                        {
                            property.Set(value, fallback);
                        }
                    }
                    else if (SettingsJsonContext.Default.GetTypeInfo(property.PropertyType) is { } inner)
                    {
                        Sanitize(current, inner);
                    }
                }

                break;
            case JsonTypeInfoKind.Enumerable when value is IList list:
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] is null)
                    {
                        list.RemoveAt(i);
                    }
                }

                SanitizeItems(list, info.ElementType);
                break;
            case JsonTypeInfoKind.Dictionary when value is IDictionary dictionary:
                foreach (var key in dictionary.Keys.Cast<object>().Where(k => dictionary[k] is null).ToList())
                {
                    dictionary.Remove(key);
                }

                SanitizeItems(dictionary.Values, info.ElementType);
                break;
        }
    }

    private static void SanitizeItems(IEnumerable items, Type? elementType)
    {
        if (elementType is null || SettingsJsonContext.Default.GetTypeInfo(elementType) is not { } info)
        {
            return;
        }

        foreach (var item in items)
        {
            Sanitize(item, info);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
