# DexHub

Ein eigenständiges Programm für alle Pokémon-Spiele, mit eingebautem Pokédex.
Kein RetroArch, keine Zusatzprogramme: einfach starten, ROM-Ordner wählen, spielen.

- **Eingebaute Emulatoren:** Die Spiele laufen direkt im Programm. Dafür nutzt DexHub bewährte
  Open-Source-Emulatorkerne (libretro-Cores), die mit dem Programm ausgeliefert werden.
- **Bibliothek:** erkennt Pokémon-Spiele anhand des ROM-Headers
- **Pokédex:** alle Pokémon mit deutschen Namen, Bildern und Filtern (Daten von [PokéAPI](https://pokeapi.co))
- **Fortschritt:** Sobald du im Spiel speicherst, markiert der Pokédex gefangene ● und gesehene ○ Pokémon
  (bisher Gen 1 + 2)

> ROMs, BIOS-Dateien und Spielstände sind **nicht** enthalten und gehören nicht ins Repo.
> Jeder nutzt seine eigenen Sicherungskopien.

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

Fest: P = Pause, F5/F9 = Schnellspeichern/-laden, F11 = Vollbild, Maus = DS-Touchscreen.

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
- [x] Controller-Unterstützung und frei belegbare Tasten
- [ ] Spielstände Gen 3 (GBA, inkl. Prüfsummen und Sektorrotation)
- [ ] Pokédex live aus dem Arbeitsspeicher, auch ohne Speichern im Spiel
- [ ] Spielstände Gen 4/5 (DS)
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
