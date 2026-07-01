using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>任务跳过存档边界；独立封装便于验证保存失败时的回滚。</summary>
[Injectable]
public class QuestSkipProfileSaveService(SaveServer saveServer)
{
    public virtual async Task SaveAsync(MongoId sessionId)
    {
        await saveServer.SaveProfileAsync(sessionId);
    }
}
