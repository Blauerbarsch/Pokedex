using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DexHub.UI;

/// <summary>
/// Meldungen, Rückfragen und Eingaben als Ebene über der Oberfläche (statt eigener Fenster) –
/// so funktionieren sie gleich auf dem PC und auf Android.
/// </summary>
public partial class MainView
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    private async Task<T> ShowDialogAsync<T>(string title, Control? body, (string Label, T Value, bool Primary)[] buttons, T cancelValue,
        Control? focus = null, Func<T>? onEnter = null)
    {
        await _dialogGate.WaitAsync();
        try
        {
            var tcs = new TaskCompletionSource<T>();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var (label, value, primary) in buttons)
            {
                var b = new Button { Content = label, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
                if (primary) b.Classes.Add("primary");
                b.Click += (_, _) => tcs.TrySetResult(value);
                row.Children.Add(b);
            }

            var content = new StackPanel { Spacing = 16 };
            content.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (body is not null) content.Children.Add(body);
            content.Children.Add(row);
            DialogCard.Child = content;

            void OnKey(object? s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { tcs.TrySetResult(cancelValue); e.Handled = true; }
                else if (e.Key == Key.Enter && onEnter is not null) { tcs.TrySetResult(onEnter()); e.Handled = true; }
            }
            DialogLayer.KeyDown += OnKey;
            DialogLayer.IsVisible = true;
            (focus ?? row.Children.OfType<Button>().LastOrDefault())?.Focus();

            var result = await tcs.Task;
            DialogLayer.KeyDown -= OnKey;
            DialogLayer.IsVisible = false;
            DialogCard.Child = null;
            return result;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private static TextBlock DialogText(string message) => new()
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.Parse("#C9CFDA")),
    };

    private Task ShowMessageAsync(string title, string message) =>
        ShowDialogAsync(title, DialogText(message), [("OK", true, true)], true, onEnter: () => true);

    private Task<bool> ConfirmAsync(string title, string message) =>
        ShowDialogAsync(title, DialogText(message), [("Abbrechen", false, false), ("Ja", true, true)], false);

    private async Task<string?> PromptAsync(string title, string label)
    {
        var box = new TextBox { MinWidth = 300 };
        var body = new StackPanel { Spacing = 8, Children = { DialogText(label), box } };
        bool ok = await ShowDialogAsync(title, body, [("Abbrechen", false, false), ("OK", true, true)], false, box, onEnter: () => true);
        return ok ? box.Text : null;
    }
}
