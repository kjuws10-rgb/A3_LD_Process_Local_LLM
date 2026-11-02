using System.Text.Json;

namespace A3.OfflineStudio;

public sealed class StudioSettings
{
    public string? RepositoryPath { get; set; }
    public string? ActiveBundle { get; set; }
    public string? PreviousBundle { get; set; }
    public static string? TracePath { get; set; }
    public static string StateRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "A3OfflineStudio");
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static StudioSettings Load()
    {
        Directory.CreateDirectory(StateRoot);
        var file = Path.Combine(StateRoot, "settings.json");
        if (!File.Exists(file)) return new();
        try { return JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(file), Json) ?? new(); }
        catch (JsonException)
        {
            File.Copy(file, Path.Combine(StateRoot, "settings.corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".json"));
            var clean = new StudioSettings(); clean.Save(); return clean;
        }
    }
    public void Save()
    {
        var file = Path.Combine(StateRoot, "settings.json");
        File.WriteAllText(file + ".new", JsonSerializer.Serialize(this, Json));
        File.Move(file + ".new", file, true);
    }
}
