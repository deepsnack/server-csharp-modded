namespace SPTarkov.Server.Core.BattlePass;

/// <summary>首次启动写入的示例数据（赛季 / 双轨奖励 / 任务），服主可在管理页或 JSON 中改。</summary>
public static class BattlePassDefaults
{
    // 常用 tpl
    private const string Roubles = "5449016a4bdc2d6f028b456f";
    private const string Dollars = "5696686a4bdc2da3298b456a";
    private const string Bitcoin = "59faff1d86f7746c51718c9c";
    private const string GpCoin = "5d235b4d86f7742e017bc88a";
    private const string Salewa = "544fb45d4bdc2dee738b4568";
    private const string GoldChain = "5734781f24597737e04bf329";

    public static BpSeason Season()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new BpSeason
        {
            SeasonId = "S1",
            Name = "赛季一 · 序章",
            StartUtc = now,
            EndUtc = now + 70L * 24 * 3600, // 10 周
            MaxLevel = 50,
            BaseXp = 1000,
            XpGrowthPerLevel = 100,
            PremiumXpMultiplier = 1.25,
        };
    }

    /// <summary>
    ///     示例双轨奖励：每级免费轨给少量卢布，付费轨给更多；
    ///     每 10 级核心大奖（featured），满级终极奖。仅作占位，服主自行替换。
    /// </summary>
    public static Dictionary<int, BpLevelRewards> Tracks()
    {
        var tracks = new Dictionary<int, BpLevelRewards>();
        for (var lvl = 1; lvl <= 50; lvl++)
        {
            var milestone = lvl % 10 == 0;
            var free = new List<BpReward> { new() { Tpl = Roubles, Count = 20000 * lvl, Name = "卢布" } };
            var premium = new List<BpReward> { new() { Tpl = Roubles, Count = 50000 * lvl, Name = "卢布" } };

            if (milestone)
            {
                free.Add(new BpReward { Tpl = Salewa, Count = 1, Name = "Salewa 急救包" });
                premium.Add(
                    new BpReward
                    {
                        Tpl = lvl >= 50 ? Bitcoin : GpCoin,
                        Count = lvl >= 50 ? 5 : 3,
                        Name = lvl >= 50 ? "比特币" : "GP 币",
                        Featured = true,
                    }
                );
            }

            tracks[lvl] = new BpLevelRewards { Free = free, Premium = premium };
        }

        return tracks;
    }

    /// <summary>默认商人元信息（服主可在管理页改）。</summary>
    public static BpTraderConfig TraderConfig()
    {
        return new BpTraderConfig
        {
            Name = "通行证商人",
            Nickname = "Pass",
            Surname = "Quartermaster",
            Description = "凭通行证解锁的购买权限，在此兑现你的专属补给。",
            Location = "通行证",
            Currency = "RUB",
            UnlockedByDefault = true,
            ResupplySeconds = 3600,
            InsuranceAvailable = false,
            RepairAvailable = false,
        };
    }

    /// <summary>
    ///     示例货架：两件「购买权限」商品（默认用卢布换购），与 tracks 里 purchaseRight 奖励的 offerId 对应。
    ///     仅占位，服主自行替换。
    /// </summary>
    public static List<BpTraderOffer> Offers()
    {
        return new List<BpTraderOffer>
        {
            new()
            {
                Id = "offer_salewa",
                Tpl = Salewa,
                Name = "Salewa 急救包（购买权限）",
                Stock = -1,
                BuyLimit = 5,
                Cost = new List<BpBarterCost> { new() { Tpl = Roubles, Count = 25000 } },
            },
            new()
            {
                Id = "offer_goldchain",
                Tpl = GoldChain,
                Name = "金链子（购买权限）",
                Stock = -1,
                BuyLimit = 0,
                Cost = new List<BpBarterCost> { new() { Tpl = GpCoin, Count = 2 } },
            },
        };
    }

    /// <summary>
    ///     示例称号目录：两个文字称号（一个金色单色、一个双色渐变），便于直接预览效果。
    ///     图片称号需上传文件，故不种子。服主自行增删改。
    /// </summary>
    public static List<BpTitle> TitleCatalog()
    {
        return new List<BpTitle>
        {
            new()
            {
                Id = "veteran",
                Name = "百战精英",
                Type = "text",
                Text = "百战精英",
                Color = "#E8B923",
                Description = "通行证示例称号（金色单色）。",
            },
            new()
            {
                Id = "champion",
                Name = "赛季冠军",
                Type = "text",
                Text = "赛季冠军",
                Color = "#FF7A18",
                ColorEnd = "#AF002D",
                Description = "通行证示例称号（双色线性渐变）。",
            },
        };
    }

    public static List<BpTaskTemplate> Tasks()
    {
        return new List<BpTaskTemplate>
        {
            // ---- Kills ----
            new()
            {
                Id = "daily_kill_scav_customs",
                Scope = "daily",
                Title = "海关清扫",
                Description = "在海关击杀 3 名 Scav。",
                Xp = 500,
                Rotation = "random",
                Weight = 2,
                ConditionType = "Kills",
                Target = "Savage",
                Count = 3,
                Location = "bigmap",
            },
            new()
            {
                Id = "daily_kill_any",
                Scope = "daily",
                Title = "战场生还",
                Description = "击杀任意 5 名敌人。",
                Xp = 400,
                Rotation = "random",
                Weight = 3,
                ConditionType = "Kills",
                Target = "Any",
                Count = 5,
                Location = null,
            },
            new()
            {
                Id = "weekly_kill_pmc",
                Scope = "weekly",
                Title = "猎杀 PMC",
                Description = "击杀 10 名 PMC。",
                Xp = 1500,
                Rotation = "random",
                Weight = 1,
                ConditionType = "Kills",
                Target = "AnyPmc",
                Count = 10,
                Location = null,
            },
            // ---- Exploration ----
            new()
            {
                Id = "season_survive",
                Scope = "season",
                Title = "老兵不死",
                Description = "成功撤离 25 次。",
                Xp = 5000,
                Rotation = "fixed",
                Weight = 1,
                ConditionType = "Exploration",
                Target = "Any",
                Count = 25,
                Location = null,
            },
            // ---- HandoverItem ----
            new()
            {
                Id = "daily_handover_salewa",
                Scope = "daily",
                Title = "战地急救",
                Description = "上交 2 个 Salewa 急救包。",
                Xp = 600,
                Rotation = "random",
                Weight = 2,
                ConditionType = "HandoverItem",
                Count = 2,
                ItemRequirements = new List<BpTaskItemRequirement>
                {
                    new() { Tpl = "544fb45d4bdc2dee738b4568", Count = 2, Name = "Salewa 急救包" },
                },
                FindInRaid = true,
            },
            // ---- FindItem ----
            new()
            {
                Id = "daily_find_goldchain",
                Scope = "daily",
                Title = "淘金热",
                Description = "在战局中找到 1 条金链子。",
                Xp = 550,
                Rotation = "random",
                Weight = 1,
                ConditionType = "FindItem",
                Count = 1,
                ItemRequirements = new List<BpTaskItemRequirement>
                {
                    new() { Tpl = "5734781f24597737e04bf329", Count = 1, Name = "金链子" },
                },
                FindInRaid = true,
            },
            // ---- PlaceItem ----
            new()
            {
                Id = "weekly_place_beacon_factory",
                Scope = "weekly",
                Title = "工厂信号",
                Description = "在工厂 3 楼办公室安放 1 个无线电信标。",
                Xp = 2000,
                Rotation = "random",
                Weight = 1,
                ConditionType = "PlaceItem",
                Count = 1,
                Location = "factory4_day",
                ZoneId = "BotZone",
                PlantTime = 5,
                ItemRequirements = new List<BpTaskItemRequirement>
                {
                    new() { Tpl = "5696686a4bdc2da3298b456a", Count = 1, Name = "信标（美元代位）" },
                },
            },
            // ---- VisitZone ----
            new()
            {
                Id = "weekly_visit_dorms",
                Scope = "weekly",
                Title = "探访宿舍",
                Description = "前往海关的宿舍区域并成功撤离。",
                Xp = 1200,
                Rotation = "random",
                Weight = 2,
                ConditionType = "VisitZone",
                Count = 3,
                Location = "bigmap",
                ZoneId = "ZoneDormitory",
            },
        };
    }
}
