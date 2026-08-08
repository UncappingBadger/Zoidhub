using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace ZoidHub.Services;

/// <summary>Unpacks the single embedded Assets\Payload.zip (WebMap + Renderer + LuaMod - see
/// build_payload.ps1) into %LocalAppData%\ZoidHub\Payload\ the first time it's needed. This is
/// what makes ZoidHub.exe a genuinely portable single file: nothing has to sit next to the exe
/// for it to work, so it can be moved, shared, or run from Downloads without breaking - matching
/// how TruckHub's plugin DLL is embedded rather than shipped as a loose file alongside it.
/// Re-extracts automatically on an app update (the marker file is versioned).</summary>
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
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        var marker = Path.Combine(RootDir, $".extracted-{version}");
        if (File.Exists(marker)) return;

        AppLogger.Log($"PayloadExtractor: extracting bundled assets (v{version})...");

        if (Directory.Exists(RootDir))
        {
            Directory.Delete(RootDir, recursive: true);
        }
        Directory.CreateDirectory(RootDir);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        zip.ExtractToDirectory(RootDir, overwriteFiles: true);

        File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        AppLogger.Log("PayloadExtractor: extraction complete.");
    }
}
