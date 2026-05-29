using System;
using System.IO;
using System.Text.Json;
using ShockUI.Models.App;

namespace ShockUI.Services.App;

public sealed class AppSettingsService
{
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public AppSettingsService()
    {
        string baseFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShockUI");

        Directory.CreateDirectory(baseFolder);
        _settingsPath = Path.Combine(baseFolder, "appsettings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return new AppSettings();

            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string json = JsonSerializer.Serialize(settings, _jsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Ignore save failure for now.
        }
    }

    public string GetSettingsPath()
    {
        return _settingsPath;
    }
}