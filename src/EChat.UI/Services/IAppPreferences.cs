namespace EChat.UI.Services;

public interface IAppPreferences
{
    string Get(string key, string defaultValue);
    void Set(string key, string value);
}
