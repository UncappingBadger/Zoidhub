namespace ZoidHub.Models;

public class Marker
{
    public string Id { get; set; } = "";
    public string MapId { get; set; } = "vanilla";
    public string Label { get; set; } = "";
    public string Color { get; set; } = "#212121";
    // Symbol id from MAP_SYMBOLS (zoidmap.js) - the same named icon set Project Zomboid's own
    // in-game map-marking UI offers players. Defaults to the game's own default marker look.
    public string Icon { get; set; } = "X";
    public int X { get; set; }
    public int Y { get; set; }
    public int Layer { get; set; }
    public string Note { get; set; } = "";
}
