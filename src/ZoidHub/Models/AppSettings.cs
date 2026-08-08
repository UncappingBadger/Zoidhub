namespace ZoidHub.Models;

public enum RenderSpeedMode { Light, Fast }

public class AppSettings
{
    /// <summary>Default to Light - a first-time render shouldn't peg a stranger's CPU without
    /// them choosing that. See RenderWorkerCount.Resolve for what each mode actually maps to.</summary>
    public RenderSpeedMode RenderSpeed { get; set; } = RenderSpeedMode.Light;

    /// <summary>Null means "use the default %LocalAppData%\ZoidHub location" (see
    /// MapDataService.GetOutputDir). A full render needs ~150-250GB - plenty of real machines
    /// have their whole OS drive smaller than that, so this lets someone point map data at a
    /// roomier drive instead. Set via MainWindow's "Change Location..." button.</summary>
    public string? MapDataRoot { get; set; }
}
