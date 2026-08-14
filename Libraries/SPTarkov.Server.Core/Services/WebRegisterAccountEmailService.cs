using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace SPTarkov.Server.Core.Services;

/// <summary>WebRegister 对其它模块暴露的注册邮箱只读接口。</summary>
public interface IWebRegisterAccountEmailService
{
    /// <summary>按账号 profileId 解析网页注册时登记的邮箱；不存在时返回 null。</summary>
    string? ResolveByProfileId(string? profileId);
}

/// <summary>
///     注册邮箱属于 WebRegister 域。本服务封装 profileId → username → email 的解析，
///     调用方无需也不得直接读取 email_mapping.json。
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class WebRegisterAccountEmailService(
    SaveServer saveServer,
    ISptLogger<WebRegisterAccountEmailService> logger
) : IWebRegisterAccountEmailService
{
    private static readonly object MappingLock = new();

    private static string EmailMappingFilePath
    {
        get
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "webregister", "email_mapping.json");
        }
    }

    public string? ResolveByProfileId(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || !MongoId.IsValidMongoId(profileId))
        {
            return null;
        }

        try
        {
            var sessionId = new MongoId(profileId);
            var username = saveServer.GetUsernameBySessionId(sessionId)?.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            lock (MappingLock)
            {
                if (!File.Exists(EmailMappingFilePath))
                {
                    return null;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(EmailMappingFilePath));
                if (!document.RootElement.TryGetProperty("mappings", out var mappings))
                {
                    return null;
                }

                foreach (var mapping in mappings.EnumerateObject())
                {
                    if (!string.Equals(mapping.Name, username, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var email = mapping.Value.GetString()?.Trim();
                    return string.IsNullOrWhiteSpace(email) ? null : email;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[WebRegister] 解析注册邮箱失败 profileId={profileId}: {ex.GetType().Name}");
        }

        return null;
    }
}
