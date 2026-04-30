#if WINDOWS
using Microsoft.Web.WebView2.Core;
#endif

namespace EChat.Maui;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();

#if WINDOWS
        blazorWebView.BlazorWebViewInitialized += (s, e) =>
        {
#if DEBUG
            e.WebView.CoreWebView2.OpenDevToolsWindow();
#endif
            var attachmentsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "echat", "attachments");

            if (Directory.Exists(attachmentsDir))
            {
                e.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "local-assets.com",
                    attachmentsDir,
                    CoreWebView2HostResourceAccessKind.Allow);
            }
        };
#endif
    }


}