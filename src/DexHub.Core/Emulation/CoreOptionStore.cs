using System.Text.Json;
using DexHub.Core.Libretro;
using DexHub.Core.Settings;

namespace DexHub.Core.Emulation;

/// <summary>
/// Merkt sich, welche Optionen ein Core anbietet. Die erfährt man erst, wenn der Core einmal lief –
/// so kann der Einstellungsdialog sie auch anzeigen, ohne dass gerade ein Spiel läuft.
/// </summary>
public static class CoreOptionStore
{
    private static string Folder
    {
        get
        {
            var dir = Path.Combine(AppSettings.AppDataFolder, "core-options");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FileFor(string coreName) => Path.Combine(Folder, coreName + ".json");

    public static void Save(string coreName, IReadOnlyList<CoreOptionDefinition> definitions)
    {
        if (definitions.Count == 0) return;
        try
        {
            File.WriteAllText(FileFor(coreName), JsonSerializer.Serialize(definitions));
        }
        catch (IOException)
        {
            // Nicht schlimm, dann eben beim nächsten Mal
        }
    }

    public static List<CoreOptionDefinition> Load(string coreName)
    {
        try
        {
            var file = FileFor(coreName);
            if (File.Exists(file))
                return JsonSerializer.Deserialize<List<CoreOptionDefinition>>(File.ReadAllText(file)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        return [];
    }
}
