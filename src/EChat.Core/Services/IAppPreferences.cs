namespace EChat.Core.Services;

public interface IAppPreferences
{
    string Get(string key, string defaultValue);
    void Set(string key, string value);
    IDictionary<string, string> ExportAll();
    void ImportAll(IDictionary<string, string> data);
}
