using System.ComponentModel.DataAnnotations;

namespace EChat.Core.Models;

public class Attachment
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string MessageId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public string? FilePath { get; set; }
    public string? Caption { get; set; }
    public bool IsImage { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}
