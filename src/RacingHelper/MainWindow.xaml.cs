using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace RacingHelper.App;

public partial class MainWindow : Window
{
    readonly string _url;

    public MainWindow(string url)
    {
        _url = url;
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        Loaded += async (_, _) => await InitWeb();
    }

    async Task InitWeb()
    {
        try
        {
            var dataDir = Path.Combine(App.Current.Settings.Current.DataFolder, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                // external links open in the user's browser
                e.Handled = true;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
            };
            Web.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            App.Log("WebView2 unavailable: " + ex.Message);
            Web.Visibility = Visibility.Collapsed;
            Fallback.Visibility = Visibility.Visible;
            FallbackText.Text = $"The embedded dashboard needs the Microsoft Edge WebView2 runtime. You can use the dashboard in your browser instead: {_url}";
        }
    }

    void OpenBrowser_Click(object sender, RoutedEventArgs e) => App.Current.OpenInBrowser();
}
