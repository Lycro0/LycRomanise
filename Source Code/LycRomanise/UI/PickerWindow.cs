using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LycRomanise.Core;

namespace LycRomanise.UI;

public sealed class PickerWindow : Window
{
    readonly AppController _app;
    readonly TrackQuery _query;
    readonly StackPanel _list = new();
    readonly TextBlock _status = new() { Opacity = 0.7, Margin = new Thickness(0, 0, 0, 12) };
    readonly CheckBox _remember = new() { Content = "Always use this source for this song", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
    readonly CancellationTokenSource _cts = new();
    public TrackQuery Query => _query;

    public PickerWindow(AppController app, TrackQuery query)
    {
        _app = app;
        _query = query;
        Title = "Choose lyrics";
        Width = 720;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Theme.Paint(this);
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/lyrics-overlay.ico"));
        var root = new DockPanel { Margin = new Thickness(24) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = query.Title, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        head.Children.Add(new TextBlock { Text = query.Artist, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 10) });
        head.Children.Add(_status);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        var foot = new StackPanel { Children = { _remember } };
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Closed += (_, _) => _cts.Cancel();
        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        _status.Text = "Asking every source…";
        _list.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 8, 0, 0) });
        List<LyricsResult> results;
        try { results = await Task.Run(() => _app.Chain.FetchAllAsync(_query, _cts.Token)); }
        catch (OperationCanceledException) { return; }
        _list.Children.Clear();
        _status.Text = results.Count == 0 ? "No source has lyrics for this song. You can import a file from Settings → Now playing." : $"{results.Count} sources have lyrics. Pick one:";
        var current = _app.CurrentResult;
        foreach (var r in results)
        {
            var preview = string.Join("\n", r.Lines.Take(4).Select(l => l.Text));
            var badge = r.Timing switch { Timing.Word => "Word synced", Timing.Line => "Line synced", _ => "Not synced" };
            var card = new Border
            {
                Style = (Style)Application.Current.FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = current?.SourceKey == r.SourceKey ? (Brush)FindResource("AccentFillColorDefaultBrush") : (Brush)FindResource("CardStrokeColorDefaultBrush"),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = r.Provider, FontWeight = FontWeights.SemiBold, FontSize = 15 });
            text.Children.Add(new TextBlock { Text = $"{badge} · {r.Lines.Count} lines" + (r.Lines.Any(l => l.Pronunciation != null) ? " · has romanization" : "") + (r.Lines.Any(l => l.Backing != null) ? " · backing vocals" : ""),
                Style = (Style)Application.Current.FindResource("CardHint") });
            text.Children.Add(new TextBlock { Text = preview, Opacity = 0.85, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            grid.Children.Add(text);
            var use = new Button { Content = "Use", Padding = new Thickness(18, 6, 18, 6), VerticalAlignment = VerticalAlignment.Top, Style = (Style)Application.Current.FindResource("AccentButtonStyle") };
            use.Click += (_, _) => { _app.UseResult(_query, r, _remember.IsChecked == true); Close(); };
            Grid.SetColumn(use, 1);
            grid.Children.Add(use);
            card.Child = grid;
            _list.Children.Add(card);
        }
    }
}
