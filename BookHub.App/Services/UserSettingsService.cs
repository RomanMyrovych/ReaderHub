using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookHub.Core.Data;
using BookHub.Core.Models;

namespace BookHub.Core.Services;

public sealed class UserSettingsService
{
    private readonly string _settingsFilePath;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public UserSettings Current { get; private set; } = new();
    public string SettingsFilePath => _settingsFilePath;

    public UserSettingsService(string? rootDirectory = null)
    {
        string baseDir = rootDirectory ?? LibraryDatabase.DataDirectory;
        Directory.CreateDirectory(baseDir);
        _settingsFilePath = Path.Combine(baseDir, "settings.json");
        Load();
    }

    public UserSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_settingsFilePath))
            {
                Current = new UserSettings();
                Save();
                return Current;
            }

            try
            {
                string json = File.ReadAllText(_settingsFilePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
                Current = loaded ?? new UserSettings();
            }
            catch
            {
                Current = new UserSettings();
            }

            return Current;
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                string json = JsonSerializer.Serialize(Current, JsonOptions);
                File.WriteAllText(_settingsFilePath, json, Encoding.UTF8);
            }
            catch
            {
                // Ignore transient write errors
            }
        }
    }
}
