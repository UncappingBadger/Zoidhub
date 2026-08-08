using System;
using System.IO;
using System.Linq;

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

    /// <summary>Set from AppSettings.MapDataRoot at startup (MainWindow's constructor) and
    /// whenever the user picks a new location (see MainWindow.ChooseLocationButton_Click). Null
    /// means "use the default %LocalAppData% location" - a static settable field rather than a
    /// constructor parameter since GetOutputDir is called as a bare static method throughout
    /// MapRenderService/MainWindow, and threading an instance through all of those for a setting
    /// that changes at most a couple of times per session isn't worth the churn.</summary>
    public static string? RootOverride { get; set; }

    /// <summary>Where a given map's render output lives. Defaults to %LocalAppData% rather than
    /// next to the exe, since the exe's own folder may not be writable (e.g. installed under
    /// Program Files) and, now that everything else is embedded/self-extracting (see
    /// PayloadExtractor), nothing about ZoidHub should assume its own directory is writable. A
    /// full render needs ~150-250GB though, which plenty of real OS drives don't have room for -
    /// RootOverride lets that default be redirected to a roomier drive/folder instead.</summary>
    public static string GetOutputDir(string mapId) => Path.Combine(RootDir, "MapData", mapId);

    private static string RootDir => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoidHub");

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

    // pzmap2dzi's own unpack() (tools\pzmap2dzi\main.py) has two silent-failure paths: if a
    // dependency's texture_path folder doesn't exist it just prints "invalid texture_path" and
    // moves on, and if the folder exists but none of vanilla.txt's hardcoded texture-pack
    // filename patterns (Erosion.pack, ApCom.pack, Tiles2x.pack, etc.) match what's actually in
    // there - e.g. a Project Zomboid build/branch that renamed or reorganized its texture packs -
    // TextureLibrary.add_pack() is simply never called and save_all() still creates an (empty)
    // output folder. Either way the unpack process exits 0, so "does a texture/ subfolder merely
    // exist" isn't a real completeness check - a render that ran against empty textures still
    // exits 0 too and gets marked complete via MarkRenderComplete, same as a genuinely good one.
    // A real user hit exactly this (reported via YouTube comment): map "finished" rendering with
    // a black background and no ground/objects on any floor, with nothing in ZoidHub's own log to
    // explain why, since this step's output used to be discarded entirely.
    //
    // Checked in two places: MapRenderService.IsUnpacked, before a *fresh* unpack/render even
    // starts, and here, against an *already-"complete"* render on every launch (see
    // MainWindow.EnsureMapRenderedAsync) - the marker file alone can't distinguish a healthy
    // render from a broken one, and a broken render from before this check existed would
    // otherwise sit there marked complete forever with no way to self-heal on update. Re-running
    // the renderer against unhealthy textures is safe to trigger from scratch: pzmap2dzi's own
    // tile-save path (pzdzi.py's save_tile) always overwrites, it never skips a tile just because
    // a file already exists there.
    //
    // Checks the base game's own texture output specifically ("default", vanilla.txt's entry for
    // the vanilla county map) rather than "any texture output for any dependency" - unrelated
    // custom Workshop maps in the bundled catalog routinely fail this same way for anyone who
    // hasn't subscribed to them, which is normal/expected and not a sign of anything wrong.
    // Thousands of individual texture images are expected from a healthy unpack (confirmed via a
    // real run: 38,000+), so a low floor (20) is enough to catch "nothing matched at all" without
    // being fragile to the exact expected count.
    private const int MinHealthyTextureFileCount = 20;

    /// <summary>mapOutputDir is a map's output root - the same one GetOutputDir(mapId) returns
    /// (the parent of both "html" and "texture").</summary>
    public static bool HasHealthyUnpackedTextures(string mapOutputDir)
    {
        var dir = Path.Combine(mapOutputDir, "texture", "default");
        return Directory.Exists(dir) &&
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(MinHealthyTextureFileCount).Count() >= MinHealthyTextureFileCount;
    }
}
