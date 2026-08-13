using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>抽奖模块钱包服务：通用抽奖券、奖池限定券、兑换币。</summary>
[Injectable(InjectionType.Singleton)]
public class LotteryWalletService
{
    private static readonly object WalletGate = new();

    public BpLotteryWallet GetWallet(string profileId)
    {
        lock (WalletGate)
        {
            return BattlePassStore.GetLotteryWallet(profileId);
        }
    }

    public BpLotteryWallet Grant(
        string profileId,
        int globalTickets = 0,
        string? poolId = null,
        int poolTickets = 0,
        int exchangeCoins = 0
    )
    {
        lock (WalletGate)
        {
            var wallet = BattlePassStore.GetLotteryWallet(profileId);
            wallet.GlobalTickets = Math.Max(0, wallet.GlobalTickets + Math.Max(0, globalTickets));
            wallet.ExchangeCoins = Math.Max(0, wallet.ExchangeCoins + Math.Max(0, exchangeCoins));

            if (!string.IsNullOrWhiteSpace(poolId) && poolTickets > 0)
            {
                var key = poolId.Trim();
                wallet.PoolTickets[key] = Math.Max(0, wallet.PoolTickets.GetValueOrDefault(key) + poolTickets);
            }

            BattlePassStore.SaveLotteryWallet(profileId, wallet);
            return wallet;
        }
    }

    public (bool ok, string message, BpLotteryCostSnapshot snapshot) TryDeductTickets(
        string profileId,
        string poolId,
        int amount
    )
    {
        lock (WalletGate)
        {
            var snapshot = new BpLotteryCostSnapshot { CostType = BpLotteryConstants.CostLotteryTickets };
            if (amount <= 0)
            {
                return (false, "抽奖券代价配置无效", snapshot);
            }

            var wallet = BattlePassStore.GetLotteryWallet(profileId);
            var poolHave = wallet.PoolTickets.GetValueOrDefault(poolId);
            var fromPool = Math.Min(poolHave, amount);
            var fromGlobal = amount - fromPool;
            if (wallet.GlobalTickets < fromGlobal)
            {
                return (false, $"抽奖券不足：需要 {amount}，限定券 {poolHave}，通用券 {wallet.GlobalTickets}", snapshot);
            }

            if (fromPool > 0)
            {
                wallet.PoolTickets[poolId] = poolHave - fromPool;
            }

            wallet.GlobalTickets -= fromGlobal;
            snapshot.PoolTickets = fromPool;
            snapshot.GlobalTickets = fromGlobal;
            BattlePassStore.SaveLotteryWallet(profileId, wallet);
            return (true, "", snapshot);
        }
    }

    public (bool ok, string message) TryDeductExchangeCoins(string profileId, int amount)
    {
        lock (WalletGate)
        {
            if (amount <= 0)
            {
                return (false, "兑换币价格配置无效");
            }

            var wallet = BattlePassStore.GetLotteryWallet(profileId);
            if (wallet.ExchangeCoins < amount)
            {
                return (false, $"兑换币不足：需要 {amount}，当前 {wallet.ExchangeCoins}");
            }

            wallet.ExchangeCoins -= amount;
            BattlePassStore.SaveLotteryWallet(profileId, wallet);
            return (true, "");
        }
    }
}
