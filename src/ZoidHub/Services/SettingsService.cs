using System;
using System.IO;
using System.Text.Json;
using ZoidHub.Models;

namespace ZoidHub.Services;

public class SettingsService
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZoidHub");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"SettingsService.Load failed: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLogger.Log($"SettingsService.Save failed: {ex.Message}");
        }
    }
}
