using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelayForge.Panel.Api;

public sealed class NodeGateway(Db db, ILogger<NodeGateway> logger, TelegramNotifier telegramNotifier, IConfiguration configuration)
{
    private readonly ConcurrentDictionary<long, Session> _nodes = new();
    private readonly ConcurrentDictionary<string, AdminSession> _admins = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NodeResponse>> _pending = new();
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new();

    public string IssueAdminTicket(AuthUser user)
    {
        if (!Domain.IsAdmin(user)) throw new InvalidOperationException("Administrator access is required.");
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _tickets.Where(pair => pair.Value.ExpiresAt <= now).ToArray()) _tickets.TryRemove(pair.Key, out _);
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _tickets[ticket] = new Ticket(user, now.AddMinutes(1));
        return ticket;
    }

    private bool TryConsumeAdminTicket(string value, out AuthUser? user)
    {
        user = null;
        if (string.IsNullOrWhiteSpace(value) || !_tickets.TryRemove(value, out var ticket)) return false;
        if (ticket.ExpiresAt <= DateTimeOffset.UtcNow || !Domain.IsAdmin(ticket.User)) return false;
        user = ticket.User;
        return true;
    }

    public async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var query = context.Request.Query;
        var type = query["type"].ToString();

        long nodeId = 0;
        string? nodeSecret = null;
        var nodeName = "node";
        var wasOffline = false;
        if (type == "1")
        {
            var secret = NodeAuth.ReadSecret(context, configuration);
            if (string.IsNullOrWhiteSpace(secret))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var rows = await db.QueryAsync("SELECT id, secret, name, status FROM `node` WHERE secret = @secret LIMIT 1", new Dictionary<string, object?> { ["secret"] = secret }, context.RequestAborted);
            if (rows.Count == 0)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            nodeId = DbValue.Long(rows[0], "id");
            nodeSecret = secret;
            nodeName = DbValue.String(rows[0], "name");
            wasOffline = DbValue.Int(rows[0], "status") != 1;
        }
        else if (type == "2")
        {
            if (!TryConsumeAdminTicket(query["ticket"].ToString(), out _))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (type == "1")
        {
            var session = new Session(nodeId, nodeSecret!, socket, new AesCrypto(nodeSecret!));
            if (_nodes.TryGetValue(nodeId, out var previous))
                await CloseQuietly(previous.Socket);
            _nodes[nodeId] = session;
            await db.ExecuteAsync("UPDATE `node` SET status = 1, version = @version, http = @http, tls = @tls, socks = @socks, updated_time = @now WHERE id = @id", new Dictionary<string, object?>
            {
                ["version"] = query["version"].ToString(),
                ["http"] = ParseInt(query["http"].ToString(), 0),
                ["tls"] = ParseInt(query["tls"].ToString(), 0),
                ["socks"] = ParseInt(query["socks"].ToString(), 0),
                ["now"] = Now(), ["id"] = nodeId
            }, context.RequestAborted);
            await BroadcastAsync(JsonSerializer.Serialize(new { id = nodeId, type = "status", data = 1 }), context.RequestAborted);
            if (wasOffline) _ = telegramNotifier.NotifyNodeStatusAsync(nodeId, nodeName, true, CancellationToken.None);
            // Start reading before replaying configuration. SendAsync waits for the
            // agent response, so delaying the read loop would make every replay
            // command time out.
            var readTask = ReadNodeLoopAsync(session, context.RequestAborted);
            try
            {
                await SyncTunnelsForNodeAsync(nodeId, context.RequestAborted);
                await readTask;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "节点配置同步失败: {NodeId}", nodeId);
                await CloseQuietly(session.Socket);
                try { await readTask; } catch { }
            }
            finally
            {
                if (_nodes.TryGetValue(nodeId, out var current) && ReferenceEquals(current, session))
                {
                    _nodes.TryRemove(nodeId, out _);
                    await db.ExecuteAsync("UPDATE `node` SET status = 0, updated_time = @now WHERE id = @id", new Dictionary<string, object?> { ["now"] = Now(), ["id"] = nodeId });
                    await BroadcastAsync(JsonSerializer.Serialize(new { id = nodeId, type = "status", data = 0 }), CancellationToken.None);
                    _ = telegramNotifier.NotifyNodeStatusAsync(nodeId, nodeName, false, CancellationToken.None);
                }
            }
        }
        else
        {
            var admin = new AdminSession(socket);
            _admins[Guid.NewGuid().ToString("N")] = admin;
            try { await ReadAdminLoopAsync(socket, context.RequestAborted); }
            catch (OperationCanceledException) { }
            finally
            {
                foreach (var pair in _admins.Where(pair => ReferenceEquals(pair.Value.Socket, socket)).ToArray())
                    _admins.TryRemove(pair.Key, out _);
            }
        }
    }

    public async Task<NodeResponse> SendAsync(long nodeId, string type, object data, CancellationToken cancellationToken = default)
    {
        if (!_nodes.TryGetValue(nodeId, out var session) || session.Socket.State != WebSocketState.Open)
            return new NodeResponse { Message = "节点不在线" };

        var requestId = Guid.NewGuid().ToString();
        var pending = new TaskCompletionSource<NodeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = pending;
        try
        {
            var payload = JsonSerializer.Serialize(new { type, data, requestId });
            var encrypted = JsonSerializer.Serialize(new { encrypted = true, data = session.Crypto.Encrypt(payload), timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            await session.SendAsync(Encoding.UTF8.GetBytes(encrypted), cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using (timeout.Token.Register(() => pending.TrySetCanceled(timeout.Token)))
                return await pending.Task;
        }
        catch (OperationCanceledException)
        {
            return new NodeResponse { Message = "等待节点响应超时" };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "发送节点命令失败: {NodeId} {Type}", nodeId, type);
            return new NodeResponse { Message = ex.Message };
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private async Task ReadNodeLoopAsync(Session session, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 128];
        while (session.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveTextAsync(session.Socket, buffer, cancellationToken);
            if (message is null) break;
            string json;
            try
            {
                using var envelope = JsonDocument.Parse(message);
                if (envelope.RootElement.TryGetProperty("encrypted", out var encrypted) && encrypted.GetBoolean())
                    json = session.Crypto.Decrypt(envelope.RootElement.GetProperty("data").GetString()!);
                else json = message;
            }
            catch (Exception ex) { logger.LogWarning(ex, "节点消息解密失败"); break; }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("requestId", out var requestId))
                {
                    var id = requestId.GetString();
                    if (!string.IsNullOrWhiteSpace(id) && _pending.TryRemove(id, out var pending))
                    {
                        pending.TrySetResult(new NodeResponse
                        {
                            Message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "",
                            Data = root.TryGetProperty("data", out var data) ? data.Clone() : null,
                            Success = !root.TryGetProperty("success", out var success) || success.GetBoolean()
                        });
                    }
                }
                else if (root.TryGetProperty("memory_usage", out _) || root.TryGetProperty("cpu_usage", out _))
                {
                    await session.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "call" })), cancellationToken);
                    await BroadcastAsync(JsonSerializer.Serialize(new { id = session.NodeId, type = "info", data = json }), cancellationToken);
                }
            }
            catch (JsonException) { logger.LogWarning("收到无法解析的节点消息"); }
        }
    }

    private async Task SyncTunnelsForNodeAsync(long nodeId, CancellationToken cancellationToken)
    {
        var tunnels = await db.QueryAsync("SELECT t.*,n.server_ip entry_ip,o.server_ip out_ip FROM `tunnel` t LEFT JOIN `node` n ON n.id=t.in_node_id LEFT JOIN `node` o ON o.id=t.out_node_id WHERE t.in_node_id=@node OR t.out_node_id=@node ORDER BY t.id", new Dictionary<string, object?> { ["node"] = nodeId }, cancellationToken);
        foreach (var tunnel in tunnels)
        {
            var error = await ForwardOperations.SyncTunnelForNodeAsync(tunnel, nodeId, db, this, cancellationToken);
            if (!string.IsNullOrWhiteSpace(error)) logger.LogWarning("隧道同步失败: {TunnelId}, NodeId={NodeId}: {Error}", DbValue.Long(tunnel, "id"), nodeId, error);
        }
    }

    private static async Task ReadAdminLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            if (await ReceiveTextAsync(socket, buffer, cancellationToken) is null) break;
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            output.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task CloseQuietly(WebSocket socket)
    {
        try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "replaced", CancellationToken.None); }
        catch { }
    }

    private static int ParseInt(string value, int fallback) => int.TryParse(value, out var result) ? result : fallback;
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private async Task BroadcastAsync(string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        foreach (var admin in _admins.Values.ToArray())
        {
            if (admin.Socket.State != WebSocketState.Open) continue;
            try { await admin.SendAsync(bytes, cancellationToken); }
            catch { }
        }
    }

    private sealed class Session(long nodeId, string secret, WebSocket socket, AesCrypto crypto)
    {
        public long NodeId { get; } = nodeId;
        public string Secret { get; } = secret;
        public WebSocket Socket { get; } = socket;
        public AesCrypto Crypto { get; } = crypto;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public async Task SendAsync(byte[] data, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try { await Socket.SendAsync(data, WebSocketMessageType.Text, true, cancellationToken); }
            finally { _sendLock.Release(); }
        }
    }

    private sealed class AdminSession(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public async Task SendAsync(byte[] data, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try { await Socket.SendAsync(data, WebSocketMessageType.Text, true, cancellationToken); }
            finally { _sendLock.Release(); }
        }
    }

    private sealed record Ticket(AuthUser User, DateTimeOffset ExpiresAt);
}

public sealed class NodeResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public JsonElement? Data { get; set; }
}
