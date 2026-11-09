# DexHub

Ein eigenständiges Programm für alle Pokémon-Spiele, mit eingebautem Pokédex.
Kein RetroArch, keine Zusatzprogramme: einfach starten, ROM-Ordner wählen, spielen.

- **Eingebaute Emulatoren:** Die Spiele laufen direkt im Programm. Dafür nutzt DexHub bewährte
  Open-Source-Emulatorkerne (libretro-Cores), die mit dem Programm ausgeliefert werden.
- **Bibliothek:** erkennt Pokémon-Spiele anhand des ROM-Headers, zeigt sie mit Cover nach Konsolen sortiert
  (Cover aus [libretro-thumbnails](https://github.com/libretro-thumbnails))
- **Pokédex:** alle Pokémon mit deutschen Namen, Bildern und Filtern (Daten von [PokéAPI](https://pokeapi.co))
- **Fortschritt:** Sobald du im Spiel speicherst, markiert der Pokédex gefangene ● und gesehene ○ Pokémon
  (Gen 1, 2 und 4)

- **Statistik & Rückblick:** einzelne Pokémon aus Team und Boxen (Gen 4), Fortschritt, Zeitleiste nach Fangdatum
- **Benutzer:** mehrere Profile mit eigenen Spielständen; mehrere Spiele gleichzeitig (jedes in eigenem Fenster)
- **Tauschen:** Tauschzentrale direkt zwischen Spielständen (Gen 4, inkl. Tausch-Entwicklungen) und
  Linkkabel zwischen zwei Game-Boy-Fenstern
- **Guide:** Typen, Schwächen, Basiswerte, Fähigkeiten, Entwicklungen, Fundorte und Attacken pro Edition, Typ-Tabelle
- **Spielzeit** pro Benutzer und Spiel; Fenstergröße wird pro Konsole gemerkt
- **Spielstände:** Übersicht pro Benutzer mit Import, Export (auch als ZIP) und Sicherungen

> ROMs, BIOS-Dateien und Spielstände sind **nicht** enthalten und gehören nicht ins Repo.
> Jeder nutzt seine eigenen Sicherungskopien.

## Herunterladen

Fertige Pakete gibt es unter **[Releases](https://github.com/Blauerbarsch/Pokedex/releases)** – ohne .NET,
ohne Git, Emulatorkerne sind dabei:

| System | Datei | Starten |
|---|---|---|
| Windows | `DexHub-win-x64.zip` | entpacken, `DexHub.exe` doppelklicken |
| Linux (z. B. Bazzite, Steam Deck) | `DexHub-linux-x64.zip` | entpacken, `./DexHub` (braucht SDL2, ist fast überall installiert) |
| Mac (Apple Silicon) | `DexHub-osx-arm64.zip` | entpacken, im Terminal einmal `xattr -cr DexHub`, dann `./DexHub/DexHub` |

Windows zeigt beim ersten Start evtl. „Der Computer wurde durch Windows geschützt“ – auf
„Weitere Informationen“ → „Trotzdem ausführen“ klicken (das Programm ist nicht signiert).

**Großbild-Modus:** Mit `DexHub --bigpicture` startet DexHub im Vollbild, komplett mit Controller
bedienbar – ideal als Steam-Verknüpfung (Steam → „Spiel hinzufügen“ → „Nicht-Steam-Spiel“, Startoption `--bigpicture`).

## Stand

| Konsole | Spielbar | Emulatorkern |
|---|---|---|
| Pokémon Mini | ✓ | PokeMini |
| Game Boy / Color | ✓ | Gambatte |
| Game Boy Advance | ✓ | mGBA |
| Nintendo DS | ✓ (Touchscreen per Maus) | melonDS DS |
| Nintendo 64 | später | braucht 3D-Rendering |
| GameCube / Wii | später | braucht 3D-Rendering |
| Nintendo 3DS | später | braucht 3D-Rendering |

## Steuerung

Tastatur und Controller (Xbox, PlayStation, Switch Pro, Steam Deck …) lassen sich unter
**Steuerung** frei belegen. Standard:

| Spieltaste | Tastatur | Controller (nach Position wie bei Nintendo) |
|---|---|---|
| Steuerkreuz | Pfeiltasten | Steuerkreuz oder linker Stick |
| A / B | X / Y | rechte / untere Taste |
| X / Y (DS) | S / A | obere / linke Taste |
| L / R | Q / W | LB / RB |
| Start / Select | Enter / Rücktaste | Start / Back |
| Schnellvorlauf (halten) | Leertaste | RT / R2 |

| DS-Touch tippen | frei belegbar | rechten Stick drücken |

**DS-Touchscreen:** mit Maus oder Finger direkt antippen, oder mit dem Controller: Der rechte Stick
bewegt einen Zeiger auf dem unteren Bildschirm, Stick drücken tippt. Die Paddles eines
Elite-Controllers oder Steam Decks lassen sich ebenfalls belegen.

| Schnellspeichern / -laden | F5 / F9 | frei belegbar (z. B. Paddles) |
| Speicherplatz wechseln (0–9) | F6 / F7 | frei belegbar |

Fest: P = Pause, F11 = Vollbild.

**Speichern unabhängig vom Spiel:** Schnellspeichern sichert den kompletten Zustand, egal ob das
Spiel gerade speichern kann. Beim Schließen sichert DexHub automatisch und macht beim nächsten Start
dort weiter (abschaltbar; „Ohne Fortsetzen starten“ startet frisch).

## Bild

Pro Konsole einstellbar (Tab **Bild**): scharfe oder weiche Pixel, Skalierung (Seitenverhältnis,
ganzzahlig, Fenster füllen), Bildeffekte (Scanlines, LCD-Raster) und beim DS die Anordnung der
Bildschirme (übereinander, nebeneinander, groß + klein, nur einer, getauscht). Dazu die Optionen,
die der jeweilige Emulator selbst anbietet (z. B. Farbkorrektur). Hotkeys im Spiel: F2 = DS-Anordnung,
F3 = DS-Bildschirme tauschen, F4 = Bildeffekt, F11 = Vollbild.

## Entwickeln (Windows & Mac)

1. **.NET 10 SDK** installieren: https://dotnet.microsoft.com/download
2. Repo klonen:
   ```bash
   git clone https://github.com/Blauerbarsch/Pokedex.git
   cd Pokedex
   ```
3. Einmalig die Emulatorkerne herunterladen (landen in `src/DexHub.App/cores`):
   ```bash
   dotnet run scripts/FetchCores.cs
   ```
4. Starten:
   ```bash
   dotnet run --project src/DexHub.App
   ```
5. Im Programm unter **Einstellungen** den ROM-Ordner wählen.

Tests laufen mit `dotnet test`.

**Empfohlene Entwicklungsumgebung:** [JetBrains Rider](https://www.jetbrains.com/rider/) (kostenlos für private Nutzung)
oder VS Code mit *C# Dev Kit* und *Avalonia for VS Code*.

## Aufbau

```
src/
  DexHub.Core/            Logik ohne Oberfläche (später auch für Android nutzbar)
    Libretro/             Direkte Anbindung der Emulatorkerne (libretro-API)
    Emulation/            Laufendes Spiel: Takt, Spielstände, Schnellspeichern, Bildpuffer
    Detection/            Spielerkennung über ROM-Header + Spieldatenbank
    Saves/                Findet und liest Spielstände
    Pokedex/              PokéAPI-Daten, Cache, Zusammenführung mit Spielständen
    Settings/             Einstellungen und Ordner
  DexHub.App/             Oberfläche mit Avalonia (Windows, Mac, Linux)
    Audio/                Tonausgabe über SDL2
    GameWindow            Spielfenster (Bild, Tastatur, Touch)
scripts/
  FetchCores.cs           Lädt die Emulatorkerne
tests/
  DexHub.Core.Tests/      Unit-Tests
```

Einstellungen, Spielstände und Cache liegen unter `%APPDATA%\DexHub` (Windows) bzw.
`~/Library/Application Support/DexHub` (Mac). Vorhandene `.sav`-Dateien neben einer ROM werden
beim ersten Start übernommen.

**Neues Spiel hinzufügen:** in `src/DexHub.Core/Detection/GameDatabase.cs` eintragen.

## Fahrplan

- [x] Bibliothek mit Spielerkennung
- [x] Eingebaute Emulatoren für Pokémon Mini, GB/GBC, GBA, DS
- [x] Pokédex mit deutschen Namen und Bildern
- [x] Spielstände Gen 1 + 2 auslesen
- [x] Spielstände Gen 4 auslesen (Diamant, Perl, Platin, HeartGold, SoulSilver)
- [x] Automatisch sichern und fortsetzen
- [x] Cover, Bibliothek nach Konsolen, Bild-Einstellungen pro Konsole
- [x] Controller-Unterstützung und frei belegbare Tasten
- [ ] Spielstände Gen 3 (GBA, inkl. Prüfsummen und Sektorrotation)
- [ ] Pokédex live aus dem Arbeitsspeicher, auch ohne Speichern im Spiel
- [ ] Spielstände Gen 5 (DS)
- [ ] N64, GameCube/Wii, 3DS (OpenGL-Rendering für die Cores)
- [ ] Fertige Downloads für Windows und Mac (GitHub Actions)
- [ ] Android-Version

## Lizenzen und Hinweise

- Die mitgelieferten Emulatorkerne stehen größtenteils unter der GPL. Wer DexHub zusammen mit
  den Kernen weitergibt, muss deren Lizenzbedingungen einhalten. Am einfachsten ist es,
  DexHub selbst ebenfalls unter die GPL-3.0 zu stellen.
- Die Speicher-Offsets für Gen 1/2 gelten für die internationalen Versionen (auch die deutschen),
  nicht für die japanischen.
- „Pokémon“ und alle zugehörigen Namen sind Marken von Nintendo, Creatures Inc. und GAME FREAK inc.
  Dieses Projekt ist ein inoffizielles Fanprojekt und steht in keiner Verbindung zu ihnen.
