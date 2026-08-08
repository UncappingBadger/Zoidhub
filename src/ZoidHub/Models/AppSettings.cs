namespace ZoidHub.Models;

public enum RenderSpeedMode { Light, Fast }

public class AppSettings
{
    /// <summary>Default to Light - a first-time render shouldn't peg a stranger's CPU without
    /// them choosing that. See RenderWorkerCount.Resolve for what each mode actually maps to.</summary>
    public RenderSpeedMode RenderSpeed { get; set; } = RenderSpeedMode.Light;
}
