using System.IO;
using System.Text.Json;
using AutoMarkerReID.Application;
using AutoMarkerReID.Persistence;

namespace AutoMarkerReID.App;

public sealed class UserPreferencesStore(StoragePaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(paths.BaseDirectory, "user-settings.json");
    private readonly Lock _sync = new();

    public UserPreferences Load()
    {
        lock (_sync)
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<UserPreferences>(File.ReadAllBytes(_path)) ?? new UserPreferences()
                    : new UserPreferences();
            }
            catch (JsonException)
            {
                return new UserPreferences();
            }
            catch (IOException)
            {
                return new UserPreferences();
            }
            catch (UnauthorizedAccessException)
            {
                return new UserPreferences();
            }
        }
    }

    public void Apply(UserSelectionState selection)
    {
        var value = Load();
        selection.RecognitionScope = value.RecognitionScope;
        selection.TargetQuery = value.TargetQuery;
        selection.AppearanceEnabled = value.AppearanceEnabled;
        // Capture saving is intentionally always on. Ignore an older saved
        // false value so upgrades immediately adopt the safer behavior.
        selection.SaveCaptures = true;
    }

    public void Save(UserSelectionState selection)
    {
        var value = new UserPreferences(
            selection.RecognitionScope,
            selection.TargetQuery,
            selection.AppearanceEnabled,
            selection.SaveCaptures,
            Load().Language);
        SaveValue(value);
    }

    public void SaveLanguage(string language)
    {
        var current = Load();
        SaveValue(current with { Language = language });
    }

    private void SaveValue(UserPreferences value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var temporary = _path + ".tmp";
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, _path, overwrite: true);
        }
    }
}

public sealed record UserPreferences(
    string? RecognitionScope = null,
    string TargetQuery = "Query_1",
    bool AppearanceEnabled = false,
    bool SaveCaptures = true,
    string Language = "vi-VN");
