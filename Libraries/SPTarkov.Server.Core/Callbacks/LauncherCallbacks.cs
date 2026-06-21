﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Launcher;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Callbacks;

[Injectable]
public class LauncherCallbacks(
    HttpResponseUtil httpResponseUtil,
    LauncherController launcherController,
    ProfileController profileController,
    SaveServer saveServer,
    Watermark watermark
)
{
    public ValueTask<string> Connect()
    {
        return new ValueTask<string>(httpResponseUtil.NoBody(launcherController.Connect()));
    }

    public async ValueTask<string> Login(string url, LoginRequestData info, MongoId sessionID)
    {
        var output = await launcherController.LoginAsync(info);
        return output.IsEmpty ? "FAILED" : output.ToString();
    }

    public async ValueTask<string> Register(string url, RegisterData info, MongoId sessionID)
    {
        var output = await launcherController.Register(info);
        return output.IsEmpty ? string.Empty : output.ToString();
    }

    public ValueTask<string> Get(string url, LoginRequestData info, MongoId sessionID)
    {
        var miniProfile = profileController.GetMiniProfile(sessionID);
        // Convert to camelCase format that launcher expects (AccountInfo expects camelCase field names)
        var output = new
        {
            id = miniProfile.ProfileId,
            username = miniProfile.Username,
            nickname = miniProfile.Nickname,
            wipe = miniProfile.Wipe ?? false,
            edition = miniProfile.Edition
        };
        return new ValueTask<string>(httpResponseUtil.NoBody(output));
    }

    public async ValueTask<string> ChangeUsername(string url, ChangeRequestData info, MongoId sessionID)
    {
        var output = await launcherController.ChangeUsernameAsync(info);
        return output.IsEmpty ? "FAILED" : "OK";
    }

    public async ValueTask<string> ChangePassword(string url, ChangeRequestData info, MongoId sessionID)
    {
        var output = await launcherController.ChangePasswordAsync(info);
        return output.IsEmpty ? "FAILED" : "OK";
    }

    public async ValueTask<string> Wipe(string url, RegisterData info, MongoId sessionID)
    {
        var output = await launcherController.WipeAsync(info);
        return output.IsEmpty ? "FAILED" : "OK";
    }

    public ValueTask<string> GetServerVersion()
    {
        return new ValueTask<string>(httpResponseUtil.NoBody(watermark.GetVersionTag()));
    }

    public ValueTask<string> Ping(string url, EmptyRequestData _, MongoId sessionID)
    {
        return new ValueTask<string>(httpResponseUtil.NoBody("pong!"));
    }

    /// <summary>
    /// 校验当前会话是否仍有效（不复检密码）；响应格式与原 mod 版保持一致（OK/FAILED）
    /// </summary>
    public ValueTask<string> SessionCheck(string url, EmptyRequestData _, MongoId sessionID)
    {
        return new ValueTask<string>(launcherController.Find(sessionID) is null ? "FAILED" : "OK");
    }

    public ValueTask<string> RemoveProfile(string url, RemoveProfileData info, MongoId sessionID)
    {
        // 启动器"删除存档"按本整合版语义是软重置：清空进度并保留账号、密码与注册邮箱。
        // 真删除账号只留给注册管理后台的"删除账号"按钮（那里显式 force + 释放邮箱）。
        return new ValueTask<string>(httpResponseUtil.NoBody(saveServer.SoftResetProfile(sessionID)));
    }

    public ValueTask<string> GetCompatibleTarkovVersion()
    {
        return new ValueTask<string>(httpResponseUtil.NoBody(launcherController.GetCompatibleTarkovVersion()));
    }

    public ValueTask<string> GetLoadedServerMods()
    {
        return new ValueTask<string>(httpResponseUtil.NoBody(launcherController.GetLoadedServerMods()));
    }

    public ValueTask<string> GetServerModsProfileUsed(string url, EmptyRequestData _, MongoId sessionID)
    {
        return new ValueTask<string>(httpResponseUtil.NoBody(launcherController.GetServerModsProfileUsed(sessionID)));
    }

    public ValueTask<string> GetWebDavConfig()
    {
        return new ValueTask<string>(httpResponseUtil.NoBody(launcherController.GetWebDavConfig()));
    }

}
