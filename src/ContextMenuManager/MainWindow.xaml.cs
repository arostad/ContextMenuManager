using System.Collections.ObjectModel;
using System.Reflection;
using ContextMenuManager.Models;
using ContextMenuManager.Services;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace ContextMenuManager;

public sealed partial class MainWindow : Window
{
    private readonly MenuService _service = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<MenuEntry> _entries = new();
    private readonly UISettings _uiSettings = new();
    private readonly IntPtr _hwnd;
    private bool _suppressClassic;
    private bool _sized;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.Title = "Context Menu Manager";
        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();

        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
        AboutText.Text = $"Context Menu Manager {v.Major}.{v.Minor}.{v.Build}  ·  MIT License  ·  © 2026 Andy Rostad";

        EntryList.ItemsSource = _entries;
        LoadData();

        ApplyTheme(_settings.Theme, save: false);
        Root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateTitleBar);
        AppWindow.Changed += OnAppWindowChanged;
        Root.Loaded += (_, _) => SizeToContent();
    }

    private void LoadData()
    {
        _suppressClassic = true;
        ClassicToggle.IsOn = _service.IsClassicMenuOn();
        _suppressClassic = false;

        _entries.Clear();
        foreach (var e in _service.Scan()) _entries.Add(e);
        EmptyText.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListHeader.Text = $"Third-party menu entries ({_entries.Count})";
        Log.Write($"loaded {_entries.Count} entries; classic={ClassicToggle.IsOn}");
    }

    // ---------------- sizing ----------------

    /// <summary>Initial size comes from the content (no fixed pixel sizes), capped by the monitor work area.</summary>
    private void SizeToContent()
    {
        if (_sized || Root.XamlRoot is null) return;
        _sized = true;
        try
        {
            double scale = Root.XamlRoot.RasterizationScale;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            double em = (double)Application.Current.Resources["ControlContentThemeFontSize"];
            double maxW = Math.Min(area.Width / scale * 0.9, em * 60); // a comfortable reading width
            double maxH = area.Height / scale * 0.85;

            Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = Math.Min(Root.DesiredSize.Width, maxW);
            Root.Measure(new Size(w, double.PositiveInfinity));
            double h = Math.Min(Root.DesiredSize.Height, maxH);

            var size = new SizeInt32((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale));
            AppWindow.ResizeClient(size);
            var outer = AppWindow.Size;
            AppWindow.Move(new PointInt32(area.X + Math.Max(0, (area.Width - outer.Width) / 2), area.Y + Math.Max(0, (area.Height - outer.Height) / 2)));
            Log.Write($"sized: scale={scale} dip={w:F0}x{h:F0} client={size.Width}x{size.Height} work={area.Width}x{area.Height}");
        }
        catch (Exception ex) { Log.Write("size: " + ex); }
    }

    // ---------------- theme ----------------

    private void ApplyTheme(string theme, bool save)
    {
        Root.RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        ThemeSystem.IsChecked = theme is not ("Light" or "Dark");
        ThemeLight.IsChecked = theme == "Light";
        ThemeDark.IsChecked = theme == "Dark";
        ThemeLabel.Text = "Theme: " + (theme is "Light" or "Dark" ? theme : "System");
        if (save)
        {
            _settings.Theme = theme;
            try { _settings.Save(); } catch (Exception ex) { ShowError("Couldn't save settings: " + ex.Message); }
        }
        UpdateTitleBar();
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t }) ApplyTheme(t, save: true);
    }

    private bool IsDark()
    {
        if (Root.RequestedTheme != ElementTheme.Default) return Root.RequestedTheme == ElementTheme.Dark;
        var bg = _uiSettings.GetColorValue(UIColorType.Background);
        return bg.R + bg.G + bg.B < 384;
    }

    private void UpdateTitleBar()
    {
        bool dark = IsDark();
        int v = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
        var tb = AppWindow.TitleBar;
        var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        tb.ForegroundColor = fg;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        var bg = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        tb.BackgroundColor = bg;
        tb.InactiveBackgroundColor = bg;
        tb.ButtonBackgroundColor = bg;
        tb.ButtonInactiveBackgroundColor = bg;
        tb.InactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 160, 160, 160) : Windows.UI.Color.FromArgb(255, 110, 110, 110);
        tb.ButtonInactiveForegroundColor = tb.InactiveForegroundColor;
        tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 45, 45, 45) : Windows.UI.Color.FromArgb(255, 229, 229, 229);
        tb.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 55, 55, 55) : Windows.UI.Color.FromArgb(255, 215, 215, 215);
    }

    /// <summary>Standards: standard title bar uses only DWMWA_BORDER_COLOR = none while maximized (no WM_NCCALCSIZE).</summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange && !args.DidSizeChange) return;
        bool max = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        int color = max ? unchecked((int)0xFFFFFFFE) : unchecked((int)0xFFFFFFFF);
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref color, sizeof(int));
    }

    // ---------------- toggles ----------------

    private void OnClassicToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressClassic) return;
        bool on = ClassicToggle.IsOn;
        try
        {
            _service.SetClassicMenu(on);
            ShowApplied();
        }
        catch (Exception ex)
        {
            Log.Write("classic toggle: " + ex);
            _suppressClassic = true;
            ClassicToggle.IsOn = _service.IsClassicMenuOn();
            _suppressClassic = false;
            ShowError("Couldn't change the classic menu setting: " + ex.Message);
        }
    }

    private void OnEntryToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch sw || sw.DataContext is not MenuEntry entry) return;
        if (sw.IsOn == entry.IsEnabled) return; // binding update, not a user change
        try
        {
            _service.SetEnabled(entry, sw.IsOn);
            ShowApplied();
        }
        catch (Exception ex)
        {
            Log.Write($"toggle {entry.Id}: {ex}");
            sw.IsOn = entry.IsEnabled;
            ShowError($"Couldn't change \"{entry.Name}\": {ex.Message}");
        }
    }

    private void ShowApplied()
    {
        ErrorBar.IsOpen = false;
        ApplyBar.IsOpen = true;
    }

    private void ShowError(string msg)
    {
        ApplyBar.IsOpen = false;
        ErrorBar.Message = msg;
        ErrorBar.IsOpen = true;
    }

    private async void OnRestartExplorer(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = "Restart Explorer?",
            Content = new TextBlock
            {
                Text = "Explorer will close and reopen. Open File Explorer windows will close and the taskbar will disappear for a moment.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Restart Explorer",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        RestartButton.IsEnabled = false;
        try
        {
            await MenuService.RestartExplorerAsync();
            ApplyBar.IsOpen = false;
        }
        catch (Exception ex) { ShowError("Couldn't restart Explorer: " + ex.Message); }
        finally { RestartButton.IsEnabled = true; }
    }
}
