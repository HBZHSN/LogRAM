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

    public List<string> FileAssociations { get; set; } = new() { ".log" };

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

        FileAssociations ??= new();
        FileAssociations = FileAssociations
            .Select(ext => ext.StartsWith('.') ? ext.ToLowerInvariant() : $".{ext.ToLowerInvariant()}")
            .Distinct()
            .ToList();

        return this;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
