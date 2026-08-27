using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogRAM;

public sealed class AppSettings
{
    public string FontFamily { get; set; } = "Consolas";

    public double FontSize { get; set; } = 12;

    public bool IsDarkTheme { get; set; } = true;

    public string Language { get; set; } = "zh";

    public int InactiveMemoryReleaseMinutes { get; set; } = 5;

    public List<string> FileAssociations { get; set; } = new() { ".log" };

    public List<string> RecentFiles { get; set; } = new();

    public List<string> SearchHistory { get; set; } = new();

    private static string SettingsFilePath
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LogRAM");
            return Path.Combine(directory, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsFilePath;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
            return settings is null ? new AppSettings() : settings.Normalize();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var path = SettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(path, json);
        }
        catch
        {
        }
    }

    private AppSettings Normalize()
    {
        if (string.IsNullOrWhiteSpace(FontFamily))
        {
            FontFamily = "Consolas";
        }

        FontSize = Math.Clamp(FontSize, 6, 72);

        Language = string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

        InactiveMemoryReleaseMinutes = NormalizeInactiveMemoryReleaseMinutes(InactiveMemoryReleaseMinutes);

        FileAssociations ??= new();
        FileAssociations = FileAssociations
            .Select(ext => ext.StartsWith('.') ? ext.ToLowerInvariant() : $".{ext.ToLowerInvariant()}")
            .Distinct()
            .ToList();

        RecentFiles = NormalizeList(RecentFiles, StringComparer.OrdinalIgnoreCase);
        SearchHistory = NormalizeList(SearchHistory, StringComparer.Ordinal);

        return this;
    }

    internal static int NormalizeInactiveMemoryReleaseMinutes(int minutes) =>
        minutes is 0 or 1 or 5 or 10 or 30 or 60 ? minutes : 5;

    private static List<string> NormalizeList(List<string>? items, StringComparer comparer)
    {
        return (items ?? new())
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(comparer)
            .Take(20)
            .ToList();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
