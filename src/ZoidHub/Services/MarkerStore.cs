using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ZoidHub.Services;

/// <summary>Persists the player's custom map markers as plain JSON in AppData - independent of
/// any save game, so markers survive between characters/runs. One file holds every map's
/// markers together (tagged by Marker.MapId) - the web view only ever works with one map's
/// worth at a time, so saving replaces just that map's slice rather than the whole file, keeping
/// markers from a different map (once map selection exists) intact.</summary>
public class MarkerStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MarkerStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZoidHub");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "markers.json");
    }

    private List<Models.Marker> LoadAll()
    {
        if (!File.Exists(_path)) return new List<Models.Marker>();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<Models.Marker>>(json) ?? new List<Models.Marker>();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"MarkerStore.Load failed: {ex.Message}");
            return new List<Models.Marker>();
        }
    }

    public List<Models.Marker> LoadForMap(string mapId) =>
        LoadAll().FindAll(m => m.MapId == mapId);

    public void SaveForMap(string mapId, List<Models.Marker> markersForThisMap)
    {
        foreach (var m in markersForThisMap) m.MapId = mapId;

        var all = LoadAll();
        all.RemoveAll(m => m.MapId == mapId);
        all.AddRange(markersForThisMap);

        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(all, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLogger.Log($"MarkerStore.Save failed: {ex.Message}");
        }
    }
}
