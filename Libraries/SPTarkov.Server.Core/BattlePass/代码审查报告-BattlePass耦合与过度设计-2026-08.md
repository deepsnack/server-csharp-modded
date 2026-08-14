# BattlePass 模块审查报告（ponytail 视角 + 耦合/内聚）

> 审查对象：`Libraries/SPTarkov.Server.Core/BattlePass/` · 89 个 .cs · **22,231 行**
> 只读审查，未修改任何源文件。产出本报告文件，位于模块根目录。

## 1. 模块概览

- **规模**：89 文件 / 22,231 行。最大文件 `BattlePassService.cs`(1192)、`BattlePassModels.cs`(1061)、`QuestSync.cs`(983)、`BattlePassAdminController.cs`(989)、`BattlePassController.cs`(805)、`BattlePassStore.cs`(900，含底层读写仅 34 行仍是单文件大静态门面)。
- **子域**：`Administration/`(18 文件，审核流+命令处理器)、`ItemControl/`(13)、`Services/`(40)、`Controllers/`(11)、`Lottery/`(8)、`Portal/`(1)、`Patches/`(1)、`Routers/`(1)。职责横跨：进度/奖励轨、任务追踪、商人/配方/物品管控、抽奖、称号、跳蚤接管、审核工作流、Portal sidecar。
- **持久化**：全部走 `BattlePassStore`（`static class`，SPT_Data/battlepass/*.json，`ReadJson/WriteJson` 双通用方法 + 单把 `Gate` 全局锁 + 每 profile 分锁）。
- **DI**：30 个 `[Injectable(InjectionType.Singleton)]`（captive dependency 的 transient→singleton 修复已基本完成）；但 **11 个 ChangeHandler 与若干服务用裸 `[Injectable]`（默认作用域）**，其中一部分被 Singleton 的 `BattlePassReviewService._handlers` 捕获（详见 §2.5）。

### 依赖方向图

```
BattlePass ──向下引用──► Core（合法方向：通过公有服务）
  SaveServer / DatabaseService / ProfileHelper / PasswordStoreService /
  NotificationSendHelper / TraderAssortHelper / ICloner / JsonUtil 等公有接口
      └─ 也直接改 Core 内部状态：pmc.UnlockedInfo.UnlockedProductionRecipe、
          profile.CustomisationUnlocks、pmc.Quests（§3 违规点）

Core ──反向引用──► BattlePass（⚠ 违规：单向依赖被打破）
  Helpers/QuestHelper.cs:37        → 注入 BattlePass.QuestClientMaskService
  Controllers/TraderController.cs:32→ 注入 BattlePassTraderSync + ItemAcquisitionMaskService
  Helpers/QuestRewardHelper.cs:24  → 注入 BattlePass.ItemControl.ItemAcquisitionMaskService
  Services/CreateProfileService.cs:40 → 注入 ItemAcquisitionMaskService
  Callbacks/DataCallbacks.cs:2     → using BattlePass.ItemControl
```

被 Core 反向引用的类型均为 BP 的**内部实现类**（`QuestClientMaskService`、`BattlePassTraderSync`、`ItemAcquisitionMaskService`），而非 BP 暴露出的稳定接口。结果：vanilla Core 对 mod 产生编译期依赖，mod 不再是可插拔边界。

## 2. 发现清单（按严重度）

### 2.1 coupling（最严重，详见 §3）

- `coupling Helpers/QuestHelper.cs:37` Core 注入 `BattlePass.QuestClientMaskService`（内部类），并在 334/1131 行调用 `IsQuestDisabled` → 任务下发链路硬依赖 BP。建议：BP 通过 Core 既有 quest 过滤扩展点（mod hook / 事件）挂接，或把屏蔽判定做成 Core 侧接口。
- `coupling Controllers/TraderController.cs:32` Core 交易主控制器注入 `BattlePassTraderSync` 与 `ItemAcquisitionMaskService`，并硬编码 `TraderId == BattlePassTraderSync.TraderId` 分支 → 商人主链路被 BP 侵入。建议：改成 Core 泛化的"额外 trader 注册表 + 获取途径掩码"接口。
- `coupling Helpers/QuestRewardHelper.cs:24 / Services/CreateProfileService.cs:40 / Callbacks/DataCallbacks.cs:2` 三处 Core 直接 using BP.ItemControl 并注入 `ItemAcquisitionMaskService` → 物品发放/建档/数据回调均反向依赖 BP。
- `coupling Services/BattlePassService.cs:1101-1102,1143-1155` BP 直接构造并写 `pmc.UnlockedInfo.UnlockedProductionRecipe`、`profile.CustomisationUnlocks`、`pmc.Quests.RemoveAll`，绕过 Core 任何"配方解锁/外观解锁"服务。风险：与 Core 后续重构（如惰性物化、SaveLoadRouter 时序）耦合，回滚逻辑分散。
- `coupling Services/BattlePassService.cs:1177 + 6 处` `saveServer.SaveProfileAsync(...).GetAwaiter().GetResult()` 同步阻塞异步保存，共 **10 处**（BattlePassService/BattlePassHandoverService/BattlePassShopService/BattlePassRewardService/BattlePassTraderSync/LotteryDrawService）→ 死锁/线程池饥饿风险，且绕过 Core 的 SaveLoadRouter 钩子（BP 自己另写 `BattlePassTraderPurchaseSaveLoadRouter` 补位，说明通道已不统一）。

### 2.2 stdlib（手搓标准库能力）

- `stdlib Services/QuestSync.cs:977-982` 手写 `DeterministicId`（SHA256→24 hex MongoId），与 `BattlePassTraderSync.cs:459-461`、`ItemControlSync.cs:504-506` **三份逐字重复**。建议合并到一处工具（或直接吃 Core `HashUtil`），净删 ~12 行×3。
- `stdlib Administration/*ChangeHandler.cs（11 处）` `GetRevision` = `JsonSerializer.Serialize` + `SHA256.HashData` + `ToHexString`，在 Recipe/Shop/Flea/Items/Quest/Title/Trader/Lottery/Track/Task 等 11 个处理器逐字复制（`TrackChangeHandler:50-55`、`TaskChangeHandler:89-95`、`LotteryChangeHandler:105-110` 甚至每次 new `JsonSerializerOptions`）。建议抽取基类/静态 `BattlePassSnapshotCodec.GetRevision`（该文件已存在，可扩充），净删 ~60 行。
- `stdlib Administration/*ChangeHandler.cs` 每个 handler 各自声明 `private static readonly JsonSerializerOptions SerializeOpts`（camelCase）8 份副本 → 应收敛为一个共享常量。

### 2.3 shrink（同样逻辑可更短）

- `shrink Services/BattlePassService.cs:670-719` `TryGrantRewardList`/`TryCommitProgressRewardState` + `CaptureProgressRewardState`/`RestoreProgressRewardState` + `ProgressRewardStateSnapshot` 四件套手工快照/回滚。`BpProgress` 本身就是可克隆 POCO，直接 `var snapshot = (BpProgress)prog.Clone()` 或浅拷贝一个字段字典即可，删 ~90 行。
- `shrink BattlePassStore.cs:842-862` `ListJsonProfileIds` 与 `ListProgressProfileIds`/`ListPlayerTitleProfileIds`（405-425、517-537）同一段 `Directory.EnumerateFiles(dir,"*.json")…` 逻辑 **3 份**，后两者其实只是 `ListJsonProfileIds` 的特化调用 → 收敛为一次调用，删 ~40 行。
- `shrink Services/BattlePassService.cs:596-623 vs 559-623` `ClaimCycle` 与 `Claim` 后半段（空奖励→commit、非空→grant+commit）结构高度相似，可抽 `ClaimCore`，删 ~30 行。
- `shrink BattlePassStore.cs:667-731` Lottery 进度 `GetLotteryProgressMap` 用 `GroupBy().ToDictionary()` 去重，且 `SaveLotteryPoolProgress`/`ResetLotteryPoolProgress` 三重 `WriteJson(map.Values.ToList())`，可收敛为一个 upsert 助手。

### 2.4 native（平台/框架原生已覆盖）

- `native Portal/BattlePassPortalBridgeService.cs:61-107,109-231` 手写 `HttpListener` + 自建 SSO/路由/JSON 回写（~170 行），而工程已是 ASP.NET Core + MVC（`WebRegisterController`、MVC catch-all 已在用）。sidecar 端口/重定向/验签完全可用一个 `[Route]` MVC controller 或最小 API 表达。建议先确认"独立端口 7794"是否真需求；若否，整个 sidecar 可删。净删 ~170 行。

### 2.5 yagni（单实现抽象 / 没人设置的配置 / 一层包装）

- `yagni Administration/RecipeChangeHandler.cs:15` 等 11 个 `[Injectable]`（未显式指定 InjectionType，默认非 Singleton），却由 Singleton 的 `BattlePassReviewService._handlers`（20-28 行注释已承认此坑）捕获。这是**遗留 captive dependency 的镜像**：`BattlePassReviewService`/`BattlePassAdminSessionService` 改成了 Singleton，但被注入的 handlers 仍是非 Singleton → 语义上是"transient 实例被 singleton 持有"。建议 handlers 全部显式 `InjectionType.Singleton`（它们无状态、只依赖 Singleton sync 服务）。
- `yagni Services/BattlePassService.cs:665-668` `GrantRewards` 仅仅是 `GrantRewardList` 的 1:1 转发包装（无额外逻辑）→ 内联删除。
- `yagni Services/QuestSkipProfileSaveService.cs`（13 行）把 `saveServer.SaveProfileAsync` 又包一层 `SaveAsync`，而 `QuestSkipService` 内早已直接 `GetAwaiter().GetResult()` → 包装与直用并存，二选一。
- `yagni Services/BattlePassItemCategoryService.cs` / `BattlePassItemBuilder.cs` / `BattlePassClothingCatalogService` vs `BattlePassClothingSearchService` 职责边界模糊，多个"搜索/目录"服务彼此 new/查询同一批 `databaseService.GetItems()`（见 §2.6 重复遍历），可合并。

### 2.6 delete（死代码 / 投机灵活性）

- `delete Services/QuestSkipRaidStateProbe.cs:43-53` `SptReportsRaidActive` 用**反射**调用本程序集内的 `ProfileActivityService.IsRaidActive`——这是 BP 与 Core 同程序集内自己的类，直接强类型注入即可，反射纯属多余（Fika 部分的 `FindLoadedType` 遍历才需要反射）。删静态缓存变量 + `GetMethod/Invoke`，改直接调用。
- `delete BattlePassModels.cs:1061` 与 `BattlePassDefaults.cs:265` 中为 v0/v1 迁移保留的历史字段（`RewardLedgerInitialized` 迁移分支、`gen_` 混存清除逻辑 `BattlePassStore.GetTasks:246`）——若上一版本已全量迁移完，这些兼容分支进入可删评估。
- `delete# 投机` `BattlePassModConfig.cs` 中 `portalBridge.hosts` 支持 `+`/`*` 通配符绑定及对应警告文案（`BattlePassPortalBridgeService:74`）——单机服场景无公网监听需求，通配符仅是"可能有人要"。

## 3. 耦合违规点（单独列出）

**违规本质**：模块间通信未走暴露接口，且出现 Core→BP 反向依赖。

| 位置 | 违规 | 风险 |
|---|---|---|
| `Helpers/QuestHelper.cs:37` | Core 直接注入 `BattlePass.QuestClientMaskService` | 任务下发主链路随 mod 存在与否改变行为；mod 卸载编译失败 |
| `Controllers/TraderController.cs:32` | Core 交易控制器注入 `BattlePassTraderSync`+掩码服务，硬编码 `TraderId` | 商人购买主链路被侵入，`TraderId` 散落多处(131/141) |
| `Helpers/QuestRewardHelper.cs:24` `Services/CreateProfileService.cs:40` `Callbacks/DataCallbacks.cs:2` | Core 反引 BP.ItemControl | 物品发放/建档回调反向依赖 mod 内部类 |
| `Services/BattlePassService.cs:1081-1178` | BP 直改 `pmc.UnlockedInfo.UnlockedProductionRecipe`、`profile.CustomisationUnlocks`、`pmc.Quests` | 绕过 Core 既有解锁/保存分层，与 SaveLoadRouter 时序耦合，回滚需手工镜像 |
| `BattlePassTraderPurchaseSaveLoadRouter.cs` + 各处 `SaveProfileAsync().GetAwaiter().GetResult()` | BP 自建 SaveLoadRouter 补位，同时又在多服务里同步阻塞保存 | 持久化通道不统一：一半走 router、一半直存，易漏钩子/竞态 |

**正确姿势**：BP 只依赖 Core 公有服务；需要"任务屏蔽/商人扩展/物品获取掩码"时，应由 Core 定义接口（`IQuestMaskService`、`ITraderProvider`、`IItemAcquisitionMask`），BP 实现并注册，Core 通过接口调用——接口应放 Core 或独立 contracts 层，目前全部反向依赖都指向 BP 具体类。

## 4. Top 5 优化建议（收益/成本）

1. **消除 Core→BP 反向依赖**（收益最高）：把 `QuestClientMaskService`、`BattlePassTraderSync`、`ItemAcquisitionMaskService` 三个被 Core 注入的类型抽成 Core 侧接口，Core 依接口、BP 依实现注册。解除 5 个 Core 文件对 mod 的编译期依赖，恢复单向边界。
2. **收敛 `GetRevision` + `DeterministicId` + `SerializeOpts` 三组重复**：抽到 `BattlePassSnapshotCodec`（已存在）与一个工具类，一次删约 90 行 11 处复制代码，且消除"每次 new JsonSerializerOptions"的隐性 GC 开销。
3. **统一持久化通道**：封一个 `BattlePassProfilePersistence`（`SaveProfileAsync` + 回滚），把 10 处 `GetAwaiter().GetResult()` 与手写回滚收敛进去，顺带把 `BattlePassTraderPurchaseSaveLoadRouter` 的职责并回 Core 主 SaveLoad 钩子。
4. **清理 `Claim`/`ClaimCycle`/快照回滚**：抽 `ClaimCore` + 用 clone 替换 4 件套手工快照，删 ~120 行且可读性反升。
5. **评估 Portal sidecar 与 Lotter/商店/目录类重叠**：若独立端口非刚需，`HttpListener` sidecar 改一个 MVC controller（-170 行）；`BattlePassItemCategoryService`/`ItemSearch`/`ClothingCatalog`/`ClothingSearch` 按"目录 vs 检索"合并为两两。

## 5. 结论

模块功能完整、注释与幂等/回滚打磨到位，但**以 vanilla Core 的编译期反向依赖为首要结构债**：模块不再是可插拔边界，任何 Core 任务/交易/物品/建档链路重构都会影响 BP。过度设计集中在 11 个 ChangeHandler 的复制样板（`GetRevision`/快照/SHA256）与三份 `DeterministicId`，以及一个可由 MVC 代替的 `HttpListener` sidecar。

估计可净删 **≈ 550–700 行**（收敛 11×GetRevision ~60 + 3×DeterministicId ~36 + 手工快照回滚 ~90 + 收敛 ListJsonProfileIds ~40 + sidecar 改 MVC ~170 + Claim 合并 ~30 + 一层包装/反射探针 ~20 + 历史兼容分支若干），另有 ~5 处零风险 1:1 包装可立即内联。
