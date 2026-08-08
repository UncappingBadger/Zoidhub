using System;
using System.IO;

namespace ZoidHub.Services;

/// <summary>Locates the pre-rendered map tile pyramid produced by the bundled renderer (see
/// MapRenderService / tools\pzmap2dzi) - the folder containing base/layer{N}.dzi +
/// layer{N}_files, plus base/map_info.json with the world-to-pixel origin/scale.
///
/// Storage is keyed per map ("vanilla" for the built-in county map, or a Workshop map's own
/// name for a custom map) so multiple maps can be rendered once each and kept side by side
/// forever - each gets its own folder under MapData\, never overwritten by another map's render.
/// Only "vanilla" is actually wired up end-to-end today (see [[project_zoidhub_build]] - no
/// custom map has been rendered or UI-selected yet), but the storage layout and path shape for a
/// custom map are real, not placeholders: pzmap2dzi itself already renders vanilla to
/// "html/base/" and any custom ("mod") map to its own "html/mod_maps/&lt;name&gt;/base/", so
/// FindMapHtmlDir mirrors that split rather than inventing a new convention.</summary>
public static class MapDataService
{
    public const string VanillaMapId = "vanilla";

    /// <summary>Where a given map's render output lives - %LocalAppData% rather than next to the
    /// exe, since the exe's own folder may not be writable (e.g. installed under Program Files)
    /// and, now that everything else is embedded/self-extracting (see PayloadExtractor), nothing
    /// about ZoidHub should assume its own directory is writable.</summary>
    public static string GetOutputDir(string mapId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZoidHub", "MapData", mapId);

    /// <summary>Returns the folder that directly contains "base/" (i.e. layer{N}.dzi +
    /// map_info.json) for the given map, or null if that map hasn't been rendered anywhere
    /// ZoidHub knows to look.</summary>
    public static string? FindMapHtmlDir(string mapId = VanillaMapId)
    {
        var root = GetOutputDir(mapId);
        var candidate = mapId == VanillaMapId
            ? Path.Combine(root, "html")
            : Path.Combine(root, "html", "mod_maps", mapId);
        if (IsValid(candidate)) return candidate;

        if (mapId == VanillaMapId)
        {
            // Dev-machine fallback from before render output moved to %LocalAppData% - vanilla
            // only, since no custom map was ever rendered under this old flat layout. Exempt from
            // the completion-marker check on purpose: this specific render was started by hand
            // (tools\pzmap2dzi directly, not MapRenderService) before MarkRenderComplete existed,
            // so it will never have the marker even once genuinely finished. Remove this whole
            // fallback once that one-off render is retired - see [[project_zoidhub_build]].
            var devOutput = @"F:\Claude Projects\ZoidHub\MapData\html";
            if (File.Exists(Path.Combine(devOutput, "base", "map_info.json"))) return devOutput;
        }

        return null;
    }

    // pzmap2dzi writes map_info.json and every layer{N}.dzi placeholder BEFORE rendering any
    // actual tiles (create_empty_output runs first) - checking just those would mistake a render
    // that got interrupted 1% of the way through for a finished one, and it would never resume.
    // MapRenderService calls MarkRenderComplete itself only after a render pass actually finishes.
    private const string CompleteMarkerName = ".render-complete";

    /// <summary>htmlBaseDir is the "base/" (or "mod_maps/&lt;name&gt;/base/") folder itself -
    /// the same one layer0.dzi and map_info.json live in.</summary>
    public static void MarkRenderComplete(string htmlBaseDir) =>
        File.WriteAllText(Path.Combine(htmlBaseDir, CompleteMarkerName), DateTime.UtcNow.ToString("O"));

    private static bool IsValid(string dir) =>
        File.Exists(Path.Combine(dir, "base", "map_info.json")) &&
        File.Exists(Path.Combine(dir, "base", CompleteMarkerName));
}
