using System.Diagnostics;
using ContextMenuManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace ContextMenuManager;

public sealed partial class AboutDialog : ContentDialog
{
    public const string RepoUrl = "https://github.com/arostad/ContextMenuManager";
    private const string AuthorUrl = "https://github.com/arostad";
    private const string LicenseUrl = RepoUrl + "/blob/main/LICENSE";

    private readonly AppSettings _settings;
    private UpdateInfo? _available;
    private bool _checking;
    private bool _updating;
    private bool _suppressCheck;

    public AboutDialog(AppSettings settings)
    {
        _settings = settings;
        // Suppress before InitializeComponent: the XAML checkbox would otherwise
        // fire Checked during init and overwrite the saved launch-check setting.
        _suppressCheck = true;
        InitializeComponent();
        VersionText.Text = "Version " + BuildInfo.Version;
        var png = Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
        if (File.Exists(png)) AppIcon.Source = new BitmapImage(new Uri(png));
        LaunchCheckBox.IsChecked = _settings.CheckForUpdatesAtLaunch;
        _suppressCheck = false;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ApplyColors();
        HideTitle();
        ActualThemeChanged += (_, _) => ApplyColors();
    }

    /// <summary>Like Andy's other About windows: a lighter body over a separate, darker footer strip.</summary>
    private void ApplyColors()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        var body = new SolidColorBrush(dark ? Color.FromArgb(255, 44, 44, 44) : Color.FromArgb(255, 255, 255, 255));
        var footer = new SolidColorBrush(dark ? Color.FromArgb(255, 28, 28, 28) : Color.FromArgb(255, 238, 238, 238));
        Background = body;
        if (GetTemplateChild("DialogSpace") is Grid space) space.Padding = new Thickness(0);
        if (GetTemplateChild("ContentScrollViewer") is ScrollViewer { Content: Grid inner })
        {
            inner.Background = body;
            inner.Padding = new Thickness(24, 24, 24, 24);
        }
        if (GetTemplateChild("CommandSpace") is Grid cmd)
        {
            cmd.Background = footer;
            cmd.Padding = new Thickness(24, 20, 24, 20);
        }
        HideTitle();
    }

    private void HideTitle()
    {
        foreach (var name in new[] { "Title", "TitleArea", "TitleTemplate", "TitleBackground" })
        {
            if (GetTemplateChild(name) is FrameworkElement el)
                el.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Check for updates on the left of the footer; accent Close on the right.</summary>
    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        ConstrainToWindow();
        LayoutFooterButtons();
        ApplyColors();
        if (GetTemplateChild("CloseButton") is Button close)
        {
            await Task.Delay(100);
            close.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>
    /// At 400% the privacy note was one line taller than the body and was
    /// clipped by the footer. Cap the body to the window minus the footer
    /// so it can scroll instead of being cut off.
    /// </summary>
    private void ConstrainToWindow()
    {
        if (XamlRoot is not { } root) { Log.Write("about constrain: no XamlRoot"); return; }
        MaxHeight = Math.Max(200, root.Size.Height - 16);
        // Footer is ~72 dip; don't take more than that or the privacy note
        // loses a line at 400%. The window itself is capped at 92% of the work area
        // so the last line should fit; this cap is a scroll safety net.
        BodyScroll.MaxHeight = Math.Max(120, root.Size.Height - 72);
        Log.Write($"about constrain: rootH={root.Size.Height:F0} dialogMax={MaxHeight:F0} bodyMax={BodyScroll.MaxHeight:F0}");
    }

    private void LayoutFooterButtons()
    {
        if (GetTemplateChild("PrimaryButton") is Button check)
        {
            check.HorizontalAlignment = HorizontalAlignment.Left;
            check.MinWidth = 120;
        }
        if (GetTemplateChild("CloseButton") is Button close)
        {
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.MinWidth = 120;
        }
        if (GetTemplateChild("CommandSpace") is Grid cmd)
        {
            if (cmd.Children.Count == 1 && cmd.Children[0] is Panel panel)
            {
                panel.HorizontalAlignment = HorizontalAlignment.Stretch;
                if (panel is Grid g && g.ColumnDefinitions.Count >= 2)
                {
                    // default template already splits space; leave it
                }
                else if (panel is StackPanel sp)
                {
                    sp.Orientation = Orientation.Horizontal;
                    sp.HorizontalAlignment = HorizontalAlignment.Stretch;
                    sp.Spacing = 12;
                }
            }
        }
    }

    private void OnLaunchCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressCheck) return;
        _settings.CheckForUpdatesAtLaunch = LaunchCheckBox.IsChecked == true;
        try { _settings.Save(); }
        catch (Exception ex)
        {
            Log.Write("settings save failed: " + ex.Message);
            SetStatus("Couldn't save that setting: " + ex.Message);
        }
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_updating) return;
        if (_available is { IsNewer: true }) StartUpdate();
        else StartCheck();
    }

    private void StartCheck()
    {
        if (_checking) return;
        _checking = true;
        IsPrimaryButtonEnabled = false;
        SetStatus("Checking...");
        _ = Task.Run(() =>
        {
            UpdateInfo info;
            try { info = UpdateChecker.Check(); }
            catch (Exception ex)
            {
                info = new UpdateInfo { RemoteVersion = "", IsNewer = false, Error = UpdateProtocol.DescribeError(ex, "Update check failed") };
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                _checking = false;
                IsPrimaryButtonEnabled = true;
                if (info.IsNewer)
                {
                    _available = info;
                    PrimaryButtonText = "Update now";
                    SetStatus("Version " + info.RemoteVersion + " is available.");
                    LayoutFooterButtons();
                    return;
                }
                SetStatus(string.IsNullOrEmpty(info.Error)
                    ? "You're on the latest version (" + BuildInfo.Version + ")."
                    : info.Error);
            });
        });
    }

    private void StartUpdate()
    {
        _updating = true;
        IsPrimaryButtonEnabled = false;
        SetStatus("Downloading. The app will restart when it is ready.");
        _ = Task.Run(() =>
        {
            try
            {
                UpdateChecker.DownloadAndRestart();
                DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _updating = false;
                    IsPrimaryButtonEnabled = true;
                    SetStatus(UpdateProtocol.DescribeError(ex));
                });
            }
        });
    }

    private void SetStatus(string text)
    {
        UpdateStatus.Text = text;
        UpdateStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAuthorClick(Hyperlink sender, HyperlinkClickEventArgs args) => OpenUrl(AuthorUrl);
    private void OnProjectClick(Hyperlink sender, HyperlinkClickEventArgs args) => OpenUrl(RepoUrl);
    private void OnLicenseClick(Hyperlink sender, HyperlinkClickEventArgs args) => OpenUrl(LicenseUrl);

    private void OnBackupsClick(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Backups);
            Log.Write("about: open backups " + AppPaths.Backups);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { AppPaths.Backups }, UseShellExecute = false });
        }
        catch (Exception ex) { Log.Write("about: open backups failed: " + ex.Message); }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Log.Write("about: open " + url);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("about: open url failed: " + ex.Message); }
    }
}
