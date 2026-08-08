using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace ZoidHub.Services;

/// <summary>Unpacks the single embedded Assets\Payload.zip (WebMap + Renderer + LuaMod - see
/// build_payload.ps1) into %LocalAppData%\ZoidHub\Payload\ the first time it's needed. This is
/// what makes ZoidHub.exe a genuinely portable single file: nothing has to sit next to the exe
/// for it to work, so it can be moved, shared, or run from Downloads without breaking - matching
/// how TruckHub's plugin DLL is embedded rather than shipped as a loose file alongside it.</summary>
public static class PayloadExtractor
{
    private const string ResourceName = "ZoidHub.Payload.zip";

    private static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZoidHub", "Payload");

    public static string WebMapDir => Path.Combine(RootDir, "WebMap");
    public static string RendererDir => Path.Combine(RootDir, "Renderer");
    public static string LuaModDir => Path.Combine(RootDir, "LuaMod");

    public static void EnsureExtracted()
    {
        using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");

        // Keyed off a hash of the payload's actual bytes, not just the assembly version - a
        // real bug this way round bit this exact app during development: an unbumped-version
        // rebuild that only changed WebMap/JS content kept serving the OLD extracted copy
        // indefinitely, since the version-only marker never changed. A future release that
        // patches web content without remembering to bump <Version> would hit the same thing
        // for real users, not just during local iteration - hashing the content itself makes
        // that class of bug structurally impossible rather than relying on remembering to bump.
        var hash = Convert.ToHexString(SHA256.HashData(resourceStream)).Substring(0, 16);
        resourceStream.Position = 0;

        var marker = Path.Combine(RootDir, $".extracted-{hash}");
        if (File.Exists(marker)) return;

        AppLogger.Log($"PayloadExtractor: extracting bundled assets (content hash {hash})...");

        if (Directory.Exists(RootDir))
        {
            Directory.Delete(RootDir, recursive: true);
        }
        Directory.CreateDirectory(RootDir);

        using var zip = new ZipArchive(resourceStream, ZipArchiveMode.Read);
        zip.ExtractToDirectory(RootDir, overwriteFiles: true);

        File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        AppLogger.Log("PayloadExtractor: extraction complete.");
    }
}
