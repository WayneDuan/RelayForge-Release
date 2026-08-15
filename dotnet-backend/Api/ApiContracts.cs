using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayForge.Panel.Api;

public static class Api
{
    public static IResult Ok(object? data = null, string message = "操作成功") =>
        Results.Json(new { code = 0, msg = message, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), data });

    public static IResult Error(string message, int code = -1) =>
        Results.Json(new { code, msg = message, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), data = (object?)null });

    public static IResult TotpRequired(string message = "请输入 2FA 验证码") =>
        Results.Json(new { code = -2, msg = message, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), data = new { requiresTotp = true } });
}

public sealed class LoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? CaptchaId { get; set; }
    public string? TotpCode { get; set; }
}

public sealed class ChangePasswordRequest
{
    public string? NewUsername { get; set; }
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
    public string? ConfirmPassword { get; set; }
}

public sealed class TotpEnableRequest
{
    public string? CurrentPassword { get; set; }
    public string? Secret { get; set; }
    public string? Code { get; set; }
}

public sealed class TotpDisableRequest
{
    public string? CurrentPassword { get; set; }
    public string? Code { get; set; }
}

public sealed class TelegramSettingsRequest
{
    public bool Enabled { get; set; }
    public string? BotToken { get; set; }
    public string? ChatId { get; set; }
    public int TrafficThresholdPercent { get; set; } = 80;
    public bool NotifyFlow { get; set; } = true;
    public bool NotifyNode { get; set; } = true;
    public bool ClearBotToken { get; set; }
}

public class UserRequest
{
    public string? User { get; set; }
    public string? Pwd { get; set; }
    public long Flow { get; set; }
    public int Num { get; set; }
    public long ExpTime { get; set; }
    public long FlowResetTime { get; set; }
    public int? Status { get; set; }
}

public sealed class UserUpdateRequest : UserRequest
{
    public long Id { get; set; }
}

public class NodeRequest
{
    public string? Name { get; set; }
    public string? Ip { get; set; }
    public string? ServerIp { get; set; }
    public string? PortRange { get; set; }
    public int PortSta { get; set; }
    public int PortEnd { get; set; }
    public int? Http { get; set; }
    public int? Tls { get; set; }
    public int? Socks { get; set; }
}

public sealed class NodeUpdateRequest : NodeRequest
{
    public long Id { get; set; }
}

public sealed class TunnelRequest
{
    public string? Name { get; set; }
    public long InNodeId { get; set; }
    public long? OutNodeId { get; set; }
    public int Type { get; set; }
    public int FlowType { get; set; } = 2;
    public long FlowLimitGb { get; set; }
    public decimal? TrafficRatio { get; set; }
    public int? SpeedLimitKbps { get; set; }
    public string? InterfaceName { get; set; }
    public string? Protocol { get; set; }
    public string? AnyTlsPassword { get; set; }
    public string? TcpListenAddr { get; set; }
    public string? UdpListenAddr { get; set; }
}

public sealed class TunnelUpdateRequest
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public int FlowType { get; set; } = 2;
    public long FlowLimitGb { get; set; }
    public decimal? TrafficRatio { get; set; }
    public int? SpeedLimitKbps { get; set; }
    public string? Protocol { get; set; }
    public string? AnyTlsPassword { get; set; }
    public string? TcpListenAddr { get; set; }
    public string? UdpListenAddr { get; set; }
    public string? InterfaceName { get; set; }
}

public sealed class UserTunnelRequest
{
    public int UserId { get; set; }
    public int TunnelId { get; set; }
    public long Flow { get; set; }
    public int Num { get; set; }
    public long FlowResetTime { get; set; }
    public long ExpTime { get; set; }
    public int? SpeedId { get; set; }
}

public sealed class UserTunnelUpdateRequest
{
    public int Id { get; set; }
    public long Flow { get; set; }
    public int Num { get; set; }
    public long FlowResetTime { get; set; }
    public long ExpTime { get; set; }
    public int Status { get; set; }
    public int? SpeedId { get; set; }
}

public class ForwardRequest
{
    public string? Name { get; set; }
    public int TunnelId { get; set; }
    public long? XuiInboundId { get; set; }
    public long Flow { get; set; }
    public string? RemoteAddr { get; set; }
    public string? Strategy { get; set; }
    public int? InPort { get; set; }
    public int? OutPort { get; set; }
    public string? InterfaceName { get; set; }
    public int? UserId { get; set; }
}

public sealed class ForwardUpdateRequest : ForwardRequest
{
    public long Id { get; set; }
}

public class XuiConnectionRequest
{
    public string? Name { get; set; }
    public string? PanelUrl { get; set; }
    public string? ConnectHost { get; set; }
    public string? ApiToken { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? TwoFactorCode { get; set; }
    public bool VerifyTls { get; set; } = true;
}

public sealed class XuiSyncRequest
{
    public long Id { get; set; }
}

public class SpeedLimitRequest
{
    public string? Name { get; set; }
    public int Speed { get; set; }
    public long TunnelId { get; set; }
    public string? TunnelName { get; set; }
}

public sealed class SpeedLimitUpdateRequest : SpeedLimitRequest
{
    public long Id { get; set; }
}

public sealed class IdRequest
{
    public long Id { get; set; }
}

public sealed class NodeInstallRequest
{
    public long Id { get; set; }
    public string? Platform { get; set; }
}

public sealed class DiagnoseRequest
{
    public long ForwardId { get; set; }
    public long TunnelId { get; set; }
}

public sealed class ResetRequest
{
    public int Id { get; set; }
    public int Type { get; set; }
}

public sealed class FlowReport
{
    [JsonPropertyName("n")]
    public string? N { get; set; }

    [JsonPropertyName("u")]
    public long U { get; set; }

    [JsonPropertyName("d")]
    public long D { get; set; }
}

public sealed class CaptchaVerifyRequest
{
    public string? Id { get; set; }
    public JsonElement Data { get; set; }
}
