using System.Diagnostics;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DexHub.Core.Models;
using DexHub.Core.Saves;
using DexHub.Core.Settings;

namespace DexHub.App;

/// <summary>Übersicht aller Spielstände aller Benutzer – mit Import, Export, Ordner öffnen und Löschen.</summary>
public partial class MainWindow
{
    private void BuildSavesPanel()
    {
        SavesPanel.Children.Clear();
        SavesPanel.Children.Add(Text(
            "Alle Spielstände pro Benutzer. Importieren übernimmt eine .sav/.srm/.dsv-Datei (z. B. aus einem anderen " +
            "Emulator), Exportieren speichert eine Kopie. Vor dem Überschreiben oder Löschen legt DexHub eine Sicherung an.",
            opacity: 0.8));

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var openBackups = new Button { Content = "Sicherungen öffnen" };
        openBackups.Click += (_, _) => OpenFolder(AppSettings.BackupsFolder);
        tools.Children.Add(openBackups);
        SavesPanel.Children.Add(tools);

        foreach (var profile in _settings.Profiles)
        {
            var rows = new StackPanel { Spacing = 4 };
            int count = 0;
            foreach (var game in _games.OrderBy(g => g.Info.Platform).ThenBy(g => g.Title))
            {
                var path = SaveLocator.Find(game, profile);
                if (path is not null) count++;
                rows.Children.Add(SaveRow(profile, game, path));
            }

            var exportZip = new Button { Content = "Alle als ZIP exportieren" };
            exportZip.Click += async (_, _) => await ExportProfileZipAsync(profile);
            var importZip = new Button { Content = "ZIP importieren" };
            importZip.Click += async (_, _) => await ImportProfileZipAsync(profile);
            var openFolder = new Button { Content = "Ordner öffnen" };
            openFolder.Click += (_, _) => OpenFolder(AppSettings.SavesFolderFor(profile));

            var content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { exportZip, importZip, openFolder } },
                    rows,
                },
            };

            SavesPanel.Children.Add(new Expander
            {
                Header = $"{profile}  –  {count} Spielstand/-stände",
                Content = content,
                IsExpanded = profile == _settings.CurrentProfile,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            });
        }
    }

    private Control SaveRow(string profile, GameEntry game, string? path)
    {
        string status, progress = "";
        if (path is null)
        {
            status = "kein Spielstand";
        }
        else
        {
            var info = new FileInfo(path);
            bool own = path.StartsWith(AppSettings.SavesFolderFor(profile), StringComparison.Ordinal);
            status = $"{info.LastWriteTime:dd.MM.yyyy HH:mm} · {info.Length / 1024} KB · {(own ? "DexHub" : "neben der ROM")}";
            if (game.Info.SaveFormat != SaveFormat.None && SaveReader.Read(path, game.Info.SaveFormat) is { } dex)
                progress = $"{dex.Caught.Count} gefangen";
        }

        var state = Path.Combine(AppSettings.StatesFolderFor(profile), Path.GetFileNameWithoutExtension(game.RomPath) + ".auto.state");
        if (File.Exists(state)) status += " · Fortsetzen-Sicherung";

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var import = new Button { Content = "Importieren…" };
        import.Click += async (_, _) => await ImportSaveAsync(profile, game);
        buttons.Children.Add(import);
        if (path is not null)
        {
            var export = new Button { Content = "Exportieren…" };
            export.Click += async (_, _) => await ExportSaveAsync(profile, game, path);
            var delete = new Button { Content = "Löschen" };
            delete.Click += async (_, _) => await DeleteSaveAsync(profile, game, path);
            buttons.Children.Add(export);
            buttons.Children.Add(delete);
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,300,110,Auto"),
            Margin = new Thickness(0, 2),
        };
        grid.Children.Add(new StackPanel
        {
            Children =
            {
                Text(game.Title, bold: true),
                Text($"{game.PlatformName} · {game.FileName}", 11, opacity: 0.6),
            },
        });
        grid.Children.Add(WithColumn(Text(status, 12, opacity: path is null ? 0.5 : 0.85), 1));
        grid.Children.Add(WithColumn(Text(progress, 12), 2));
        grid.Children.Add(WithColumn(buttons, 3));
        return new Border { Background = TileBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6), Child = grid };
    }

    // ---------- Aktionen ----------

    private static readonly FilePickerFileType SaveFileType = new("Spielstände")
    {
        Patterns = ["*.sav", "*.srm", "*.dsv", "*.sa1", "*.dat"],
    };

    private async Task ImportSaveAsync(string profile, GameEntry game)
    {
        if (IsRunning(game, profile))
        {
            await ShowMessageAsync("Importieren", "Bitte zuerst das Spiel beenden.");
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Spielstand für {game.Title} ({profile}) wählen",
            AllowMultiple = false,
            FileTypeFilter = [SaveFileType, FilePickerFileTypes.All],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } source) return;

        var target = SaveLocator.OwnSavePath(game, profile);
        if (File.Exists(target) &&
            !await ConfirmAsync("Importieren", $"{profile} hat schon einen Spielstand für {game.Title}. Überschreiben? (Er wird vorher gesichert.)"))
            return;

        try
        {
            BackupFile(target, profile);
            File.Copy(source, target, overwrite: true);
            MoveAutoStateToBackup(profile, game); // sonst würde "Fortsetzen" den alten Stand laden
            SetStatus($"Spielstand für {game.Title} ({profile}) importiert.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Importieren fehlgeschlagen", ex.Message);
        }
        AfterSavesChanged();
    }

    private async Task ExportSaveAsync(string profile, GameEntry game, string path)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Spielstand exportieren",
            SuggestedFileName = $"{Path.GetFileNameWithoutExtension(game.RomPath)} ({AppSettings.SafeName(profile)}).sav",
            DefaultExtension = "sav",
            FileTypeChoices = [SaveFileType],
        });
        if (file?.TryGetLocalPath() is not { } target) return;

        try
        {
            File.Copy(path, target, overwrite: true);
            SetStatus($"Exportiert nach {target}");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Exportieren fehlgeschlagen", ex.Message);
        }
    }

    private async Task DeleteSaveAsync(string profile, GameEntry game, string path)
    {
        if (IsRunning(game, profile))
        {
            await ShowMessageAsync("Löschen", "Bitte zuerst das Spiel beenden.");
            return;
        }
        bool own = path.StartsWith(AppSettings.SavesFolderFor(profile), StringComparison.Ordinal);
        var message = own
            ? $"Spielstand von {profile} für {game.Title} löschen? Er wird in die Sicherungen verschoben."
            : $"Dieser Spielstand liegt neben der ROM ({path}). Er wird in die Sicherungen verschoben. Fortfahren?";
        if (!await ConfirmAsync("Spielstand löschen", message)) return;

        try
        {
            var target = Path.Combine(AppSettings.BackupsFolder,
                $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{AppSettings.SafeName(profile)}_geloescht_{Path.GetFileName(path)}");
            File.Move(path, target);
            MoveAutoStateToBackup(profile, game);
            SetStatus($"Spielstand gelöscht (Sicherung: {target}).");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Löschen fehlgeschlagen", ex.Message);
        }
        AfterSavesChanged();
    }

    private async Task ExportProfileZipAsync(string profile)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Spielstände von {profile} exportieren",
            SuggestedFileName = $"DexHub-{AppSettings.SafeName(profile)}-{DateTime.Now:yyyy-MM-dd}.zip",
            DefaultExtension = "zip",
        });
        if (file?.TryGetLocalPath() is not { } target) return;

        try
        {
            if (File.Exists(target)) File.Delete(target);
            ZipFile.CreateFromDirectory(AppSettings.SavesFolderFor(profile), target);
            SetStatus($"Spielstände von {profile} exportiert nach {target}");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Exportieren fehlgeschlagen", ex.Message);
        }
    }

    private async Task ImportProfileZipAsync(string profile)
    {
        if (_running.Any(r => r.Profile == profile && !r.Process.HasExited))
        {
            await ShowMessageAsync("Importieren", $"Bitte zuerst alle Spiele von {profile} beenden.");
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"ZIP mit Spielständen für {profile} wählen",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("ZIP-Archiv") { Patterns = ["*.zip"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } zip) return;
        if (!await ConfirmAsync("ZIP importieren",
                $"Spielstände aus der ZIP für {profile} übernehmen? Gleichnamige vorhandene Spielstände werden vorher gesichert."))
            return;

        try
        {
            var folder = AppSettings.SavesFolderFor(profile);
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                if (!target.StartsWith(Path.GetFullPath(folder), StringComparison.Ordinal)) continue; // Schutz vor "../"
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                BackupFile(target, profile);
                entry.ExtractToFile(target, overwrite: true);

                var game = _games.FirstOrDefault(g => Path.GetFileNameWithoutExtension(g.RomPath) == Path.GetFileNameWithoutExtension(entry.Name));
                if (game is not null) MoveAutoStateToBackup(profile, game);
            }
            SetStatus($"ZIP für {profile} importiert.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Importieren fehlgeschlagen", ex.Message);
        }
        AfterSavesChanged();
    }

    // ---------- Hilfen ----------

    private static void BackupFile(string path, string profile)
    {
        if (!File.Exists(path)) return;
        File.Copy(path, Path.Combine(AppSettings.BackupsFolder,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{AppSettings.SafeName(profile)}_{Path.GetFileName(path)}"), overwrite: true);
    }

    private static void MoveAutoStateToBackup(string profile, GameEntry game)
    {
        var state = Path.Combine(AppSettings.StatesFolderFor(profile), Path.GetFileNameWithoutExtension(game.RomPath) + ".auto.state");
        if (!File.Exists(state)) return;
        File.Move(state, Path.Combine(AppSettings.BackupsFolder,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{AppSettings.SafeName(profile)}_{Path.GetFileName(state)}"), overwrite: true);
    }

    private void AfterSavesChanged()
    {
        foreach (var g in _games) g.SavePath = SaveLocator.Find(g);
        RefreshGamesList();
        RebuildDex();
        BuildSavesPanel();
        BuildTradePanel();
    }

    /// <summary>Ordner im Dateimanager öffnen (Windows Explorer, Finder, Linux-Dateimanager).</summary>
    private void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Ordner: {folder} ({ex.Message})");
        }
    }
}
