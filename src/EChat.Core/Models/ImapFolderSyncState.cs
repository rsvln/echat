namespace EChat.Core.Models;

/// <summary>
/// Stores IMAP synchronization state per account+folder.
/// LastSyncedUid is the highest contiguous UID we have successfully saved to the DB
/// with no gaps before it — used as the anchor for the next sync instead of a date/time.
/// UidValidity must match the server's UIDVALIDITY; if it changes, LastSyncedUid is reset to 0.
/// </summary>
public class ImapFolderSyncState
{
    public required string AccountId { get; set; }
    public required string FolderName { get; set; }

    /// <summary>Server's UIDVALIDITY value. If the server changes this, the local UID cache is stale.</summary>
    public uint UidValidity { get; set; }

    /// <summary>
    /// Highest contiguous UID we have saved without gaps.
    /// On next sync: SEARCH for UIDs > LastSyncedUid.
    /// </summary>
    public uint LastSyncedUid { get; set; }
}
