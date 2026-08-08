using System;
using System.IO;

namespace ZoidHub.Services;

public static class AppLogger
{
    private static readonly object Lock = new();
    private static readonly string LogPath;

    static AppLogger()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZoidHub", "logs");
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "zoidhub.log");
    }

    public static string FilePath => LogPath;

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (Lock)
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }
}
