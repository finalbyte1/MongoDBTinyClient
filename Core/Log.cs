using System;
using System.IO;

namespace MongoDBTinyClient.Core;

/// <summary>Minimal best-effort file log: it must never bring the app down.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string FilePath { get; } =
        Path.Combine(Paths.DataDirectory, "mongodbtinyclient.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string context, Exception ex) => Write("ERROR", context + ": " + ex);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 512 * 1024)
                    file.Delete();

                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ignored on purpose
        }
    }
}

internal static class Paths
{
    public static string DataDirectory { get; } = CreateDataDirectory();

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    private static string CreateDataDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MongoDBTinyClient");
        try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        return dir;
    }
}
