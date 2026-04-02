using EChat.Core.Models;

namespace EChat.Core.Protocol;

public class MessageOrderCorrector
{
    public void CorrectCausalOrder(List<ChatMessage> messages)
    {
        var messageDict = messages.ToDictionary(m => m.MessageId);
        
        foreach (var msg in messages.Where(m => m.InReplyTo != null))
        {
            if (messageDict.TryGetValue(msg.InReplyTo!, out var parent))
            {
                if (msg.Timestamp < parent.Timestamp)
                {
                    msg.DisplayTimestamp = parent.Timestamp.AddMilliseconds(1);
                    msg.ClockSkewDetected = true;
                }
            }
        }
    }
}