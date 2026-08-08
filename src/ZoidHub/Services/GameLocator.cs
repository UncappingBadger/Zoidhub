using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZoidHub.Services;

/// <summary>Locates the Project Zomboid game install directory (for reading tile/sprite
/// definitions) and the player's Zomboid user data directory (Documents\Zomboid or the
/// portable equivalent) - the latter is where the companion Lua mod writes live position data.</summary>
public static class GameLocator
{
    private const string GameFolderName = "ProjectZomboid";

    public static string? FindGameInstallDir()
    {
        foreach (var library in FindSteamLibraryFolders())
        {
            var candidate = Path.Combine(library, "steamapps", "common", GameFolderName);
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "ProjectZomboid64.exe")))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>The user data folder - where saves, mods, and the Lua cache/file-writer output
    /// live. PZ is a Java game and resolves this from user.home + "/Zomboid", which on Windows
    /// is the plain user profile root (%UserProfile%\Zomboid, e.g. C:\Users\name\Zomboid) - NOT
    /// Documents\Zomboid, despite that being a very easy assumption to make (and one this code
    /// wrongly made originally - confirmed wrong two ways: PZ's own docs, and a real install on
    /// this machine having Saves\ under %UserProfile%\Zomboid but nothing under
    /// Documents\Zomboid). If Documents has been redirected (OneDrive etc.) the two can be
    /// completely different paths, so don't assume they coincide.</summary>
    public static string GetZomboidUserDataDir()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Zomboid");
    }

    public static string GetZomboidLuaOutputDir()
    {
        return Path.Combine(GetZomboidUserDataDir(), "Lua");
    }

    public static string GetZomboidModsDir()
    {
        return Path.Combine(GetZomboidUserDataDir(), "mods");
    }

    private static IEnumerable<string> FindSteamLibraryFolders()
    {
        var steamPath = FindSteamInstallPath();
        if (steamPath == null) yield break;

        yield return steamPath;

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) yield break;

        string text;
        try { text = File.ReadAllText(vdfPath); }
        catch { yield break; }

        foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
        {
            yield return m.Groups[1].Value.Replace("\\\\", "\\");
        }
    }

    private static string? FindSteamInstallPath()
    {
        try
        {
            var userPath = Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath", null) as string;
            if (!string.IsNullOrEmpty(userPath)) return userPath.Replace('/', '\\');
        }
        catch { /* registry access can fail in locked-down environments - fall through */ }

        try
        {
            var machinePath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (!string.IsNullOrEmpty(machinePath)) return machinePath;
        }
        catch { /* same as above */ }

        const string fallback = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(fallback) ? fallback : null;
    }
}
