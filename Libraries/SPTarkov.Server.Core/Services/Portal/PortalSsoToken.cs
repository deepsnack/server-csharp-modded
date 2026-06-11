using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.Services.Portal;

/// <summary>
/// 与 SptManagerPortal 互通的 SSO token：
/// 格式 = base64url(payloadJsonBytes) + "." + base64url(HMAC-SHA256(secret, payloadJsonBytes))
/// payload 含 aud（目标 modId）、exp（unix 秒）、jti（随机 GUID）。与其他子工具同款实现。
/// </summary>
public static class PortalSsoToken
{
    public record Payload
    {
        [JsonPropertyName("aud")] public string Aud { get; set; } = "";
        [JsonPropertyName("exp")] public long Exp { get; set; }
        [JsonPropertyName("jti")] public string Jti { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Payload? Verify(string token, string secretBase64, string expectedAudience)
    {
        try
        {
            var dotIdx = token.IndexOf('.');
            if (dotIdx <= 0 || dotIdx == token.Length - 1) return null;

            var payloadBytes = Base64UrlDecode(token[..dotIdx]);
            var sigBytes = Base64UrlDecode(token[(dotIdx + 1)..]);
            var secretBytes = Convert.FromBase64String(secretBase64);

            var expectedSig = HMACSHA256.HashData(secretBytes, payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(sigBytes, expectedSig)) return null;

            var payload = JsonSerializer.Deserialize<Payload>(payloadBytes, JsonOpt);
            if (payload is null) return null;
            if (!string.Equals(payload.Aud, expectedAudience, StringComparison.Ordinal)) return null;
            if (payload.Exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            if (string.IsNullOrEmpty(payload.Jti)) return null;

            return payload;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}

/// <summary>5 分钟窗口内跟踪已见过的 jti，拒绝重放。</summary>
public class JtiReplayGuard
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _seen = new();
    private readonly TimeSpan _retention = TimeSpan.FromMinutes(5);

    public bool TryAccept(string jti)
    {
        var now = DateTime.UtcNow;
        if (Random.Shared.Next(10) == 0)
        {
            foreach (var kv in _seen)
            {
                if (now - kv.Value > _retention) _seen.TryRemove(kv.Key, out _);
            }
        }
        return _seen.TryAdd(jti, now);
    }
}
