using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using ZoidHub.Models;

namespace ZoidHub.Services;

/// <summary>Watches the JSON file the ZoidHubBridge Lua mod writes (player world X/Y/Z, updated
/// on a short in-game tick) and raises PositionUpdated on the UI thread via the supplied
/// dispatcher-invoke callback. Poll-based (file-watch + a settle timer), not a push socket -
/// PZ's Lua sandbox has no reliable built-in networking, so file I/O is the proven approach
/// (same pattern PalHub Live's UE4SS bridge uses).</summary>
public class LivePositionService : IDisposable
{
    private readonly string _filePath;
    private readonly Action<PlayerPosition?> _onUpdate;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LivePositionService(Action<PlayerPosition?> onUpdate)
    {
        _onUpdate = onUpdate;
        _filePath = Path.Combine(GameLocator.GetZomboidLuaOutputDir(), "ZoidHubBridge", "position.json");
    }

    public string FilePath => _filePath;

    public void Start()
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);

        _watcher = new FileSystemWatcher(dir, Path.GetFileName(_filePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Changed += (_, _) => ScheduleRead();
        _watcher.Created += (_, _) => ScheduleRead();
        _watcher.EnableRaisingEvents = true;

        ScheduleRead();
    }

    private void ScheduleRead()
    {
        // Debounce: the game writes the file quickly on every tick, and we can catch it
        // mid-write - wait a beat for the write to settle before reading.
        _debounce?.Dispose();
        _debounce = new Timer(_ => ReadNow(), null, 150, Timeout.Infinite);
    }

    private void ReadNow()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _onUpdate(null);
                return;
            }

            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var pos = JsonSerializer.Deserialize<PlayerPosition>(json, JsonOptions);
            _onUpdate(pos);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"LivePositionService read failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
