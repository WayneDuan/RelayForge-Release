using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RelayForge.Panel.Api;

public sealed class TelegramNotifier(
    Db db,
    AesCrypto crypto,
    IHttpClientFactory httpClientFactory,
    ILogger<TelegramNotifier> logger)
{
    private const string TokenKey = "telegram_bot_token";
    private const string EnabledKey = "telegram_enabled";
    private const string ChatIdKey = "telegram_chat_id";
    private const string ThresholdKey = "telegram_traffic_threshold";
    private const string NotifyFlowKey = "telegram_notify_flow";
    private const string NotifyNodeKey = "telegram_notify_node";

    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private TelegramConfig? _cachedSettings;
    private DateTimeOffset _cacheExpires;

    public async Task<TelegramPublicSettings> GetPublicSettingsAsync(CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        return new TelegramPublicSettings(
            settings.Enabled,
            !string.IsNullOrWhiteSpace(settings.Token),
            MaskToken(settings.Token),
            settings.ChatId,
            settings.ThresholdPercent,
            settings.NotifyFlow,
            settings.NotifyNode);
    }

    public async Task<NotificationOperationResult> SaveAsync(TelegramSettingsRequest request, CancellationToken ct)
    {
        var current = await LoadSettingsAsync(ct);
        var token = request.ClearBotToken
            ? ""
            : string.IsNullOrWhiteSpace(request.BotToken) ? current.Token : request.BotToken.Trim();
        var chatId = request.ChatId?.Trim() ?? "";
        var threshold = request.TrafficThresholdPercent;

        if (token.Length > 200 || chatId.Length > 200) return NotificationOperationResult.Failure("Bot Token 或 Chat ID 长度无效");
        if (threshold is < 1 or > 100) return NotificationOperationResult.Failure("流量通知阈值必须是 1-100");
        if (request.Enabled && (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId)))
            return NotificationOperationResult.Failure("启用 Telegram 通知需要 Bot Token 和 Chat ID");

        await UpsertAsync(EnabledKey, request.Enabled ? "1" : "0", ct);
        await UpsertAsync(ChatIdKey, chatId, ct);
        await UpsertAsync(ThresholdKey, threshold.ToString(CultureInfo.InvariantCulture), ct);
        await UpsertAsync(NotifyFlowKey, request.NotifyFlow ? "1" : "0", ct);
        await UpsertAsync(NotifyNodeKey, request.NotifyNode ? "1" : "0", ct);
        if (request.ClearBotToken || !string.IsNullOrWhiteSpace(request.BotToken))
            await UpsertAsync(TokenKey, string.IsNullOrWhiteSpace(token) ? "" : crypto.Encrypt(token), ct);

        InvalidateCache();
        return NotificationOperationResult.Success(request.Enabled ? "Telegram 通知已启用" : "Telegram 通知设置已保存");
    }

    public async Task<NotificationOperationResult> SendTestAsync(CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Token) || string.IsNullOrWhiteSpace(settings.ChatId))
            return NotificationOperationResult.Failure("请先保存并启用 Telegram 通知");

        var sent = await SendMessageAsync(settings, $"RelayForge Telegram 通知测试\n时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}", ct);
        return sent
            ? NotificationOperationResult.Success("测试消息已发送")
            : NotificationOperationResult.Failure("Telegram 测试消息发送失败，请检查 Bot Token、Chat ID 和服务器网络");
    }

    public async Task NotifyFlowThresholdsAsync(IReadOnlyDictionary<string, object?> row, long tunnelUsage, CancellationToken ct)
    {
        try
        {
            var settings = await LoadSettingsAsync(ct);
            if (!settings.Enabled || !settings.NotifyFlow) return;

            var userId = DbValue.Long(row, "user_id");
            var userUsage = DbValue.Long(row, "owner_in_flow") + DbValue.Long(row, "owner_out_flow");
            await NotifyQuotaAsync(settings, "用户", userId, DbValue.String(row, "owner_name"), userUsage, ToBytes(DbValue.Long(row, "owner_flow")), ct);

            var forwardId = DbValue.Long(row, "id");
            await NotifyQuotaAsync(settings, "转发", forwardId, DbValue.String(row, "name"), FlowOperations.Usage(row, "tunnel_flow"), FlowOperations.ToBytes(DbValue.Long(row, "flow")), ct);

            var relationId = DbValue.Long(row, "relation_id");
            if (relationId > 0)
            {
                var relationUsage = DbValue.Int(row, "tunnel_flow") == 1
                    ? DbValue.Long(row, "relation_in_flow")
                    : DbValue.Long(row, "relation_in_flow") + DbValue.Long(row, "relation_out_flow");
                await NotifyQuotaAsync(settings, "用户隧道", relationId, DbValue.String(row, "tunnel_name"), relationUsage, ToBytes(DbValue.Long(row, "relation_flow")), ct);
            }

            var tunnelId = DbValue.Long(row, "tunnel_id");
            await NotifyQuotaAsync(settings, "隧道", tunnelId, DbValue.String(row, "tunnel_name"), tunnelUsage, ToBytes(DbValue.Long(row, "tunnel_limit_gb")), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send Telegram traffic notification");
        }
    }

    public async Task NotifyNodeStatusAsync(long nodeId, string nodeName, bool online, CancellationToken ct)
    {
        try
        {
            var settings = await LoadSettingsAsync(ct);
            if (!settings.Enabled || !settings.NotifyNode) return;
            var state = online ? "上线" : "离线";
            var message = $"RelayForge 节点通知\n节点：{nodeName}（#{nodeId}）\n状态：{state}\n时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}";
            await SendMessageAsync(settings, message, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send Telegram node notification for {NodeId}", nodeId);
        }
    }

    private async Task NotifyQuotaAsync(TelegramConfig settings, string kind, long resourceId, string resourceName, long usage, long limit, CancellationToken ct)
    {
        if (resourceId <= 0 || limit <= 0) return;
        var percentage = usage * 100d / limit;
        var thresholdKey = $"flow:{kind}:{resourceId}:threshold";
        var limitKey = $"flow:{kind}:{resourceId}:limit";

        if (percentage < settings.ThresholdPercent)
            await ClearClaimAsync(thresholdKey, ct);
        else if (settings.ThresholdPercent < 100)
            await SendClaimedAsync(settings, thresholdKey,
                $"RelayForge 流量通知\n对象：{kind} {resourceName}\n当前：{FormatBytes(usage)} / {FormatBytes(limit)}（{percentage:0.#}%）\n已达到 {settings.ThresholdPercent}% 通知阈值。", ct);

        if (percentage < 100)
            await ClearClaimAsync(limitKey, ct);
        else
            await SendClaimedAsync(settings, limitKey,
                $"RelayForge 流量通知\n对象：{kind} {resourceName}\n当前：{FormatBytes(usage)} / {FormatBytes(limit)}（已达到额度）\n相关服务可能已自动暂停。", ct);
    }

    private async Task SendClaimedAsync(TelegramConfig settings, string eventKey, string message, CancellationToken ct)
    {
        if (!await TryClaimAsync(eventKey, ct)) return;
        if (!await SendMessageAsync(settings, message, ct)) await ClearClaimAsync(eventKey, ct);
    }

    private async Task<bool> SendMessageAsync(TelegramConfig settings, string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Token) || string.IsNullOrWhiteSpace(settings.ChatId)) return false;
        try
        {
            var endpoint = $"https://api.telegram.org/bot{Uri.EscapeDataString(settings.Token)}/sendMessage";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    chat_id = settings.ChatId,
                    text = message,
                    disable_web_page_preview = true
                }), Encoding.UTF8, "application/json")
            };
            using var response = await httpClientFactory.CreateClient("telegram").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Telegram returned HTTP {StatusCode}", response.StatusCode);
                return false;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telegram request failed");
            return false;
        }
    }

    private async Task<TelegramConfig> LoadSettingsAsync(CancellationToken ct)
    {
        if (_cachedSettings is not null && _cacheExpires > DateTimeOffset.UtcNow) return _cachedSettings;
        await _settingsLock.WaitAsync(ct);
        try
        {
            if (_cachedSettings is not null && _cacheExpires > DateTimeOffset.UtcNow) return _cachedSettings;
            var rows = await db.QueryAsync("SELECT name,value FROM vite_config WHERE name IN (@token,@enabled,@chat,@threshold,@flow,@node)", new Dictionary<string, object?>
            {
                ["token"] = TokenKey, ["enabled"] = EnabledKey, ["chat"] = ChatIdKey,
                ["threshold"] = ThresholdKey, ["flow"] = NotifyFlowKey, ["node"] = NotifyNodeKey
            }, ct);
            var values = rows.ToDictionary(row => DbValue.String(row, "name"), row => DbValue.String(row, "value"), StringComparer.OrdinalIgnoreCase);
            var encryptedToken = values.GetValueOrDefault(TokenKey) ?? "";
            var token = "";
            if (!string.IsNullOrWhiteSpace(encryptedToken))
            {
                try { token = crypto.Decrypt(encryptedToken); }
                catch { token = encryptedToken; }
            }
            var threshold = int.TryParse(values.GetValueOrDefault(ThresholdKey), out var parsedThreshold) ? parsedThreshold : 80;
            _cachedSettings = new TelegramConfig(
                IsTrue(values.GetValueOrDefault(EnabledKey)),
                token,
                values.GetValueOrDefault(ChatIdKey) ?? "",
                Math.Clamp(threshold, 1, 100),
                IsTrue(values.GetValueOrDefault(NotifyFlowKey), true),
                IsTrue(values.GetValueOrDefault(NotifyNodeKey), true));
            _cacheExpires = DateTimeOffset.UtcNow.AddSeconds(30);
            return _cachedSettings;
        }
        finally { _settingsLock.Release(); }
    }

    private async Task UpsertAsync(string name, string value, CancellationToken ct) => await db.ExecuteAsync(
        "INSERT INTO vite_config (name,value,time) VALUES (@name,@value,@time) ON DUPLICATE KEY UPDATE value=@value,time=@time",
        Domain.Params(("name", name), ("value", value), ("time", Domain.Now())), ct);

    private async Task<bool> TryClaimAsync(string eventKey, CancellationToken ct) => await db.ExecuteAsync(
        "INSERT IGNORE INTO telegram_notification_state (event_key,sent_time) VALUES (@key,@time)",
        Domain.Params(("key", eventKey), ("time", Domain.Now())), ct) == 1;

    private async Task ClearClaimAsync(string eventKey, CancellationToken ct) => await db.ExecuteAsync(
        "DELETE FROM telegram_notification_state WHERE event_key=@key", Domain.Params(("key", eventKey)), ct);

    private void InvalidateCache()
    {
        _cachedSettings = null;
        _cacheExpires = DateTimeOffset.MinValue;
    }

    private static bool IsTrue(string? value, bool fallback = false) => value is null ? fallback : value is "1" or "true" or "on";
    private static long ToBytes(long gigabytes) => FlowOperations.ToBytes(gigabytes);
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / 1024d / 1024d:0.##} MB";
        return $"{bytes / 1024d / 1024d / 1024d:0.##} GB";
    }
    private static string MaskToken(string token) => string.IsNullOrWhiteSpace(token) ? "" : token.Length <= 8 ? "********" : $"{token[..4]}...{token[^4..]}";

    private sealed record TelegramConfig(bool Enabled, string Token, string ChatId, int ThresholdPercent, bool NotifyFlow, bool NotifyNode);
}

public sealed record TelegramPublicSettings(
    bool Enabled,
    bool BotTokenConfigured,
    string BotTokenMasked,
    string ChatId,
    int TrafficThresholdPercent,
    bool NotifyFlow,
    bool NotifyNode);

public sealed record NotificationOperationResult(bool IsSuccess, string Message)
{
    public static NotificationOperationResult Success(string message) => new(true, message);
    public static NotificationOperationResult Failure(string message) => new(false, message);
}
