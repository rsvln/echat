namespace EChat.Core.Models;

public class ChatSettings
{
    public string ChatFolderName { get; set; } = "EpsilonChat";
    public string SyncFolderName { get; set; } = ".EpsilonChat-Sync";
    public string SubjectTemplate { get; set; } = "[EChat] {chatName}";
    public bool CreateSeparateFolders { get; set; } = true;
    public bool ShowInInbox { get; set; } = false;
    public bool AutoArchiveProcessed { get; set; } = true;
}