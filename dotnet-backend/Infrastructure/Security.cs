using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelayForge.Panel.Api;

public sealed class PasswordService
{
    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"PBKDF2$SHA256$120000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string stored)
    {
        if (stored.StartsWith("PBKDF2$", StringComparison.Ordinal))
        {
            var parts = stored.Split('$');
            if (parts.Length != 5 || !int.TryParse(parts[2], out var iterations)) return false;
            try
            {
                var salt = Convert.FromBase64String(parts[3]);
                var expected = Convert.FromBase64String(parts[4]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException) { return false; }
        }

        if (stored.Length == 32 && stored.All(Uri.IsHexDigit))
        {
            using var md5 = MD5.Create();
            var digest = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(digest), Encoding.UTF8.GetBytes(stored.ToLowerInvariant()));
        }
        return false;
    }
}

public sealed class TotpService
{
    private const int StepSeconds = 30;
    private const int Digits = 6;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var output = new StringBuilder(32);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0) output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    public bool Verify(string? code, string? secret, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(secret)) return false;
        var normalizedCode = new string(code.Where(char.IsDigit).ToArray());
        if (normalizedCode.Length != Digits) return false;
        byte[] key;
        try { key = DecodeSecret(secret); }
        catch (FormatException) { return false; }
        var counter = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / StepSeconds;
        for (var offset = -1; offset <= 1; offset++)
        {
            var expected = GenerateCode(key, counter + offset);
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(normalizedCode))) return true;
        }
        return false;
    }

    public bool IsValidSecret(string? secret)
    {
        try { return DecodeSecret(secret ?? "").Length >= 16; }
        catch (FormatException) { return false; }
    }

    public string BuildUri(string secret, string account)
    {
        var issuer = Uri.EscapeDataString("RelayForge");
        var label = Uri.EscapeDataString($"RelayForge:{account}");
        return $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    private static string GenerateCode(byte[] key, long counter)
    {
        Span<byte> bytes = stackalloc byte[8];
        for (var index = bytes.Length - 1; index >= 0; index--)
        {
            bytes[index] = (byte)(counter & 0xff);
            counter >>= 8;
        }
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(bytes.ToArray());
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] DecodeSecret(string secret)
    {
        var normalized = secret.Trim().Replace(" ", "", StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        if (normalized.Length < 16 || normalized.Any(value => !Alphabet.Contains(value))) throw new FormatException("Invalid TOTP secret");
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var value in normalized)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(value);
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)(buffer >> bits));
        }
        return bytes.ToArray();
    }
}

public sealed class TokenService(IConfiguration configuration)
{
    private readonly byte[] _secret = ReadSecret(configuration);
    private readonly string _issuer = configuration["Jwt:Issuer"] ?? "relayforge";
    private readonly string _audience = configuration["Jwt:Audience"] ?? "relayforge-panel";
    private readonly int _lifetimeMinutes = ReadLifetime(configuration);

    public string Create(AuthUser user)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = _issuer,
            aud = _audience,
            sub = user.Id,
            user = user.Name,
            name = user.Name,
            role_id = user.RoleId,
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            exp = DateTimeOffset.UtcNow.AddMinutes(_lifetimeMinutes).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N")
        }));
        return $"{header}.{payload}.{Sign($"{header}.{payload}")}";
    }

    public bool TryValidate(string? token, out AuthUser? user)
    {
        user = null;
        if (token?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true) token = token[7..];
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 3 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign($"{parts[0]}.{parts[1]}")), Encoding.UTF8.GetBytes(parts[2]))) return false;
        try
        {
            using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            if (!header.RootElement.TryGetProperty("alg", out var algorithm) || algorithm.GetString() != "HS256") return false;

            using var json = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var root = json.RootElement;
            if (!root.TryGetProperty("iss", out var issuer) || issuer.GetString() != _issuer) return false;
            if (!root.TryGetProperty("aud", out var audience) || audience.GetString() != _audience) return false;
            if (!root.TryGetProperty("exp", out var exp) || exp.GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
            user = new AuthUser(root.GetProperty("sub").GetInt64(), root.GetProperty("role_id").GetInt32(), root.GetProperty("name").GetString() ?? "");
            return true;
        }
        catch (Exception) { return false; }
    }

    private static byte[] ReadSecret(IConfiguration configuration)
    {
        var value = configuration["JWT_SECRET"];
        if (string.IsNullOrWhiteSpace(value) || value is "change-me-in-production" or "replace-with-a-long-random-secret")
            throw new InvalidOperationException("JWT_SECRET must be configured with at least 32 random bytes.");
        var secret = Encoding.UTF8.GetBytes(value);
        if (secret.Length < 32) throw new InvalidOperationException("JWT_SECRET must be at least 32 bytes.");
        return secret;
    }

    private static int ReadLifetime(IConfiguration configuration)
    {
        var minutes = int.TryParse(configuration["Jwt:LifetimeMinutes"], out var configured) ? configured : 120;
        return Math.Clamp(minutes, 5, 1440);
    }

    private string Sign(string value)
    {
        using var hmac = new HMACSHA256(_secret);
        return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}

public sealed class AesCrypto
{
    private readonly byte[] _key;
    public AesCrypto(string secret) => _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    public string Encrypt(string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Decrypt(string value)
    {
        var input = Convert.FromBase64String(value);
        if (input.Length < 28) throw new CryptographicException("Invalid encrypted payload");
        var nonce = input[..12];
        var tag = input[^16..];
        var cipher = input[12..^16];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
