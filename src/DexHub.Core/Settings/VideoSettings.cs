namespace DexHub.Core.Settings;

/// <summary>Wie das Spielbild ins Fenster eingepasst wird.</summary>
public enum ScalingMode
{
    /// <summary>So groß wie möglich, Seitenverhältnis bleibt erhalten.</summary>
    Fit,
    /// <summary>Nur ganze Vielfache (2x, 3x, …): alle Pixel exakt gleich groß.</summary>
    Integer,
    /// <summary>Füllt das ganze Fenster, Bild wird verzerrt.</summary>
    Stretch,
}

/// <summary>Effekt, der über das Spielbild gelegt wird.</summary>
public enum OverlayEffect
{
    None,
    /// <summary>Dunkle Zeilen wie bei einem Röhrenfernseher.</summary>
    Scanlines,
    /// <summary>Feines Raster wie beim Display von Game Boy/GBA.</summary>
    LcdGrid,
}

/// <summary>Anordnung der beiden DS-Bildschirme.</summary>
public enum DsLayout
{
    TopBottom,
    SideBySide,
    /// <summary>Hauptbildschirm groß, der andere klein daneben.</summary>
    Hybrid,
    /// <summary>Nur der Hauptbildschirm (oben, bzw. unten wenn getauscht).</summary>
    MainOnly,
    /// <summary>Nur der zweite Bildschirm (Touchscreen, bzw. oben wenn getauscht).</summary>
    SecondOnly,
}

/// <summary>Bild-Einstellungen für eine Konsole.</summary>
public sealed class VideoSettings
{
    public bool Smooth { get; set; }
    public ScalingMode Scaling { get; set; } = ScalingMode.Fit;
    public OverlayEffect Effect { get; set; } = OverlayEffect.None;
    public DsLayout DsLayout { get; set; } = DsLayout.TopBottom;
    public bool DsSwapScreens { get; set; }

    // ---------- Beschriftungen (Reihenfolge = Enum-Reihenfolge) ----------

    public static readonly string[] ScalingLabels =
        ["Seitenverhältnis beibehalten", "Ganzzahlig (gleichmäßige Pixel)", "Fenster füllen (verzerrt)"];

    public static readonly string[] EffectLabels =
        ["Kein Effekt", "Scanlines (Röhren-TV)", "LCD-Raster (Handheld-Display)"];

    public static readonly string[] DsLayoutLabels =
        ["Übereinander", "Nebeneinander", "Groß + klein", "Nur Hauptbildschirm", "Nur Touchscreen"];
}
