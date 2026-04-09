using System.Text.Json;
using EChat.Core.Models;
using EChat.Core.Protocol;
using EChat.Core.Transport;
using Microsoft.Extensions.Logging;

namespace EChat.Core.Sync;

public class DeviceSyncService
{
    private readonly ILogger<DeviceSyncService> _logger;
    private readonly EmailTransportService _transportService;
    private readonly AccountConfig _accountConfig;

    public event Func<string, string, Task>? ReadStateReceived;
    public event Func<string, string, Task>? DraftReceived;
    public event Func<Dictionary<string, object>, Task>? SettingsReceived;

    public DeviceSyncService(
        ILogger<DeviceSyncService> logger,
        EmailTransportService transportService,
        AccountConfig accountConfig)
    {
        _logger = logger;
        _transportService = transportService;
        _accountConfig = accountConfig;
    }
    
    public async Task SyncReadStateAsync(string chatId, string lastReadMessageId)
    {
        var payload = new SyncPayload
        {
            SyncType = "read-state",
            DeviceId = _accountConfig.DeviceId,
            Timestamp = DateTimeOffset.UtcNow,
            Data = new Dictionary<string, object>
            {
                ["chat_id"] = chatId,
                ["last_read_message"] = lastReadMessageId
            }
        };
        
        await SendSyncMessageAsync(payload);
    }
    
    public async Task SyncDraftAsync(string chatId, string draftContent)
    {
        var payload = new SyncPayload
        {
            SyncType = "draft",
            DeviceId = _accountConfig.DeviceId,
            Timestamp = DateTimeOffset.UtcNow,
            Data = new Dictionary<string, object>
            {
                ["chat_id"] = chatId,
                ["draft_content"] = draftContent
            }
        };
        
        await SendSyncMessageAsync(payload);
    }
    
    public async Task SyncSettingsAsync(Dictionary<string, object> settings)
    {
        var payload = new SyncPayload
        {
            SyncType = "settings",
            DeviceId = _accountConfig.DeviceId,
            Timestamp = DateTimeOffset.UtcNow,
            Data = settings
        };
        
        await SendSyncMessageAsync(payload);
    }
    
    private async Task SendSyncMessageAsync(SyncPayload payload)
    {
        var message = new OutgoingMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Content = JsonSerializer.Serialize(payload.Data),
            Recipients = new List<string> { _accountConfig.Email },
            Tier = BatchTier.LowPriority,
            Timestamp = payload.Timestamp,
            Type = MessageType.Regular,
            SyncType = payload.SyncType,
            SyncDeviceId = payload.DeviceId
        };
        
        await _transportService.SendMessageAsync(message);
        
        _logger.LogDebug("Sent sync message: {SyncType} from device {DeviceId}", payload.SyncType, _accountConfig.DeviceId);
    }
    
    public async Task ProcessSyncMessageAsync(ParsedMessage message)
    {
        if (message.Headers.SyncDeviceId == _accountConfig.DeviceId)
        {
            return;
        }
        
        if (message.Headers.SyncType == null)
        {
            return;
        }
        
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(message.Content);
            
            if (data == null) return;
            
            switch (message.Headers.SyncType)
            {
                case "read-state":
                    if (ReadStateReceived != null && 
                        data.TryGetValue("chat_id", out var chatId) &&
                        data.TryGetValue("last_read_message", out var lastRead))
                    {
                        await ReadStateReceived(chatId.GetString()!, lastRead.GetString()!);
                    }
                    break;
                    
                case "draft":
                    if (DraftReceived != null &&
                        data.TryGetValue("chat_id", out var draftChatId) &&
                        data.TryGetValue("draft_content", out var content))
                    {
                        await DraftReceived(draftChatId.GetString()!, content.GetString()!);
                    }
                    break;
                    
                case "settings":
                    if (SettingsReceived != null)
                    {
                        var settings = data.ToDictionary(
                            kvp => kvp.Key,
                            kvp => (object)kvp.Value
                        );
                        await SettingsReceived(settings);
                    }
                    break;
            }
            
            _logger.LogDebug("Processed sync message: {SyncType} from device {DeviceId}", 
                message.Headers.SyncType, message.Headers.SyncDeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process sync message");
        }
    }
}