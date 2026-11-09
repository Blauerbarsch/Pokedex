using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DexHub.Core.Pokemon;
using DexHub.Core.Saves;
using DexHub.Core.Settings;
using DexHub.Core.Trading;

namespace DexHub.App;

/// <summary>
/// Pokémon-Bank: Pokémon aus einem Spiel herausnehmen und in ein anderes legen
/// (auch Gen 3 → Gen 4 wie der Pal-Park).
/// </summary>
public partial class MainWindow
{
    private TradeColumn? _bankGame;
    private WrapPanel? _bankTiles;
    private TextBlock? _bankInfo;
    private TextBlock? _bankResult;
    private readonly Dictionary<string, Border> _bankTileById = [];
    private string? _bankSelected;

    private void BuildBankPanel()
    {
        BankPanel.Children.Clear();
        _bankTileById.Clear();
        _bankSelected = null;

        BankPanel.Children.Add(Text(
            "Lege Pokémon aus einem Spiel in deine Bank und hol sie später in ein anderes Spiel. " +
            "Von Gen 3 (Rubin bis Blattgrün) geht es auch nach Gen 4 – wie im Pal-Park. " +
            "Vorher wird der Spielstand gesichert. Jeder Benutzer hat seine eigene Bank.", opacity: 0.8));

        var games = _games.Where(g => TradeService.IsSupported(g.Info.SaveFormat))
                          .OrderBy(g => g.Info.Generation).ThenBy(g => g.Title).ToList();
        if (games.Count == 0)
        {
            BankPanel.Children.Add(Text("Keine Gen-3- oder Gen-4-Spiele in der Bibliothek gefunden.", opacity: 0.6));
            return;
        }

        _bankGame = CreateTradeColumn(games, _settings.CurrentProfile);
        _bankGame.ProfileBox.IsEnabled = false; // die Bank gehört dem aktiven Benutzer

        var deposit = new Button { Content = "In die Bank  →", HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16, 8) };
        var withdraw = new Button { Content = "←  Ins Spiel holen", HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16, 8) };
        deposit.Click += async (_, _) => await DepositAsync();
        withdraw.Click += async (_, _) => await WithdrawAsync();
        _bankResult = Text("", 13, opacity: 0.9);

        var middle = new StackPanel
        {
            Spacing = 12,
            Width = 200,
            Margin = new Thickness(12, 40, 12, 0),
            Children = { deposit, withdraw, _bankResult },
        };

        _bankTiles = new WrapPanel();
        _bankInfo = Text("", 12, opacity: 0.7);
        var bankView = new Border
        {
            Background = TileBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Text($"Bank von {_settings.CurrentProfile}", 16, bold: true),
                    _bankInfo,
                    new ScrollViewer { MaxHeight = 640, Content = _bankTiles },
                },
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        grid.Children.Add(TradeColumnView(_bankGame, "Spiel"));
        grid.Children.Add(WithColumn(middle, 1));
        grid.Children.Add(WithColumn(bankView, 2));
        BankPanel.Children.Add(grid);

        LoadTradeColumn(_bankGame);
        LoadBankTiles();
    }

    private void LoadBankTiles()
    {
        if (_bankTiles is null || _bankInfo is null) return;
        _bankTiles.Children.Clear();
        _bankTileById.Clear();
        _bankSelected = null;

        if (_gameData is null)
        {
            _bankInfo.Text = "Spieldaten werden noch geladen …";
            return;
        }

        var entries = PokemonBank.Load(_settings.CurrentProfile);
        _bankInfo.Text = entries.Count == 0
            ? "Noch leer. Links ein Pokémon anklicken und „In die Bank“ wählen."
            : $"{entries.Count} Pokémon in der Bank. Eins anklicken.";

        foreach (var entry in entries.OrderBy(e => e.StoredAt))
        {
            var pokemon = entry.Parse(_gameData);
            if (pokemon is null) continue;
            pokemon.Level = _gameData.LevelFromExp(pokemon.Species, pokemon.Exp);
            var tile = CreateTradeTile(pokemon);
            ToolTip.SetTip(tile, $"Gen {entry.Generation} · aus {entry.FromGame} · {entry.StoredAt:dd.MM.yyyy}");
            var id = entry.Id;
            tile.PointerPressed += (_, _) =>
            {
                _bankSelected = id;
                foreach (var (other, t) in _bankTileById) t.BorderBrush = other == id ? SelectedBrush : Brushes.Transparent;
            };
            _bankTileById[id] = tile;
            _bankTiles.Children.Add(tile);
        }
    }

    private async Task DepositAsync()
    {
        if (_bankGame?.Side is not { } side || _bankGame.Selected is not { } slot || _gameData is null)
        {
            await ShowMessageAsync("Bank", "Bitte links ein Pokémon aus den Boxen auswählen.");
            return;
        }
        if (IsRunning(side.Game, side.Profile))
        {
            await ShowMessageAsync("Bank", "Bitte zuerst das Spiel beenden – sonst würde es die Änderung beim Speichern überschreiben.");
            return;
        }
        if (!await ConfirmAsync("In die Bank legen",
                $"{DisplayName(slot.Pokemon)} (Lv. {slot.Pokemon.Level}) aus {side.Game.Title} in die Bank legen?\n" +
                "Es verschwindet dabei aus der Box im Spiel. Der Spielstand wird vorher gesichert."))
            return;

        try
        {
            PokemonBank.Deposit(side, slot, _gameData);
            if (_bankResult is not null) _bankResult.Text = $"{DisplayName(slot.Pokemon)} liegt jetzt in der Bank.";
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Bank", ex.Message + "\n\nSicherungen liegen in:\n" + AppSettings.BackupsFolder);
        }
        AfterBankChange();
    }

    private async Task WithdrawAsync()
    {
        if (_bankGame?.Side is not { } side || _bankSelected is not { } id || _gameData is null)
        {
            await ShowMessageAsync("Bank", "Bitte rechts ein Pokémon aus der Bank und links das Ziel-Spiel auswählen.");
            return;
        }
        if (IsRunning(side.Game, side.Profile))
        {
            await ShowMessageAsync("Bank", "Bitte zuerst das Spiel beenden – sonst würde es die Änderung beim Speichern überschreiben.");
            return;
        }

        var entry = PokemonBank.Load(_settings.CurrentProfile).FirstOrDefault(e => e.Id == id);
        if (entry is null) { LoadBankTiles(); return; }
        bool palPark = entry.Generation == 3 && side.Game.Info.Generation == 4;

        var question = $"{(entry.Parse(_gameData) is { } p ? DisplayName(p) : "Pokémon")} nach {side.Game.Title} holen?" +
                       (palPark ? "\n\nEs wird dabei von Gen 3 nach Gen 4 umgewandelt (wie im Pal-Park). " +
                                  "Ein getragenes Item und Bänder bleiben zurück. Zurück nach Gen 3 geht danach nicht mehr." : "");
        if (!await ConfirmAsync("Aus der Bank holen", question)) return;

        // Für den Pal-Park braucht es Fähigkeiten und Geschlechterverteilung aus den Guide-Daten
        if (palPark && _guide is null)
        {
            if (_bankResult is not null) _bankResult.Text = "Lade Pokémon-Daten (beim ersten Mal 1–2 Minuten) …";
            await EnsureGuideLoadedAsync();
            if (_guide is null)
            {
                await ShowMessageAsync("Bank", "Die Pokémon-Daten konnten nicht geladen werden. Besteht eine Internetverbindung?");
                return;
            }
        }

        try
        {
            var names = new SpeciesNames(SpeciesName,
                s => _dexById.TryGetValue(s, out var e) && e.Species.NameEn.Length > 0 ? e.Species.NameEn : SpeciesName(s));
            var notes = PokemonBank.Withdraw(_settings.CurrentProfile, id, side, _gameData, SpeciesInfoFor, names);
            if (_bankResult is not null) _bankResult.Text = string.Join("\n", notes);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Bank", ex.Message);
        }
        AfterBankChange();
    }

    private SpeciesInfo? SpeciesInfoFor(int species)
    {
        if (_guide is null || !_guide.Pokemon.TryGetValue(species, out var p)) return null;
        var normal = p.Abilities.Where(a => !a.Hidden).Select(a => a.Ability).ToList();
        if (normal.Count == 0) return null;
        return new SpeciesInfo(normal[0], normal.Count > 1 ? normal[1] : 0, p.GenderRate);
    }

    private void AfterBankChange()
    {
        foreach (var g in _games) g.SavePath = SaveLocator.Find(g);
        if (_bankGame is not null) LoadTradeColumn(_bankGame);
        LoadBankTiles();
        if (_tradeA is not null) LoadTradeColumn(_tradeA);
        if (_tradeB is not null) LoadTradeColumn(_tradeB);
        RefreshGamesList();
        RebuildDex();
    }
}
