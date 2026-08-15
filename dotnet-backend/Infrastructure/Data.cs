using MySqlConnector;

namespace RelayForge.Panel.Api;

public sealed class Db(IConfiguration configuration)
{
    private readonly string _connectionString = BuildConnectionString(configuration);

    private static string BuildConnectionString(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("MySql");
        if (!string.IsNullOrWhiteSpace(configured) && !configured.Contains("${"))
            return configured;

        var host = Required(configuration, "DB_HOST");
        var database = Required(configuration, "DB_NAME");
        var user = Required(configuration, "DB_USER");
        var password = Required(configuration, "DB_PASSWORD");
        var sslMode = configuration["DB_SSL_MODE"] ?? "Preferred";
        return $"Server={host};Port=3306;Database={database};User ID={user};Password={password};SslMode={sslMode};Allow User Variables=True;";
    }

    private static string Required(IConfiguration configuration, string key) => configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{key} is required.");

    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result.Add(row);
        }
        return result;
    }

    public async Task<int> ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> InsertAndGetIdAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var idCommand = new MySqlCommand("SELECT LAST_INSERT_ID()", connection);
        var value = await idCommand.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    public async Task<object?> ScalarAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }

    private static MySqlCommand CreateCommand(MySqlConnection connection, string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        var command = new MySqlCommand(sql, connection);
        if (parameters is null) return command;
        foreach (var pair in parameters)
            command.Parameters.AddWithValue(pair.Key.StartsWith('@') ? pair.Key : $"@{pair.Key}", pair.Value ?? DBNull.Value);
        return command;
    }
}
