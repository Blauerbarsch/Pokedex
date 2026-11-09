namespace DexHub.Core.Models;

/// <summary>Welches Spielstand-Format das Programm lesen kann.</summary>
public enum SaveFormat
{
    /// <summary>Noch nicht unterstützt.</summary>
    None,
    Gen1,
    Gen2GoldSilver,
    Gen2Crystal,
    /// <summary>Rubin, Saphir, Smaragd, Feuerrot, Blattgrün (gleicher Aufbau, Unterschiede werden erkannt).</summary>
    Gen3,
    Gen4DiamondPearl,
    Gen4Platinum,
    Gen4HeartGoldSoulSilver,
}

/// <summary>Ein bekanntes Spiel aus der <see cref="Detection.GameDatabase"/>.</summary>
public sealed record GameInfo(
    string Title,
    Platform Platform,
    int? Generation,
    SaveFormat SaveFormat = SaveFormat.None);

/// <summary>Eine ROM-Datei in der Bibliothek des Nutzers.</summary>
public sealed class GameEntry
{
    public required string RomPath { get; init; }
    public required GameInfo Info { get; init; }

    /// <summary>true, wenn das Spiel über den Header erkannt wurde (nicht nur über den Dateinamen).</summary>
    public bool IsRecognized { get; init; }

    /// <summary>Pfad zum Spielstand, falls einer gefunden wurde.</summary>
    public string? SavePath { get; set; }

    // Anzeige-Eigenschaften für die Oberfläche
    public string Title => Info.Title;
    public string PlatformName => Info.Platform.DisplayName();
    public string FileName => Path.GetFileName(RomPath);

    public string StatusText
    {
        get
        {
            var parts = new List<string>();
            if (Info.Generation is int gen) parts.Add($"Gen {gen}");
            if (!IsRecognized) parts.Add("nur am Dateinamen erkannt");
            if (!Emulation.CoreCatalog.IsPlatformSupported(Info.Platform)) parts.Add("noch nicht spielbar");
            if (Info.SaveFormat != SaveFormat.None)
                parts.Add(SavePath is null ? "kein Spielstand gefunden" : "Spielstand gefunden");
            return string.Join(" · ", parts);
        }
    }
}
