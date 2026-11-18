using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LycRomanise.Core;

namespace LycRomanise.UI;

public partial class OverlayWindow : Window
{
    readonly AppController _app;
    IntPtr _hwnd;
    bool _hovering;

    public KaraokeView Karaoke => View;

    public OverlayWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyLock();
            // A launcher may ask for the first window to start minimized; the strip never should.
            ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { _app.ShowLyricsWindow(); return; }
            if (_app.Settings.Locked || e.OriginalSource is System.Windows.Controls.Button) return;
            try { DragMove(); } catch { }
            SavePosition();
        };
        MouseEnter += (_, _) => { _hovering = true; UpdateChrome(); };
        MouseLeave += (_, _) => { _hovering = false; UpdateChrome(); };
        MouseRightButtonUp += (_, _) => _app.Tray?.ShowMenuAtCursor();
        MouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control) _app.NudgeOffset(e.Delta > 0 ? 100 : -100);
        };
    }

    public void ApplySettings(AppSettings s)
    {
        Width = s.StripWidth;
        ShowInTaskbar = s.ShowInTaskbar;
        var bg = Theme.ParseColor(s.BackgroundColor, Colors.Black);
        Panel.Background = new SolidColorBrush(Color.FromArgb((byte)(s.BackgroundOpacity * 255), bg.R, bg.G, bg.B));
        Panel.CornerRadius = new CornerRadius(s.CornerRadius);
        View.SetStyle(new KaraokeStyle
        {
            Unsung = Theme.ParseColor(s.UnsungColor, Colors.Gold),
            Sung = Theme.ParseColor(s.SungColor, Colors.HotPink),
            Next = Theme.ParseColor(s.NextLineColor, Colors.Gold),
            Outline = Theme.ParseColor(s.OutlineColor, Colors.Black),
            Sub = Theme.ParseColor(s.SubColor, Colors.White),
            Typeface = new Typeface(new FontFamily(s.FontFamily + ", Segoe UI, Yu Gothic UI, Malgun Gothic, Microsoft YaHei UI"),
                FontStyles.Normal, s.Bold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal),
            FontSize = s.FontSize,
            OutlineWidth = s.OutlineWidth,
            ShowNext = s.ShowNextLine,
            ShowTranslation = s.ShowTranslation,
            ShowBacking = s.ShowBackingVocals,
            Smooth = s.SmoothSweep,
            Align = s.Align switch { StripAlign.Left => TextAlignment.Left, StripAlign.Right => TextAlignment.Right, _ => TextAlignment.Center },
        });
        View.Effect = s.Glow
            ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55, Color = Theme.ParseColor(s.OutlineColor, Colors.Black), RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance }
            : null;
        View.Height = View.DesiredStripHeight();
        ApplyLock();
        UpdateChrome();
    }

    public void PlaceInitially(AppSettings s)
    {
        var area = SystemParameters.WorkArea;
        if (s.Left is double l && s.Top is double t && IsOnScreen(l, t))
        {
            Left = l; Top = t;
        }
        else
        {
            Left = area.Left + (area.Width - s.StripWidth) / 2;
            Top = area.Top + area.Height * 0.12;
        }
    }

    static bool IsOnScreen(double l, double t) =>
        l > SystemParameters.VirtualScreenLeft - 200 && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
        && t > SystemParameters.VirtualScreenTop - 50 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    public void ResetPosition()
    {
        _app.Settings.Left = _app.Settings.Top = null;
        PlaceInitially(_app.Settings);
        SavePosition();
    }

    void SavePosition()
    {
        _app.Settings.Left = Left;
        _app.Settings.Top = Top;
        _app.SaveSettings();
    }

    // ---------------------------------------------------------- lock / click-through
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int cmd);

    public void ApplyLock()
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE;
        ex = _app.Settings.Locked ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        ex = _app.Settings.ShowInTaskbar ? (ex & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW : (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        UpdateChrome();
    }

    void UpdateChrome()
    {
        bool unlocked = !_app.Settings.Locked;
        bool show = unlocked && _hovering;
        Controls.IsHitTestVisible = show;
        Controls.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(160)));
        LockBtn.Visibility = _app.Settings.HideLockIcon ? Visibility.Collapsed : Visibility.Visible;
        Panel.BorderBrush = new SolidColorBrush(show ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Colors.Transparent);
        if (show && _app.Settings.BackgroundOpacity < 0.25)
            Panel.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x10, 0x10, 0x14));
        else
        {
            var bg = Theme.ParseColor(_app.Settings.BackgroundColor, Colors.Black);
            Panel.Background = new SolidColorBrush(Color.FromArgb((byte)(_app.Settings.BackgroundOpacity * 255), bg.R, bg.G, bg.B));
        }
        CreditText.BeginAnimation(OpacityProperty, new DoubleAnimation(show || _app.CreditRequired ? 1 : 0, TimeSpan.FromMilliseconds(160)));
    }

    public void SetCredit(string? credit)
    {
        CreditText.Text = credit ?? "";
        UpdateChrome();
    }

    void Lyrics_Click(object sender, RoutedEventArgs e) => _app.ShowLyricsWindow();
    void Sources_Click(object sender, RoutedEventArgs e) => _app.ShowPicker();
    void Earlier_Click(object sender, RoutedEventArgs e) => _app.NudgeOffset(+250);
    void Later_Click(object sender, RoutedEventArgs e) => _app.NudgeOffset(-250);
    void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();
    void Lock_Click(object sender, RoutedEventArgs e) => _app.SetLocked(true);
}
