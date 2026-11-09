using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DexHub.Core.Pokemon;
using DexHub.Core.Stats;
using DexHub.Core.Trading;

namespace DexHub.App;

/// <summary>Erfolge im Statistik-Reiter.</summary>
public partial class MainWindow
{
    private Expander BuildAchievementsSection(List<OwnedPokemon> mons)
    {
        var profile = _settings.CurrentProfile;
        var hunts = HuntStore.For(profile);
        var context = new AchievementContext(
            CaughtSpecies: _dex.Where(d => d.IsCaught).Select(d => d.Id).ToHashSet(),
            ShinyCount: mons.Count(p => p.IsShiny),
            HighestLevel: mons.Count == 0 ? 0 : mons.Max(p => p.Level),
            PerfectIvPokemon: mons.Count(p => p.Ivs.Length == 6 && p.Ivs.Count(iv => iv == 31) >= 3),
            PlayTime: PlayTimeStore.Total(profile),
            GamesPlayed: PlayTimeStore.LoadAll().Count(kv => kv.Key.StartsWith(profile + "|", StringComparison.Ordinal) && kv.Value.Seconds >= 300),
            HuntEncounters: hunts.Sum(h => h.Count),
            HuntsFound: hunts.Count(h => !h.IsActive),
            BankCount: PokemonBank.Load(profile).Count);

        // Nur speichern, wenn die Daten vollständig geladen sind – sonst stimmt der Stand nicht
        if (_gameData is not null)
        {
            var fresh = Achievements.Update(profile, context);
            if (fresh.Count is > 0 and <= 3)
                SetStatus("🏆 Erfolg freigeschaltet: " + string.Join(", ", fresh.Select(a => a.Title)));
        }
        var unlocked = Achievements.LoadUnlocked(profile);

        var wrap = new WrapPanel();
        foreach (var a in Achievements.All.OrderByDescending(a => unlocked.ContainsKey(a.Id)).ThenBy(a => unlocked.GetValueOrDefault(a.Id)))
        {
            bool done = unlocked.TryGetValue(a.Id, out var when);
            var (current, target) = a.Progress(context);
            var info = done
                ? $"erreicht am {when:dd.MM.yyyy}"
                : $"{Math.Min(current, target):N0} / {target:N0}";

            var tile = new Border
            {
                Background = TileBrush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 8, 8),
                Width = 230,
                Opacity = done ? 1 : 0.55,
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children = { Text(done ? a.Icon : "🔒", 22), Text(a.Title, 14, bold: true) },
                        },
                        Text(a.Description, 12, opacity: 0.8),
                        done ? (Control)Text(info, 11, opacity: 0.6) : new ProgressBar
                        {
                            Minimum = 0, Maximum = target, Value = Math.Min(current, target), Height = 6, Margin = new Thickness(0, 4, 0, 0),
                        },
                        done ? new Border() : (Control)Text(info, 11, opacity: 0.6),
                    },
                },
            };
            wrap.Children.Add(tile);
        }

        return StatsSection($"🏆 Erfolge ({unlocked.Count} / {Achievements.All.Count})", wrap, expanded: false);
    }
}
