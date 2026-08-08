namespace ZoidHub.Models;

public enum RenderStage { Unpacking, RenderingFloor, Done, Failed }

public class RenderProgress
{
    public RenderStage Stage { get; set; }
    public double FloorPercent { get; set; }
    public string Message { get; set; } = "";
}
