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
    Watermark watermark,
    WebRegisterController webRegisterController
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
            wipe = false,
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

    public ValueTask<string> RemoveProfile(string url, RemoveProfileData info, MongoId sessionID)
    {
        // 先删除用户的邮箱记录
        webRegisterController.UnregisterEmailBySessionId(sessionID);
        
        // 然后删除profile
        return new ValueTask<string>(httpResponseUtil.NoBody(saveServer.RemoveProfile(sessionID)));
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

    /// <summary>
    /// 获取可用的版本列表
    /// 从 profiles 目录读取版本信息
    /// </summary>
    /// <returns>版本列表</returns>
    public ValueTask<string> GetVersions()
    {
        var result = webRegisterController.GetVersions();
        return new ValueTask<string>(httpResponseUtil.NoBody(result));
    }

    /// <summary>
    /// 发送验证码到指定邮箱
    /// </summary>
    /// <param name="request">包含邮箱的请求</param>
    /// <returns>发送结果</returns>
    public ValueTask<string> SendVerificationCode(dynamic request)
    {
        var result = webRegisterController.SendVerificationCode(request);
        return new ValueTask<string>(httpResponseUtil.NoBody(result));
    }

    /// <summary>
    /// 处理用户注册
    /// </summary>
    /// <param name="request">注册请求</param>
    /// <returns>注册结果</returns>
    public ValueTask<string> WebRegister(WebRegisterRequest request)
    {
        var result = webRegisterController.Register(request);
        return new ValueTask<string>(httpResponseUtil.NoBody(result));
    }
}
