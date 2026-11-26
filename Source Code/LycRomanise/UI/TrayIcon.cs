using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using LycRomanise.Core;
using WinForms = System.Windows.Forms;

namespace LycRomanise.UI;

public sealed class TrayIcon : IDisposable
{
    readonly AppController _app;
    readonly WinForms.NotifyIcon _icon;
    readonly Window _host;
    ContextMenu? _menu;
    string _status = "Waiting for music";

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

    public TrayIcon(AppController app)
    {
        _app = app;
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/lyrics-overlay.ico"))?.Stream;
        _icon = new WinForms.NotifyIcon
        {
            Icon = stream != null ? new System.Drawing.Icon(stream) : System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "LycRomanise",
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right) ShowMenuAtCursor();
        };
        _icon.MouseDoubleClick += (_, _) => _app.ShowLyricsWindow();
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowMenuAtCursor(); };

        // An invisible window owns the menu, so it closes when you click elsewhere.
        _host = new Window
        {
            Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent, ShowActivated = true, Left = -10000, Top = -10000,
        };
        _host.Show();
        _host.Hide();
    }

    MenuItem Item(string header, string glyph, Action click, bool? check = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["Icons"], FontSize = 14 },
        };
        if (check != null) { item.IsCheckable = true; item.IsChecked = check.Value; }
        item.Click += (_, _) => click();
        return item;
    }

    ContextMenu Build()
    {
        var s = _app.Settings;
        var menu = new ContextMenu { MinWidth = 250 };
        var np = _app.NowPlaying;
        menu.Items.Add(new MenuItem
        {
            Header = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = np != null ? $"{np.Title}" : "Nothing playing", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 },
                    new TextBlock { Text = np != null ? np.Artist : "", Opacity = 0.7, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 },
                    new TextBlock { Text = _status, Opacity = 0.6, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 },
                },
            },
            IsEnabled = false,
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Full lyrics", "", _app.ShowLyricsWindow));
        menu.Items.Add(Item(s.Locked ? "Unlock strip" : "Lock strip (click-through)", s.Locked ? "" : "", () => _app.SetLocked(!s.Locked)));
        menu.Items.Add(Item("Choose lyrics for this song…", "", _app.ShowPicker));
        menu.Items.Add(Item("Search lyrics again", "", () => _app.Refetch(skipCache: true)));
        var timing = new MenuItem { Header = "Timing for this song", Icon = new TextBlock { Text = "", FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["Icons"] } };
        timing.Items.Add(Item("Earlier by 0.25 s", "", () => _app.NudgeOffset(250)));
        timing.Items.Add(Item("Later by 0.25 s", "", () => _app.NudgeOffset(-250)));
        timing.Items.Add(Item("Reset", "", () => _app.NudgeOffset(-_app.SongOffset)));
        menu.Items.Add(timing);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show lyrics on this PC", "", () =>
        {
            var copy = _app.Settings.Copy();
            copy.HideDesktopStrip = !copy.HideDesktopStrip;
            _app.ApplySettings(copy, false);
        }, !s.HideDesktopStrip));
        menu.Items.Add(Item("Web view for phones / OBS", "", () =>
        {
            var copy = _app.Settings.Copy();
            copy.WebEnabled = !copy.WebEnabled;
            _app.ApplySettings(copy, false);
            if (copy.WebEnabled) Toast($"Web view on: {_app.Web.Url}");
        }, s.WebEnabled));
        menu.Items.Add(Item("Reset strip position", "", () => _app.Overlay.ResetPosition()));
        menu.Items.Add(new Separator());
        if (_app.Update is { } up)
            menu.Items.Add(Item($"Update to {up.Tag}", "", async () =>
            {
                if (!Updater.CanSelfUpdate(up)) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(up.PageUrl) { UseShellExecute = true }); return; }
                Toast($"Downloading {up.Tag}…");
                var err = await _app.InstallUpdateAsync(null);
                if (err != null) Toast("Update failed: " + err);
            }));
        menu.Items.Add(Item("Settings", "", _app.ShowSettings));
        menu.Items.Add(Item("Quit", "", _app.Quit));
        return menu;
    }

    public void ShowMenuAtCursor()
    {
        _menu = Build();
        _host.Show();
        _host.Activate();
        SetForegroundWindow(new WindowInteropHelper(_host).Handle);
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.PlacementTarget = _host;
        _menu.Closed += (_, _) => _host.Hide();
        _menu.IsOpen = true;
    }

    public void SetStatus(string status)
    {
        _status = status;
        var np = _app.NowPlaying;
        var text = np != null ? $"{np.Artist} - {np.Title}\n{status}" : $"LycRomanise\n{status}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void Refresh() { }

    public void Toast(string text) => _icon.ShowBalloonTip(2500, "LycRomanise", text, WinForms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
