using System;
using System.IO;

namespace ZoidHub.Services;

/// <summary>Copies the ZoidHubBridge Lua mod (source-controlled under LuaMod\ZoidHubBridge)
/// into the user's Zomboid\mods folder, and checks whether it's already installed and enabled.
/// The mod itself just writes the local player's position to a JSON file - see
/// LivePositionService for the reading side.
///
/// Build 42 uses a different on-disk mod layout than B41: mod.info/poster.png/media\ all have
/// to live inside a "42\" subfolder, plus an (empty is fine) "common\" folder next to it, or the
/// game's mod browser silently never lists the mod at all - no error, it just doesn't appear.
/// Confirmed against a real B42 Steam support thread after a flat B41-style layout (which is
/// what the game's own bundled examplemod still uses, misleadingly) failed to show up live.</summary>
public static class LuaBridgeInstaller
{
    private const string ModId = "ZoidHubBridge";

    public static bool IsInstalled()
    {
        var dest = Path.Combine(GameLocator.GetZomboidModsDir(), ModId);
        return File.Exists(Path.Combine(dest, "42", "mod.info")) &&
               File.Exists(Path.Combine(dest, "42", "media", "lua", "client", "ZoidHubBridge.lua")) &&
               Directory.Exists(Path.Combine(dest, "common"));
    }

    public static bool Install()
    {
        try
        {
            var src = FindBundledModSource();
            if (src == null)
            {
                AppLogger.Log("LuaBridgeInstaller: bundled mod source not found");
                return false;
            }

            var dest = Path.Combine(GameLocator.GetZomboidModsDir(), ModId);
            CopyDirectory(src, dest);
            AppLogger.Log($"LuaBridgeInstaller: installed to {dest}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"LuaBridgeInstaller.Install failed: {ex.Message}");
            return false;
        }
    }

    private static string? FindBundledModSource()
    {
        var extracted = Path.Combine(PayloadExtractor.LuaModDir, ModId);
        if (Directory.Exists(extracted)) return extracted;

        var devSource = @"F:\Claude Projects\ZoidHub\LuaMod\ZoidHubBridge";
        if (Directory.Exists(devSource)) return devSource;

        return null;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }
}
