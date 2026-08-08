namespace ZoidHub.Models;

/// <summary>Mirrors the JSON written by the ZoidHubBridge Lua mod
/// (Documents\Zomboid\Lua\ZoidHubBridge\position.json).</summary>
public class PlayerPosition
{
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public int Layer { get; set; }
    public bool InGame { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}
