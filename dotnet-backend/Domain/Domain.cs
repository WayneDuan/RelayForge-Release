using System.Text.Json;

namespace RelayForge.Panel.Api;

public static class Domain
{
    public static Dictionary<string, object?> Params(params (string Name, object? Value)[] values) =>
        values.ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);

    public static bool IsAdmin(AuthUser user) => user.RoleId == 0;
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static string NewSecret() => Guid.NewGuid().ToString("N");

    public static object Node(IReadOnlyDictionary<string, object?> row, bool includeSecret = false) => new
    {
        id = DbValue.Long(row, "id"), name = DbValue.String(row, "name"), ip = DbValue.String(row, "ip"),
        serverIp = DbValue.String(row, "server_ip"), version = DbValue.String(row, "version"),
        portRange = DbValue.String(row, "port_range") is { Length: > 0 } configuredRange ? configuredRange : DbValue.Int(row, "port_sta") > 0 && DbValue.Int(row, "port_end") > 0 ? string.Concat(DbValue.Int(row, "port_sta"), "-", DbValue.Int(row, "port_end")) : "",
        portSta = DbValue.Int(row, "port_sta"), portEnd = DbValue.Int(row, "port_end"),
        http = DbValue.Int(row, "http"), tls = DbValue.Int(row, "tls"), socks = DbValue.Int(row, "socks"),
        status = DbValue.Int(row, "status"), secret = includeSecret ? DbValue.String(row, "secret") : null,
        createdTime = DbValue.Long(row, "created_time"), updatedTime = DbValue.Long(row, "updated_time")
    };

    public static object Tunnel(IReadOnlyDictionary<string, object?> row) => new
    {
        id = DbValue.Long(row, "id"), name = DbValue.String(row, "name"),
        inNodeId = DbValue.Long(row, "in_node_id"), inIp = DbValue.String(row, "in_ip"),
        outNodeId = DbValue.Long(row, "out_node_id"), outIp = DbValue.String(row, "out_ip"),
        type = DbValue.Int(row, "type"), flow = DbValue.Int(row, "flow"), flowType = DbValue.Int(row, "flow"),
        flowLimitGb = DbValue.Long(row, "flow_limit_gb"),
        inFlow = DbValue.Long(row, "tunnel_in_flow"), outFlow = DbValue.Long(row, "tunnel_out_flow"),
        protocol = DbValue.String(row, "protocol"), trafficRatio = DbValue.Decimal(row, "traffic_ratio"),
        speedLimitKbps = DbValue.Int(row, "speed_limit_kbps"),
        tcpListenAddr = DbValue.String(row, "tcp_listen_addr"), udpListenAddr = DbValue.String(row, "udp_listen_addr"),
        interfaceName = DbValue.String(row, "interface_name"), status = DbValue.Int(row, "status"),
        createdTime = DbValue.Long(row, "created_time"), updatedTime = DbValue.Long(row, "updated_time")
    };

    public static object TunnelList(IReadOnlyDictionary<string, object?> row) => new
    {
        id = DbValue.Long(row, "id"), name = DbValue.String(row, "name"), ip = DbValue.String(row, "in_ip"),
        inNodePortSta = DbValue.Int(row, "port_sta"), inNodePortEnd = DbValue.Int(row, "port_end"),
        type = DbValue.Int(row, "type"), protocol = DbValue.String(row, "protocol"),
        flowType = DbValue.Int(row, "flow"), flowLimitGb = DbValue.Long(row, "flow_limit_gb")
    };

    public static object Forward(IReadOnlyDictionary<string, object?> row) => new
    {
        id = DbValue.Long(row, "id"), userId = DbValue.Int(row, "user_id"), userName = DbValue.String(row, "user_name"),
        name = DbValue.String(row, "name"), tunnelId = DbValue.Int(row, "tunnel_id"),
        tunnelName = DbValue.String(row, "tunnel_name"), inIp = DbValue.String(row, "in_ip"),
        entryIp = DbValue.String(row, "entry_ip"),
        tunnelType = DbValue.Int(row, "tunnel_type"),
        xuiInboundId = DbValue.Long(row, "xui_inbound_id"), xuiInboundName = DbValue.String(row, "xui_inbound_name"),
        inPort = DbValue.Int(row, "in_port"), outPort = DbValue.NullableInt(row, "out_port"),
        remoteAddr = DbValue.String(row, "remote_addr"), strategy = DbValue.String(row, "strategy"),
        interfaceName = DbValue.String(row, "interface_name"), flow = DbValue.Long(row, "flow"),
        tunnelFlow = DbValue.Int(row, "tunnel_flow"), tunnelFlowLimitGb = DbValue.Long(row, "tunnel_limit_gb"),
        inFlow = DbValue.Long(row, "in_flow"),
        outFlow = DbValue.Long(row, "out_flow"), status = DbValue.Int(row, "status"), inx = DbValue.Int(row, "inx"),
        createdTime = DbValue.Long(row, "created_time"), updatedTime = DbValue.Long(row, "updated_time")
    };

    public static object XuiConnection(IReadOnlyDictionary<string, object?> row) => new
    {
        id = DbValue.Long(row, "id"), name = DbValue.String(row, "name"), panelUrl = DbValue.String(row, "panel_url"),
        connectHost = DbValue.String(row, "connect_host"), verifyTls = DbValue.Int(row, "verify_tls") != 0,
        status = DbValue.Int(row, "status"), inboundCount = DbValue.Int(row, "inbound_count"),
        lastSyncTime = DbValue.Long(row, "last_sync_time"), lastError = DbValue.String(row, "last_error"),
        createdTime = DbValue.Long(row, "created_time"), updatedTime = DbValue.Long(row, "updated_time")
    };

    public static object XuiInbound(IReadOnlyDictionary<string, object?> row) => new
    {
        id = DbValue.Long(row, "id"), connectionId = DbValue.Long(row, "connection_id"),
        connectionName = DbValue.String(row, "connection_name"), externalId = DbValue.String(row, "external_id"),
        name = DbValue.String(row, "name"), tag = DbValue.String(row, "tag"), protocol = DbValue.String(row, "protocol"),
        port = DbValue.Int(row, "port"), listen = DbValue.String(row, "listen"), remoteAddr = DbValue.String(row, "remote_addr"),
        enabled = DbValue.Int(row, "enabled") != 0, lastSeenTime = DbValue.Long(row, "last_seen_time")
    };

    public static object User(IReadOnlyDictionary<string, object?> row) => new
    {
        id = DbValue.Long(row, "id"), user = DbValue.String(row, "user"), name = DbValue.String(row, "user"),
        roleId = DbValue.Int(row, "role_id"), expTime = DbValue.Long(row, "exp_time"), flow = DbValue.Long(row, "flow"),
        inFlow = DbValue.Long(row, "in_flow"), outFlow = DbValue.Long(row, "out_flow"), num = DbValue.Int(row, "num"),
        flowResetTime = DbValue.Long(row, "flow_reset_time"), status = DbValue.Int(row, "status"),
        createdTime = DbValue.Long(row, "created_time"), updatedTime = DbValue.Long(row, "updated_time")
    };

    public static object SpeedLimit(IReadOnlyDictionary<string, object?> row) => new
    {
        id = DbValue.Long(row, "id"), name = DbValue.String(row, "name"), speed = DbValue.Int(row, "speed"),
        tunnelId = DbValue.Long(row, "tunnel_id"), tunnelName = DbValue.String(row, "tunnel_name"),
        status = DbValue.Int(row, "status"), createdTime = DbValue.Long(row, "created_time"), updatedTime = DbValue.Long(row, "updated_time")
    };

    public static object UserTunnel(IReadOnlyDictionary<string, object?> row) => new
    {
        id = DbValue.Int(row, "id"), userId = DbValue.Int(row, "user_id"), tunnelId = DbValue.Int(row, "tunnel_id"),
        tunnelName = DbValue.String(row, "tunnel_name"), tunnelFlow = DbValue.Int(row, "tunnel_flow"),
        flow = DbValue.Long(row, "flow"), inFlow = DbValue.Long(row, "in_flow"), outFlow = DbValue.Long(row, "out_flow"),
        num = DbValue.Int(row, "num"), flowResetTime = DbValue.Long(row, "flow_reset_time"),
        expTime = DbValue.Long(row, "exp_time"), speedId = DbValue.NullableInt(row, "speed_id"),
        speedLimitName = DbValue.String(row, "speed_name"), speed = DbValue.NullableInt(row, "speed"), status = DbValue.Int(row, "status")
    };

    public static bool TrySplitAddress(string address, out string host, out int port)
    {
        host = ""; port = 0;
        address = address.Trim();
        if (address.StartsWith('['))
        {
            var close = address.IndexOf(']');
            if (close > 1 && close + 2 <= address.Length && address[close + 1] == ':' && int.TryParse(address[(close + 2)..], out port))
            { host = address[1..close]; return port is > 0 and <= 65535; }
            return false;
        }
        var colon = address.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(address[(colon + 1)..], out port)) return false;
        host = address[..colon];
        return port is > 0 and <= 65535;
    }
}

public static class PortRangeRules
{
    public static bool TryParseOptional(string? value, int fallbackStart, int fallbackEnd, out string normalized, out List<(int Start, int End)> ranges, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = "";
            ranges = [];
            error = null;
            return true;
        }

        return TryParse(value, fallbackStart, fallbackEnd, out normalized, out ranges, out error);
    }

    public static bool TryParse(string? value, int fallbackStart, int fallbackEnd, out string normalized, out List<(int Start, int End)> ranges, out string? error)
    {
        var source = string.IsNullOrWhiteSpace(value) ? string.Concat(fallbackStart, "-", fallbackEnd) : value.Trim();
        ranges = [];
        var normalizedParts = new List<string>();

        foreach (var rawPart in source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = rawPart.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (bounds.Length is < 1 or > 2 || !int.TryParse(bounds[0], out var start))
            {
                normalized = "";
                error = string.Concat("invalid port range: ", rawPart);
                return false;
            }

            var end = start;
            if (bounds.Length == 2 && !int.TryParse(bounds[1], out end))
            {
                normalized = "";
                error = string.Concat("invalid port range: ", rawPart);
                return false;
            }
            if (start < 1 || end > 65535 || end < start)
            {
                normalized = "";
                error = string.Concat("invalid port range: ", rawPart);
                return false;
            }

            ranges.Add((start, end));
            normalizedParts.Add(start == end ? start.ToString() : string.Concat(start, "-", end));
        }

        if (ranges.Count == 0)
        {
            normalized = "";
            error = "at least one port is required";
            return false;
        }

        normalized = string.Join(",", normalizedParts);
        error = null;
        return true;
    }
}

public static class GostProtocol
{
    public static object[] Services(string name, int inPort, int tunnelType, string remoteAddr, string tcpAddr, string udpAddr, string strategy, string? limiter, string? interfaceName, string protocol, string? anyTlsPassword)
    {
        if (protocol == "anytls") return [Service(name, "tcp", inPort, tunnelType, remoteAddr, tcpAddr, strategy, limiter, interfaceName, protocol, anyTlsPassword)];
        return [Service(name, "tcp", inPort, tunnelType, remoteAddr, tcpAddr, strategy, limiter, interfaceName, protocol, anyTlsPassword), Service(name, "udp", inPort, tunnelType, remoteAddr, udpAddr, strategy, limiter, interfaceName, protocol, anyTlsPassword)];
    }

    public static object Service(string name, string transport, int port, int tunnelType, string remoteAddr, string listenAddr, string strategy, string? limiter, string? interfaceName, string protocol, string? anyTlsPassword)
    {
        var service = new Dictionary<string, object?>
        {
            ["name"] = $"{name}_{transport}", ["addr"] = $"{listenAddr}:{port}",
            ["handler"] = new Dictionary<string, object?> { ["type"] = transport, ["chain"] = tunnelType == 2 ? $"{name}_chains" : null },
            ["listener"] = transport == "udp" ? new Dictionary<string, object?> { ["type"] = transport, ["metadata"] = new { keepAlive = true } } : new { type = transport },
            ["limiter"] = limiter?.ToString(), ["forwarder"] = tunnelType == 1 ? Forwarder(remoteAddr, strategy) : null
        };
        if (!string.IsNullOrWhiteSpace(interfaceName)) service["metadata"] = new { @interface = interfaceName };
        return service;
    }

    public static object Forwarder(string remoteAddr, string strategy) => new
    {
        nodes = remoteAddr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select((addr, index) => new { name = $"node_{index + 1}", addr }).ToArray(),
        selector = new { strategy = string.IsNullOrWhiteSpace(strategy) ? "fifo" : strategy, maxFails = 1, failTimeout = "600s" }
    };

    public static object RemoteService(string name, int port, string remoteAddr, string protocol, string strategy, string? limiter, string? interfaceName, string? anyTlsPassword)
    {
        var anyTls = protocol == "anytls";
        var service = new Dictionary<string, object?>
        {
            ["name"] = $"{name}_tls", ["addr"] = $":{port}", ["handler"] = new { type = "relay" }, ["listener"] = anyTls ? new Dictionary<string, object?> { ["type"] = "anytls", ["metadata"] = new { password = anyTlsPassword } } : new { type = protocol }, ["limiter"] = limiter, ["forwarder"] = Forwarder(remoteAddr, strategy)
        };
        if (!string.IsNullOrWhiteSpace(interfaceName)) service["metadata"] = new { @interface = interfaceName };
        return service;
    }

    // The public node accepts BIND requests from an internal node. Each forward
    // receives a dedicated relay port so an internal node only ever dials out.
    public static object ReverseRelayService(string name, int port, string protocol, string? limiter, string? interfaceName, string? anyTlsPassword, string relayUsername, string relaySecret)
    {
        var anyTls = protocol == "anytls";
        var service = new Dictionary<string, object?>
        {
            ["name"] = $"{name}_relay", ["addr"] = $":{port}",
            ["handler"] = new { type = "relay", auth = new { username = relayUsername, password = relaySecret }, metadata = RelayMetadata(protocol, true) },
            ["listener"] = anyTls ? new Dictionary<string, object?> { ["type"] = "anytls", ["metadata"] = new { password = anyTlsPassword } } : new { type = protocol },
            ["limiter"] = limiter
        };
        if (!string.IsNullOrWhiteSpace(interfaceName)) service["metadata"] = new { @interface = interfaceName };
        return service;
    }

    public static object[] ReverseServices(string name, int port, string remoteAddr, string strategy, string protocol, string? limiter, string? interfaceName)
    {
        if (protocol != "quic") return [ReverseService(name, "tcp", port, remoteAddr, strategy, limiter, interfaceName)];
        return
        [
            ReverseService(name, "tcp", port, remoteAddr, strategy, limiter, interfaceName),
            ReverseService(name, "udp", port, remoteAddr, strategy, limiter, interfaceName)
        ];
    }

    private static object ReverseService(string name, string transport, int port, string remoteAddr, string strategy, string? limiter, string? interfaceName)
    {
        var reverseTransport = transport == "tcp" ? "rtcp" : "rudp";
        var service = new Dictionary<string, object?>
        {
            ["name"] = $"{name}_{transport}", ["addr"] = $":{port}",
            ["handler"] = new { type = reverseTransport },
            ["listener"] = new { type = reverseTransport, chain = $"{name}_chains" },
            ["limiter"] = limiter, ["forwarder"] = Forwarder(remoteAddr, strategy)
        };
        if (!string.IsNullOrWhiteSpace(interfaceName)) service["metadata"] = new { @interface = interfaceName };
        return service;
    }

    public static object Chain(string name, string remoteAddr, string protocol, string? interfaceName, string? anyTlsPassword, string? relayUsername = null, string? relaySecret = null)
    {
        var anyTls = protocol == "anytls";
        var connector = new Dictionary<string, object?> { ["type"] = "relay" };
        if (!string.IsNullOrWhiteSpace(relayUsername) && !string.IsNullOrWhiteSpace(relaySecret)) connector["auth"] = new { username = relayUsername, password = relaySecret };
        connector["metadata"] = RelayMetadata(protocol, false);
        var node = new Dictionary<string, object?> { ["name"] = $"node-{name}", ["addr"] = remoteAddr, ["connector"] = connector, ["dialer"] = anyTls ? new Dictionary<string, object?> { ["type"] = "anytls", ["metadata"] = new { password = anyTlsPassword } } : new { type = protocol } };
        if (!string.IsNullOrWhiteSpace(interfaceName)) node["interface"] = interfaceName;
        return new { name = $"{name}_chains", hops = new[] { new { name = $"hop-{name}", nodes = new[] { node } } } };
    }

    private static Dictionary<string, object> RelayMetadata(string protocol, bool bind)
    {
        var metadata = new Dictionary<string, object> { ["nodelay"] = true, ["mux.version"] = 2, ["mux.maxReceiveBuffer"] = 4194304, ["mux.maxStreamBuffer"] = 4194304 };
        if (bind) metadata["bind"] = true;
        if (protocol == "quic") metadata["quic.enableDatagram"] = true;
        return metadata;
    }
}
