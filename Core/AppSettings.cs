using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MongoDBTinyClient.Core;

internal sealed class AppSettings
{
    public const int MaxRecentConnections = 10;
    public const string DefaultConnection = "mongodb://localhost:27017";

    /// <summary>Connection strings encrypted with DPAPI (current user), most recent first.</summary>
    public List<string> RecentConnections { get; set; } = new();

    /// <summary>Documents per page in the Browse tab.</summary>
    public int PageSize { get; set; } = 50;

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> Connections =>
        RecentConnections.Select(Unprotect).Where(s => s.Length > 0).ToList();

    public void RememberConnection(string connectionString)
    {
        var list = Connections.Where(c => !string.Equals(c, connectionString, StringComparison.Ordinal)).ToList();
        list.Insert(0, connectionString);
        RecentConnections = list.Take(MaxRecentConnections).Select(Protect).ToList();
    }

    public void ForgetConnection(string connectionString)
    {
        RecentConnections = Connections
            .Where(c => !string.Equals(c, connectionString, StringComparison.Ordinal))
            .Select(Protect).ToList();
    }

    public void Normalize()
    {
        RecentConnections ??= new List<string>();
        PageSize = Math.Clamp(PageSize, 1, 5000);
        if (WindowWidth < 700) WindowWidth = 1280;
        if (WindowHeight < 450) WindowHeight = 800;
    }

    private static string Protect(string plain)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string cipher)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty; // copied from another user/PC: drop it
        }
    }
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Options);
                if (settings != null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Loading settings", ex);
        }

        var fresh = new AppSettings();
        fresh.Normalize();
        return fresh;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, Paths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings", ex);
        }
    }
}
