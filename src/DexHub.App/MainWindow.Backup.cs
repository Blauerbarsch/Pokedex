using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DexHub.Core.Stats;

namespace DexHub.App;

/// <summary>Automatische Sicherung der Spielstände in einen frei wählbaren Ordner (z. B. auf dem NAS).</summary>
public partial class MainWindow
{
    private DispatcherTimer? _backupTimer;
    private bool _backupRunning;
    private TextBlock? _backupStatus;

    private static readonly (string Label, int Hours)[] BackupIntervals =
    [
        ("Bei jedem Programmstart", 0), ("Alle 6 Stunden", 6), ("Täglich", 24), ("Wöchentlich", 24 * 7),
    ];

    private void InitBackup()
    {
        BuildBackupSettings();
        Opened += async (_, _) => await RunBackupIfDueAsync();
        _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _backupTimer.Tick += async (_, _) => await RunBackupIfDueAsync();
        _backupTimer.Start();
    }

    private void BuildBackupSettings()
    {
        var panel = BackupSettingsPanel;
        panel.Children.Clear();
        panel.Children.Add(Text(
            "Sichert Spielstände, Bank, Shiny-Jagden, Spielzeit, Erfolge und Einstellungen aller Benutzer als ZIP – " +
            "z. B. auf dein NAS. Schnellspeicherungen sind nicht dabei (zu groß).", 13, opacity: 0.8));

        bool enabled = !string.IsNullOrWhiteSpace(_settings.AutoBackupFolder);
        panel.Children.Add(Text(enabled ? $"Ordner: {_settings.AutoBackupFolder}" : "Aus – noch kein Ordner gewählt.", 13, bold: enabled));

        var choose = new Button { Content = enabled ? "Anderen Ordner wählen…" : "Ordner wählen…" };
        choose.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Ordner für Sicherungen wählen",
                AllowMultiple = false,
            });
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
            _settings.AutoBackupFolder = path;
            _settings.LastAutoBackup = null; // gleich die erste Sicherung anlegen
            _settings.Save();
            BuildBackupSettings();
            await RunBackupIfDueAsync();
        };
        var off = new Button { Content = "Ausschalten", IsVisible = enabled };
        off.Click += (_, _) =>
        {
            _settings.AutoBackupFolder = "";
            _settings.Save();
            BuildBackupSettings();
        };
        var now = new Button { Content = "Jetzt sichern", IsEnabled = enabled };
        now.Click += async (_, _) => await RunBackupAsync();
        var open = new Button { Content = "Ordner öffnen", IsVisible = enabled };
        open.Click += (_, _) => OpenFolder(_settings.AutoBackupFolder);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { choose, now, open, off } });

        var interval = new ComboBox
        {
            ItemsSource = BackupIntervals.Select(i => i.Label).ToList(),
            SelectedIndex = Math.Max(0, Array.FindIndex(BackupIntervals, i => i.Hours == _settings.AutoBackupHours)),
            MinWidth = 220,
        };
        interval.SelectionChanged += (_, _) =>
        {
            if (interval.SelectedIndex < 0) return;
            _settings.AutoBackupHours = BackupIntervals[interval.SelectedIndex].Hours;
            _settings.Save();
        };
        var keep = new NumericUpDown { Minimum = 1, Maximum = 100, Value = _settings.AutoBackupKeep, Increment = 1, FormatString = "0", Width = 130 };
        keep.ValueChanged += (_, _) =>
        {
            _settings.AutoBackupKeep = (int)(keep.Value ?? 10);
            _settings.Save();
        };
        panel.Children.Add(LabeledRow("Wie oft", interval, 160));
        panel.Children.Add(LabeledRow("Aufheben", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { keep, Text("neueste Sicherungen (ältere werden gelöscht)", 13, opacity: 0.7) },
        }, 160));

        _backupStatus = Text(_settings.LastAutoBackup is { } last ? $"Letzte Sicherung: {last:dd.MM.yyyy HH:mm}" : "", 12, opacity: 0.7);
        panel.Children.Add(_backupStatus);
    }

    private async Task RunBackupIfDueAsync()
    {
        if (BackupService.IsDue(_settings)) await RunBackupAsync();
    }

    private async Task RunBackupAsync()
    {
        if (_backupRunning || string.IsNullOrWhiteSpace(_settings.AutoBackupFolder)) return;
        _backupRunning = true;
        if (_backupStatus is not null) _backupStatus.Text = "Sichere …";
        try
        {
            var folder = _settings.AutoBackupFolder;
            int keep = _settings.AutoBackupKeep;
            var path = await Task.Run(() => BackupService.Create(folder, keep));
            _settings.LastAutoBackup = DateTime.Now;
            _settings.Save();
            if (_backupStatus is not null) _backupStatus.Text = $"Letzte Sicherung: {DateTime.Now:dd.MM.yyyy HH:mm} ({Path.GetFileName(path)})";
        }
        catch (Exception ex)
        {
            if (_backupStatus is not null) _backupStatus.Text = "Sicherung fehlgeschlagen: " + ex.Message;
            Console.WriteLine("[DexHub] Sicherung: " + ex);
        }
        finally
        {
            _backupRunning = false;
        }
    }
}
