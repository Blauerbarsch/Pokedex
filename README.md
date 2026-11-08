# DexHub

Ein Launcher für alle Pokémon-Spiele, von Pokémon Mini bis 3DS, mit eingebautem Pokédex.
DexHub ist **kein eigener Emulator**. Die Spiele laufen über [RetroArch](https://www.retroarch.com/)
und dessen libretro-Cores. DexHub kümmert sich um alles drumherum:

- **Bibliothek:** erkennt Pokémon-Spiele anhand des ROM-Headers und startet sie mit dem passenden Core
- **Pokédex:** alle Pokémon mit deutschen Namen, Bildern und Filtern (Daten von [PokéAPI](https://pokeapi.co))
- **Live-Fortschritt:** liest die Spielstände aus und markiert gefangene ● und gesehene ○ Pokémon (bisher Gen 1 + 2)

> ROMs, BIOS-Dateien und Spielstände sind **nicht** enthalten und gehören nicht ins Repo.
> Jeder nutzt seine eigenen Sicherungskopien.

## Einrichten (Windows & Mac)

1. **.NET 10 SDK** installieren: https://dotnet.microsoft.com/download
2. **RetroArch** installieren: https://www.retroarch.com/?page=platforms
3. In RetroArch unter *Hauptmenü → Online-Aktualisierung → Core-Downloader* diese Cores laden:

   | Konsole | Core |
   |---|---|
   | Pokémon Mini | PokeMini |
   | Game Boy / Color | Gambatte (oder SameBoy) |
   | Game Boy Advance | mGBA |
   | Nintendo 64 | Mupen64Plus-Next |
   | Nintendo DS | melonDS DS (oder melonDS) |
   | GameCube / Wii | Dolphin |
   | Nintendo 3DS | Citra |

   Auf dem Mac sind nicht alle Cores für jede Prozessor-Variante verfügbar.

4. Repo klonen und starten:

   ```bash
   git clone https://github.com/Blauerbarsch/Pokedex.git
   cd Pokedex
   dotnet run --project src/DexHub.App
   ```

5. In DexHub unter **Einstellungen** den ROM-Ordner wählen und prüfen, ob die Pfade zu RetroArch,
   den Cores und dem `saves`-Ordner stimmen. Danach auf *Speichern* klicken.

Tests laufen mit `dotnet test`.

**Empfohlene Entwicklungsumgebung:** [JetBrains Rider](https://www.jetbrains.com/rider/) (kostenlos für private Nutzung)
oder VS Code mit der Erweiterung *C# Dev Kit* und *Avalonia for VS Code*.

## Aufbau

```
src/
  DexHub.Core/            Logik ohne Oberfläche (später auch für Android nutzbar)
    Detection/            Spielerkennung über ROM-Header + Spieldatenbank
    Emulation/            Startet RetroArch mit dem richtigen Core
    Saves/                Findet und liest Spielstände
    Pokedex/              PokéAPI-Daten, Cache, Zusammenführung mit Spielständen
    Settings/             Einstellungen (JSON im Benutzerordner)
  DexHub.App/             Oberfläche mit Avalonia (Windows, Mac, Linux)
tests/
  DexHub.Core.Tests/      Unit-Tests
```

Einstellungen und Cache liegen unter `%APPDATA%\DexHub` (Windows) bzw.
`~/Library/Application Support/DexHub` (Mac).

**Neues Spiel hinzufügen:** einfach in `src/DexHub.Core/Detection/GameDatabase.cs` eintragen.

## Fahrplan

- [x] Bibliothek mit Spielerkennung
- [x] Start über RetroArch
- [x] Pokédex mit deutschen Namen und Bildern
- [x] Spielstände Gen 1 + 2 auslesen
- [ ] Spielstände Gen 3 (GBA, inkl. Prüfsummen und Sektorrotation)
- [ ] Spielstände Gen 4/5 (DS)
- [ ] Spielstände 3DS und GameCube
- [ ] Cover-Bilder und schönere Bibliothek
- [ ] Pokédex-Details: Typen, Fundorte pro Spiel, Entwicklungen
- [ ] Live-Auslesen **während** des Spielens über die RetroArch-Netzwerkbefehle (`READ_CORE_MEMORY`)
- [ ] Cores direkt einbinden statt RetroArch extern zu starten
- [ ] Android-Version

## Hinweise

- Die Speicher-Offsets für Gen 1/2 gelten für die internationalen Versionen (auch die deutschen),
  nicht für die japanischen.
- „Pokémon“ und alle zugehörigen Namen sind Marken von Nintendo, Creatures Inc. und GAME FREAK inc.
  Dieses Projekt ist ein inoffizielles Fanprojekt und steht in keiner Verbindung zu ihnen.
