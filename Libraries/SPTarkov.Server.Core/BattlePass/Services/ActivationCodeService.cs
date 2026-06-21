using System.Security.Cryptography;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     激活码：管理员生成、玩家网页兑换。type=premium 解锁付费轨；type=levels 直升 value 级。
///     无外部支付依赖；premiumUnlocked 即"付费检测"标志。
/// </summary>
[Injectable]
public class ActivationCodeService(BattlePassService battlePassService)
{
    private static readonly object Gate = new();
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 去掉易混字符

    /// <summary>批量生成激活码并落盘，返回新生成的码。</summary>
    public List<BpActivationCode> Generate(string type, int value, int count, string? batchTag)
    {
        type = string.Equals(type, "levels", StringComparison.OrdinalIgnoreCase) ? "levels" : "premium";
        count = Math.Clamp(count, 1, 1000);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (Gate)
        {
            var all = BattlePassStore.GetCodes();
            var existing = all.Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var created = new List<BpActivationCode>();

            for (var i = 0; i < count; i++)
            {
                string code;
                do
                {
                    code = NewCode();
                } while (!existing.Add(code));

                var entry = new BpActivationCode
                {
                    Code = code,
                    Type = type,
                    Value = type == "levels" ? Math.Max(1, value) : 0,
                    BatchTag = batchTag,
                    CreatedUtc = now,
                };
                created.Add(entry);
                all.Add(entry);
            }

            BattlePassStore.SaveCodes(all);
            return created;
        }
    }

    /// <summary>兑换激活码，作用于传入的进度对象（调用方负责保存进度）。返回 (成功, 消息)。</summary>
    public (bool ok, string message) Redeem(string profileId, BpProgress prog, BpSeason season, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (false, "激活码为空");
        }

        code = code.Trim();

        lock (Gate)
        {
            var all = BattlePassStore.GetCodes();
            var entry = all.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return (false, "激活码无效");
            }

            if (!string.IsNullOrEmpty(entry.RedeemedBy))
            {
                return (false, "激活码已被使用");
            }

            if (entry.Type == "premium")
            {
                if (prog.PremiumUnlocked)
                {
                    return (false, "付费轨已解锁，无需再次兑换");
                }

                prog.PremiumUnlocked = true;
            }
            else
            {
                battlePassService.AddLevels(prog, season, entry.Value);
            }

            entry.RedeemedBy = profileId;
            entry.RedeemedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            BattlePassStore.SaveCodes(all);

            return (true, entry.Type == "premium" ? "付费轨已解锁" : $"已直升 {entry.Value} 级");
        }
    }

    private static string NewCode()
    {
        Span<byte> buf = stackalloc byte[16];
        RandomNumberGenerator.Fill(buf);
        var chars = new char[16];
        for (var i = 0; i < 16; i++)
        {
            chars[i] = Alphabet[buf[i] % Alphabet.Length];
        }

        // 形如 XXXX-XXXX-XXXX-XXXX
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}-{new string(chars, 12, 4)}";
    }
}
