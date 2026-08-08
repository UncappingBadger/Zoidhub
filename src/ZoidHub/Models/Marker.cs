namespace ZoidHub.Models;

public class Marker
{
    public string Id { get; set; } = "";
    public string MapId { get; set; } = "vanilla";
    public string Label { get; set; } = "";
    public string Color { get; set; } = "#E0A030";
    public int X { get; set; }
    public int Y { get; set; }
    public int Layer { get; set; }
    public string Note { get; set; } = "";
}
