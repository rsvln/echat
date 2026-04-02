using EChat.UI.Services;

namespace EChat.Maui.Services;

public class AppPreferences : IAppPreferences
{
    public string Get(string key, string defaultValue) =>
        Microsoft.Maui.Storage.Preferences.Get(key, defaultValue);

    public void Set(string key, string value) =>
        Microsoft.Maui.Storage.Preferences.Set(key, value);
}
