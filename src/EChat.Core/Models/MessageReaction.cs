namespace EChat.Core.Models;

public class MessageReaction
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string MessageId { get; set; }
    public required string Emoji { get; set; }
    public required string Sender { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
