using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using DexHub.Core.Updates;

namespace DexHub.App;

/// <summary>Update-Knopf: neue Releases von GitHub laden und installieren.</summary>
public partial class MainWindow
{
    private UpdateInfo? _update;

    private void InitUpdates()
    {
        VersionText.Text = UpdateService.IsDevelopmentBuild
            ? "Entwicklerversion (aus dem Git-Ordner gestartet) – Update mit „git pull“ im Terminal."
            : $"Installierte Version: {UpdateService.CurrentVersionText}";

        // Fertige Version: beim Start leise nachschauen
        if (!UpdateService.IsDevelopmentBuild)
            Opened += async (_, _) => await CheckForUpdatesAsync(silent: true);
    }

    private async void OnCheckUpdatesClick(object? sender, RoutedEventArgs e) => await CheckForUpdatesAsync(silent: false);

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (UpdateService.IsDevelopmentBuild)
        {
            UpdateStatusText.Text = "In der Entwicklerversion bitte im Terminal „git pull“ ausführen und neu starten.";
            return;
        }

        UpdateStatusText.Text = "Suche nach Updates…";
        try
        {
            _update = await UpdateService.CheckAsync();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Update-Suche fehlgeschlagen: " + ex.Message;
            return;
        }

        bool available = _update is not null;
        UpdateButton.IsVisible = available;
        InstallUpdateButton.IsVisible = available;
        UpdateStatusText.Text = available
            ? $"Neue Version verfügbar: {_update!.Name} vom {_update.Date.LocalDateTime:dd.MM.yyyy HH:mm}"
            : "DexHub ist auf dem neuesten Stand.";
        if (available && silent) SetStatus($"Update verfügbar: {_update!.Name} – oben auf „Update verfügbar“ klicken.");
    }

    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        if (AnyGameRunning)
        {
            await ShowMessageAsync("Update", "Bitte zuerst alle laufenden Spiele beenden.");
            return;
        }
        if (!await ConfirmAsync("Update installieren",
                $"{_update.Name} herunterladen ({_update.Size / 1024 / 1024} MB) und installieren?\n\n" +
                "DexHub schließt sich dabei kurz und startet danach von selbst neu. Spielstände und Einstellungen bleiben erhalten."))
            return;

        UpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(msg =>
            {
                UpdateStatusText.Text = msg;
                SetStatus(msg);
            });
            await UpdateService.PrepareAndLaunchAsync(_update, progress);

            // Beenden, damit das Skript die Dateien austauschen kann
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = true;
            await ShowMessageAsync("Update fehlgeschlagen", ex.Message);
        }
    }
}
