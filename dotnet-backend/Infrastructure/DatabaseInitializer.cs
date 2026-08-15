namespace RelayForge.Panel.Api;

public sealed class DatabaseInitializer(Db db, PasswordService passwords)
{
    public async Task InitializeAsync(IConfiguration configuration)
    {
        await EnsureDatabaseSchemaAsync(configuration);
        await TryMigrationAsync("node", "ALTER TABLE `node` ADD COLUMN `port_range` varchar(255) DEFAULT NULL");
        await TryMigrationAsync("forward-flow", "ALTER TABLE `forward` ADD COLUMN `flow` bigint(20) NOT NULL DEFAULT '0' AFTER `interface_name`");
        await TryMigrationAsync("forward-relay", "ALTER TABLE `forward` ADD COLUMN `relay_secret` varchar(100) DEFAULT NULL AFTER `interface_name`");
        await TryMigrationAsync("forward-xui", "ALTER TABLE `forward` ADD COLUMN `xui_inbound_id` bigint(20) DEFAULT NULL AFTER `tunnel_id`");
        await TryMigrationAsync("xui-2fa", "ALTER TABLE `xui_connection` ADD COLUMN `two_factor_code_cipher` longtext NULL AFTER `password_cipher`");
        await TryMigrationAsync("user-2fa", "ALTER TABLE `user` ADD COLUMN `totp_enabled` tinyint(1) NOT NULL DEFAULT '0' AFTER `status`");
        await TryMigrationAsync("user-2fa-secret", "ALTER TABLE `user` ADD COLUMN `totp_secret_cipher` longtext NULL AFTER `totp_enabled`");
        await TryMigrationAsync("forward-relay-secret-backfill", "UPDATE `forward` f JOIN `tunnel` t ON t.id=f.tunnel_id SET f.relay_secret=REPLACE(UUID(),'-','') WHERE t.type=3 AND (f.relay_secret IS NULL OR f.relay_secret='')");
        await TryMigrationAsync("tunnel-quota", "ALTER TABLE `tunnel` ADD COLUMN `flow_limit_gb` bigint(20) NOT NULL DEFAULT '0' AFTER `flow`");
        await TryMigrationAsync("tunnel-quota-backfill", "UPDATE `tunnel` SET `flow_limit_gb`=`flow` WHERE `flow` NOT IN (0,1,2) AND `flow_limit_gb`=0");
        await TryMigrationAsync("tunnel-flow-normalize", "UPDATE `tunnel` SET `flow`=2 WHERE `flow` NOT IN (1,2) OR `flow` IS NULL");
        try { await EnsureDefaultConfigsAsync(); }
        catch { }
    }

    private async Task TryMigrationAsync(string name, string sql)
    {
        try { await db.ExecuteAsync(sql); }
        catch { /* Compatibility migrations are intentionally idempotent. */ }
    }

    private async Task EnsureDefaultConfigsAsync()
    {
        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS `vite_config` (
                `id` int(10) NOT NULL AUTO_INCREMENT,
                `name` varchar(200) NOT NULL,
                `value` varchar(1000) NOT NULL,
                `time` bigint(20) NOT NULL,
                PRIMARY KEY (`id`),
                UNIQUE KEY `unique_name` (`name`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """);

        try { await db.ExecuteAsync("ALTER TABLE vite_config MODIFY value varchar(1000) NOT NULL"); }
        catch { }

        foreach (var (name, value) in new[]
        {
            ("app_name", "RelayForge"),
            ("panel_host", ""),
            ("frontend_port", "6311"),
            ("backend_port", "6315"),
            ("panel_secure", "0"),
            ("secure_port", "443"),
            ("telegram_enabled", "0"),
            ("telegram_bot_token", ""),
            ("telegram_chat_id", ""),
            ("telegram_traffic_threshold", "80"),
            ("telegram_notify_flow", "1"),
            ("telegram_notify_node", "1")
        })
        {
            // Defaults are inserted only when missing; saved settings survive restarts and upgrades.
            await db.ExecuteAsync(
                "INSERT INTO vite_config (name,value,time) VALUES (@name,@value,@time) ON DUPLICATE KEY UPDATE name=VALUES(name)",
                Domain.Params(("name", name), ("value", value), ("time", Domain.Now())));
        }
    }

    private async Task EnsureDatabaseSchemaAsync(IConfiguration configuration)
    {
        // The schema is applied by the backend image so installation does not require a public SQL asset.
    var statements = new[]
    {
        """
        CREATE TABLE IF NOT EXISTS `forward` (
            `id` int(10) NOT NULL AUTO_INCREMENT,
            `user_id` int(10) NOT NULL,
            `user_name` varchar(100) NOT NULL,
            `name` varchar(100) NOT NULL,
            `tunnel_id` int(10) NOT NULL,
            `xui_inbound_id` bigint(20) DEFAULT NULL,
            `in_port` int(10) NOT NULL,
            `out_port` int(10) DEFAULT NULL,
            `remote_addr` longtext NOT NULL,
            `strategy` varchar(100) NOT NULL DEFAULT 'fifo',
            `interface_name` varchar(200) DEFAULT NULL,
            `relay_secret` varchar(100) DEFAULT NULL,
            `flow` bigint(20) NOT NULL DEFAULT '0',
            `in_flow` bigint(20) NOT NULL DEFAULT '0',
            `out_flow` bigint(20) NOT NULL DEFAULT '0',
            `created_time` bigint(20) NOT NULL,
            `updated_time` bigint(20) NOT NULL,
            `status` int(10) NOT NULL,
            `inx` int(10) NOT NULL DEFAULT '0',
            PRIMARY KEY (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """,
        """
        CREATE TABLE IF NOT EXISTS `xui_connection` (
            `id` bigint(20) NOT NULL AUTO_INCREMENT,
            `user_id` bigint(20) NOT NULL,
            `name` varchar(100) NOT NULL,
            `panel_url` varchar(500) NOT NULL,
            `connect_host` varchar(255) NOT NULL,
            `api_token_cipher` longtext,
            `username_cipher` longtext,
            `password_cipher` longtext,
            `two_factor_code_cipher` longtext,
            `verify_tls` int(10) NOT NULL DEFAULT '1',
            `status` int(10) NOT NULL DEFAULT '0',
            `last_sync_time` bigint(20) NOT NULL DEFAULT '0',
            `last_error` varchar(500) DEFAULT NULL,
            `created_time` bigint(20) NOT NULL,
            `updated_time` bigint(20) NOT NULL,
            PRIMARY KEY (`id`),
            KEY `idx_xui_connection_user` (`user_id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """,
        """
        CREATE TABLE IF NOT EXISTS `xui_inbound` (
            `id` bigint(20) NOT NULL AUTO_INCREMENT,
            `connection_id` bigint(20) NOT NULL,
            `external_id` varchar(100) NOT NULL,
            `name` varchar(200) NOT NULL,
            `tag` varchar(200) DEFAULT NULL,
            `protocol` varchar(50) NOT NULL DEFAULT 'unknown',
            `port` int(10) NOT NULL,
            `listen` varchar(255) DEFAULT NULL,
            `remote_addr` varchar(300) NOT NULL,
            `enabled` int(10) NOT NULL DEFAULT '1',
            `last_seen_time` bigint(20) NOT NULL DEFAULT '0',
            `updated_time` bigint(20) NOT NULL,
            PRIMARY KEY (`id`),
            UNIQUE KEY `uk_xui_inbound_external` (`connection_id`,`external_id`),
            KEY `idx_xui_inbound_connection` (`connection_id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """,
        """
        CREATE TABLE IF NOT EXISTS `node` (
            `id` int(10) NOT NULL AUTO_INCREMENT,
            `name` varchar(100) NOT NULL,
            `secret` varchar(100) NOT NULL,
            `ip` longtext,
            `server_ip` varchar(100) NOT NULL,
            `port_sta` int(10) NOT NULL,
            `port_end` int(10) NOT NULL,
            `port_range` varchar(255) DEFAULT NULL,
            `version` varchar(100) DEFAULT NULL,
            `http` int(10) NOT NULL DEFAULT '0',
            `tls` int(10) NOT NULL DEFAULT '0',
            `socks` int(10) NOT NULL DEFAULT '0',
            `created_time` bigint(20) NOT NULL,
            `updated_time` bigint(20) DEFAULT NULL,
            `status` int(10) NOT NULL,
            PRIMARY KEY (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """,
        """
        CREATE TABLE IF NOT EXISTS `speed_limit` (
            `id` int(10) NOT NULL AUTO_INCREMENT,
            `name` varchar(100) NOT NULL,
            `speed` int(10) NOT NULL,
            `tunnel_id` int(10) NOT NULL,
            `tunnel_name` varchar(100) NOT NULL,
            `created_time` bigint(20) NOT NULL,
            `updated_time` bigint(20) DEFAULT NULL,
            `status` int(10) NOT NULL,
            PRIMARY KEY (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """,
        """
        CREATE TABLE IF NOT EXISTS `statistics_flow` (
            `id` int(10) NOT NULL AUTO_INCREMENT,
            `user_id` int(10) NOT NULL,
            `flow` bigint(20) NOT NULL,
            `total_flow` bigint(20) NOT NULL,
            `time` varchar(100) NOT NULL,
            `created_time` bigint(20) NOT NULL,
            PRIMARY KEY (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """,
        """
        CREATE TABLE IF NOT EXISTS `telegram_notification_state` (
            `event_key` varchar(200) NOT NULL,
            `sent_time` bigint(20) NOT NULL,
            PRIMARY KEY (`event_key`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """,
        """
        CREATE TABLE IF NOT EXISTS `tunnel` (
            `id` int(10) NOT NULL AUTO_INCREMENT,
            `name` varchar(100) NOT NULL,
            `traffic_ratio` decimal(10,1) NOT NULL DEFAULT '1.0',
            `speed_limit_kbps` int(10) NOT NULL DEFAULT '0',
            `in_node_id` int(10) NOT NULL,
            `in_ip` varchar(100) NOT NULL,
            `out_node_id` int(10) NOT NULL,
            `out_ip` varchar(100) NOT NULL,
            `type` int(10) NOT NULL,
            `protocol` varchar(10) NOT NULL DEFAULT 'tls',
            `anytls_password` varchar(255) DEFAULT NULL,
            `flow` int(10) NOT NULL DEFAULT '2',
            `flow_limit_gb` bigint(20) NOT NULL DEFAULT '0',
            `tcp_listen_addr` varchar(100) NOT NULL DEFAULT '[::]',
            `udp_listen_addr` varchar(100) NOT NULL DEFAULT '[::]',
            `interface_name` varchar(200) DEFAULT NULL,
            `created_time` bigint(20) NOT NULL,
            `updated_time` bigint(20) NOT NULL,
            `status` int(10) NOT NULL,
            PRIMARY KEY (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """,
        """
        CREATE TABLE IF NOT EXISTS `user` (
            `id` int(10) NOT NULL AUTO_INCREMENT,
            `user` varchar(100) NOT NULL,
            `pwd` varchar(100) NOT NULL,
            `role_id` int(10) NOT NULL,
            `exp_time` bigint(20) NOT NULL,
            `flow` bigint(20) NOT NULL,
            `in_flow` bigint(20) NOT NULL DEFAULT '0',
            `out_flow` bigint(20) NOT NULL DEFAULT '0',
            `flow_reset_time` bigint(20) NOT NULL,
            `num` int(10) NOT NULL,
            `created_time` bigint(20) NOT NULL,
            `updated_time` bigint(20) DEFAULT NULL,
            `status` int(10) NOT NULL,
            `totp_enabled` tinyint(1) NOT NULL DEFAULT '0',
            `totp_secret_cipher` longtext DEFAULT NULL,
            PRIMARY KEY (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """,
        """
        CREATE TABLE IF NOT EXISTS `user_tunnel` (
            `id` int(10) NOT NULL AUTO_INCREMENT,
            `user_id` int(10) NOT NULL,
            `tunnel_id` int(10) NOT NULL,
            `speed_id` int(10) DEFAULT NULL,
            `num` int(10) NOT NULL,
            `flow` bigint(20) NOT NULL,
            `in_flow` bigint(20) NOT NULL DEFAULT '0',
            `out_flow` bigint(20) NOT NULL DEFAULT '0',
            `flow_reset_time` bigint(20) NOT NULL,
            `exp_time` bigint(20) NOT NULL,
            `status` int(10) NOT NULL,
            PRIMARY KEY (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """
    };

        foreach (var statement in statements) await db.ExecuteAsync(statement);

        var adminCount = Convert.ToInt64(await db.ScalarAsync("SELECT COUNT(*) FROM `user` WHERE role_id=0"));
        if (adminCount > 0) return;

        var username = configuration["INITIAL_ADMIN_USERNAME"];
        var passwordBase64 = configuration["INITIAL_ADMIN_PASSWORD_B64"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(passwordBase64) || !username.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-'))
            throw new InvalidOperationException("An initial administrator is required for an empty database.");

        string password;
        try
        {
            password = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(passwordBase64));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("The initial administrator password is invalid.", ex);
        }

        if (password.Length < 8) throw new InvalidOperationException("The initial administrator password is invalid.");
        var now = Domain.Now();
        await db.ExecuteAsync(
            "INSERT INTO `user` (`user`,pwd,role_id,exp_time,flow,in_flow,out_flow,flow_reset_time,num,created_time,updated_time,status) VALUES (@user,@pwd,0,@exp,@flow,0,0,1,99999,@now,@now,1)",
            Domain.Params(("user", username), ("pwd", passwords.Hash(password)), ("exp", 2727251700000L), ("flow", 99999L), ("now", now)));
    }
}
