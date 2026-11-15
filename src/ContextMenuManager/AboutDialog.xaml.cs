using System.Diagnostics;
using System.Reflection;
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

    public AboutDialog()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
        VersionText.Text = $"Version {v.Major}.{v.Minor}.{v.Build}";
        var png = Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
        if (File.Exists(png)) AppIcon.Source = new BitmapImage(new Uri(png));
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ApplyColors();
        ActualThemeChanged += (_, _) => ApplyColors();
    }

    /// <summary>Like Andy's other About windows: a lighter body over a separate, darker footer strip that holds Close.</summary>
    private void ApplyColors()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        var body = new SolidColorBrush(dark ? Color.FromArgb(255, 44, 44, 44) : Color.FromArgb(255, 255, 255, 255));
        var footer = new SolidColorBrush(dark ? Color.FromArgb(255, 28, 28, 28) : Color.FromArgb(255, 238, 238, 238));
        Background = body;
        if (GetTemplateChild("DialogSpace") is Grid space) space.Padding = new Thickness(0); // body and footer run edge to edge
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
    }

    /// <summary>Close sits on the right of the footer at its natural width and takes focus instead of the first link.</summary>
    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (GetTemplateChild("CloseButton") is not Button close) return;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.MinWidth = 120;
        ApplyColors(); // again after the template's visual states have run
        await Task.Delay(100); // the dialog moves focus to the first link after Opened
        close.Focus(FocusState.Programmatic);
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
