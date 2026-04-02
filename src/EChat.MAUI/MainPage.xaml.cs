namespace EChat.Maui;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();

#if DEBUG && WINDOWS
        blazorWebView.BlazorWebViewInitialized += (s, e) =>
        {
            e.WebView.CoreWebView2.OpenDevToolsWindow();
        };
#endif
    }
}