using System.Text.Json;

namespace LogRAM.Npp;

internal sealed class ViewerSettings
{
    public string Language { get; set; } = "zh";
    public List<string> RecentFiles { get; set; } = new();
    public List<string> SearchHistory { get; set; } = new();
    internal static ViewerSettings Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(path)) ?? new();
            settings.RecentFiles ??= new();
            settings.SearchHistory ??= new();
            settings.RecentFiles.RemoveAll(string.IsNullOrWhiteSpace);
            settings.SearchHistory.RemoveAll(string.IsNullOrWhiteSpace);
            settings.Language = settings.Language == "en" ? "en" : "zh";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save(string path)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    internal static void Remember(List<string> entries, string value)
    {
        entries.RemoveAll(item => item.Equals(value, StringComparison.Ordinal));
        entries.Insert(0, value);
        if (entries.Count > 20) entries.RemoveRange(20, entries.Count - 20);
    }
}
