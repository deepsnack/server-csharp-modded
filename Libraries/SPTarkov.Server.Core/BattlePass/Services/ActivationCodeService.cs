using System.Security.Cryptography;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     激活码：管理员生成、玩家网页兑换。type=premium 解锁付费轨；type=levels 直升 value 级。
///     无外部支付依赖；premiumUnlocked 即"付费检测"标志。
/// </summary>
[Injectable]
public class ActivationCodeService(BattlePassService battlePassService, LotteryWalletService lotteryWalletService)
{
    private static readonly object Gate = new();
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 去掉易混字符

    /// <summary>批量生成激活码并落盘，返回新生成的码。</summary>
    public List<BpActivationCode> Generate(
        string type,
        int value,
        int count,
        string? batchTag,
        string? poolId = null,
        long expiresUtc = 0,
        int maxRedemptions = 1,
        bool perPlayerOnce = true,
        bool commonCode = false,
        List<BpReward>? rewards = null
    )
    {
        type = NormalizeType(type);
        count = Math.Clamp(count, 1, 1000);
        if (commonCode)
        {
            count = 1;
            maxRedemptions = Math.Max(1, maxRedemptions);
        }

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
                    PoolId = poolId,
                    ExpiresUtc = Math.Max(0, expiresUtc),
                    MaxRedemptions = Math.Max(1, maxRedemptions),
                    PerPlayerOnce = perPlayerOnce,
                    BatchTag = batchTag,
                    CreatedUtc = now,
                };
                if (type is "lotteryGlobalTickets" or "lotteryPoolTickets" or "lotteryExchangeCoins")
                {
                    entry.Value = Math.Max(1, value);
                }

                if (type == "rewards")
                {
                    // 每张码独立持有一份奖励列表副本，避免共享引用被后续修改牵连。
                    entry.Rewards = rewards is null ? new List<BpReward>() : new List<BpReward>(rewards);
                }

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

            NormalizeEntry(entry);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (entry.ExpiresUtc > 0 && now >= entry.ExpiresUtc)
            {
                return (false, "激活码已过期");
            }

            if (entry.RedeemCount >= entry.MaxRedemptions)
            {
                return (false, "激活码已被使用");
            }

            if (entry.PerPlayerOnce && entry.RedeemedProfileIds.Contains(profileId))
            {
                return (false, "你已兑换过该激活码");
            }

            var type = NormalizeType(entry.Type);
            if (type == "premium")
            {
                if (prog.PremiumUnlocked)
                {
                    return (false, "付费轨已解锁，无需再次兑换");
                }

                prog.PremiumUnlocked = true;
            }
            else if (type == "levels")
            {
                battlePassService.AddLevels(prog, season, entry.Value);
            }
            else if (type == "lotteryGlobalTickets")
            {
                lotteryWalletService.Grant(profileId, globalTickets: entry.Value);
            }
            else if (type == "lotteryPoolTickets")
            {
                if (string.IsNullOrWhiteSpace(entry.PoolId))
                {
                    return (false, "激活码缺少绑定奖池");
                }

                lotteryWalletService.Grant(profileId, poolId: entry.PoolId, poolTickets: entry.Value);
            }
            else if (type == "lotteryExchangeCoins")
            {
                lotteryWalletService.Grant(profileId, exchangeCoins: entry.Value);
            }
            else if (type == "rewards")
            {
                if (entry.Rewards is not { Count: > 0 })
                {
                    return (false, "激活码未配置任何奖励");
                }

                // 统一奖励发放：物品(含任务跳过券)/称号/配方/服装/购买权/抽奖资源，与等级奖励/任务同一条链路。
                battlePassService.GrantRewards(profileId, prog, entry.Rewards, "激活码奖励");
            }

            entry.RedeemedBy ??= profileId;
            entry.RedeemedUtc = now;
            entry.RedeemCount++;
            entry.RedeemedProfileIds.Add(profileId);
            BattlePassStore.SaveCodes(all);

            return (true, MessageFor(type, entry));
        }
    }

    private static string NormalizeType(string? type)
    {
        return type?.Trim().ToLowerInvariant() switch
        {
            "levels" => "levels",
            "lotteryglobaltickets" => "lotteryGlobalTickets",
            "lotterypooltickets" => "lotteryPoolTickets",
            "lotteryexchangecoins" => "lotteryExchangeCoins",
            "rewards" => "rewards",
            _ => "premium",
        };
    }

    private static void NormalizeEntry(BpActivationCode entry)
    {
        entry.MaxRedemptions = entry.MaxRedemptions <= 0 ? 1 : entry.MaxRedemptions;
        entry.RedeemedProfileIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(entry.RedeemedBy))
        {
            entry.RedeemedProfileIds.Add(entry.RedeemedBy);
            entry.RedeemCount = Math.Max(entry.RedeemCount, 1);
        }
    }

    private static string MessageFor(string type, BpActivationCode entry)
    {
        return type switch
        {
            "levels" => $"已直升 {entry.Value} 级",
            "lotteryGlobalTickets" => $"已获得 {entry.Value} 张通用抽奖券",
            "lotteryPoolTickets" => $"已获得 {entry.Value} 张限定抽奖券",
            "lotteryExchangeCoins" => $"已获得 {entry.Value} 枚兑换币",
            "rewards" => "激活码奖励已发放，请查收游戏内邮件 / 通行证",
            _ => "付费轨已解锁",
        };
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
