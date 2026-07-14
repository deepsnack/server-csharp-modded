using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>称号模块命令处理器：目录 CRUD、PNG 上传、授予/撤销玩家称号。</summary>
[Injectable]
public class TitleChangeHandler : IBattlePassChangeHandler
{
    private static readonly Regex TitleIdRegex = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Module => "titles";
    public IReadOnlyList<string> CommandTypes { get; } =
        ["title.upsert", "title.delete", "title.image", "title.grant", "title.revoke"];
    public string RequiredCapability => "titles.submit";

    public object Normalize(string commandType, JsonElement input)
    {
        return commandType switch
        {
            "title.upsert" => NormalizeTitle(input),
            "title.delete" => NormalizeTitleId(input),
            "title.image" => NormalizeImage(input),
            "title.grant" => NormalizePlayerTitle(input),
            "title.revoke" => NormalizePlayerTitle(input),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string? Validate(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "title.upsert" => ValidateTitle((BpTitle) normalizedInput),
            "title.delete" => ValidateTitleId(((TitleIdInput) normalizedInput).Id),
            "title.image" => ValidateImage((TitleImageInput) normalizedInput),
            "title.grant" => ValidatePlayerTitle((TitlePlayerInput) normalizedInput),
            "title.revoke" => ValidatePlayerTitle((TitlePlayerInput) normalizedInput),
            _ => $"未知命令类型: {commandType}",
        };
    }

    public string Describe(string commandType, object normalizedInput, object? currentState)
    {
        return commandType switch
        {
            "title.upsert" => currentState is null
                ? $"新增称号: {((BpTitle) normalizedInput).Id}"
                : $"编辑称号: {((BpTitle) normalizedInput).Id}",
            "title.delete" => $"删除称号: {((TitleIdInput) normalizedInput).Id}",
            "title.image" => $"上传称号图片: {((TitleImageInput) normalizedInput).Id}",
            "title.grant" => $"授予称号 {((TitlePlayerInput) normalizedInput).TitleId} → {((TitlePlayerInput) normalizedInput).ProfileId}",
            "title.revoke" => $"撤销称号 {((TitlePlayerInput) normalizedInput).TitleId} ← {((TitlePlayerInput) normalizedInput).ProfileId}",
            _ => commandType,
        };
    }

    public string GetTargetKey(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "title.upsert" => $"titles:title:{((BpTitle) normalizedInput).Id}",
            "title.delete" => $"titles:title:{((TitleIdInput) normalizedInput).Id}",
            "title.image" => $"titles:title:{((TitleImageInput) normalizedInput).Id}",
            "title.grant" or "title.revoke" => $"titles:player:{((TitlePlayerInput) normalizedInput).ProfileId}",
            _ => $"titles:unknown:{commandType}",
        };
    }

    public string GetTargetDisplayName(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "title.upsert" => ((BpTitle) normalizedInput).Name,
            "title.delete" => ((TitleIdInput) normalizedInput).Id,
            "title.image" => ((TitleImageInput) normalizedInput).Id,
            "title.grant" or "title.revoke" =>
                $"{((TitlePlayerInput) normalizedInput).ProfileId} / {((TitlePlayerInput) normalizedInput).TitleId}",
            _ => commandType,
        };
    }

    public object? GetCurrentSnapshot(string targetKey)
    {
        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3)
        {
            return null;
        }

        return parts[1] switch
        {
            "title" => GetTitleSnapshot(parts[2]),
            "player" => new TitlePlayerSnapshot
            {
                ProfileId = parts[2],
                Titles = ClonePlayerTitles(BattlePassStore.GetPlayerTitles(parts[2])),
            },
            _ => null,
        };
    }

    public string GetRevision(object? snapshot)
    {
        if (snapshot is null)
        {
            return "";
        }

        var json = JsonSerializer.Serialize(snapshot, snapshot.GetType(), SerializeOpts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public string ApplyAndActivate(string commandType, object normalizedInput, string? expectedBaseRevision, string? changeId)
    {
        var targetKey = GetTargetKey(commandType, normalizedInput);
        EnsureRevision(targetKey, expectedBaseRevision);

        return commandType switch
        {
            "title.upsert" => ApplyUpsert((BpTitle) normalizedInput),
            "title.delete" => ApplyDelete(((TitleIdInput) normalizedInput).Id),
            "title.image" => ApplyImage((TitleImageInput) normalizedInput),
            "title.grant" => ApplyGrant((TitlePlayerInput) normalizedInput),
            "title.revoke" => ApplyRevoke((TitlePlayerInput) normalizedInput),
            _ => throw new InvalidOperationException($"不支持的命令类型: {commandType}"),
        };
    }

    public string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId)
    {
        var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
        if (!string.Equals(currentRevision, expectedCurrentRevision, StringComparison.Ordinal))
        {
            throw new ChangeConflictException($"回溯目标 {targetKey} 已发生后续改动: expected={expectedCurrentRevision}, actual={currentRevision}");
        }

        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3)
        {
            throw new InvalidOperationException($"无效目标键: {targetKey}");
        }

        switch (parts[1])
        {
            case "title":
                RestoreTitle(parts[2], beforeSnapshot);
                break;
            case "player":
                RestorePlayer(parts[2], beforeSnapshot);
                break;
            default:
                throw new InvalidOperationException($"不支持回溯目标: {targetKey}");
        }

        return GetRevision(GetCurrentSnapshot(targetKey));
    }

    private static BpTitle NormalizeTitle(JsonElement input)
    {
        var title = JsonSerializer.Deserialize<BpTitle>(input.GetRawText(), SerializeOpts)
            ?? throw new ArgumentException("无法解析称号数据");
        title.Id = (title.Id ?? "").Trim();
        title.Name = (title.Name ?? "").Trim();
        title.Type = string.IsNullOrWhiteSpace(title.Type) ? "text" : title.Type.Trim();
        title.Text = title.Text?.Trim();
        title.Color = title.Color?.Trim();
        title.ColorEnd = title.ColorEnd?.Trim();
        title.Description = title.Description?.Trim();
        title.Width = title.Width <= 0 ? 128 : title.Width;
        title.Height = title.Height <= 0 ? 32 : title.Height;
        if (string.Equals(title.Type, "image", StringComparison.OrdinalIgnoreCase))
        {
            title.ImageFile = title.Id + ".png";
            title.Width = 128;
            title.Height = 32;
        }

        return title;
    }

    private static TitleIdInput NormalizeTitleId(JsonElement input)
    {
        var id = input.TryGetProperty("id", out var idProp) ? idProp.GetString()?.Trim() : null;
        return new TitleIdInput { Id = id ?? "" };
    }

    private static TitleImageInput NormalizeImage(JsonElement input)
    {
        var image = JsonSerializer.Deserialize<TitleImageInput>(input.GetRawText(), SerializeOpts)
            ?? throw new ArgumentException("无法解析称号图片数据");
        image.Id = (image.Id ?? "").Trim();
        image.Image = NormalizeBase64(image.Image);
        return image;
    }

    private static TitlePlayerInput NormalizePlayerTitle(JsonElement input)
    {
        var data = JsonSerializer.Deserialize<TitlePlayerInput>(input.GetRawText(), SerializeOpts)
            ?? throw new ArgumentException("无法解析玩家称号数据");
        data.ProfileId = (data.ProfileId ?? "").Trim();
        data.TitleId = (data.TitleId ?? "").Trim();
        return data;
    }

    private static string? ValidateTitle(BpTitle title)
    {
        var idError = ValidateTitleId(title.Id);
        if (idError is not null)
        {
            return idError;
        }

        if (string.IsNullOrWhiteSpace(title.Name))
        {
            return "称号名称不能为空";
        }

        if (!string.Equals(title.Type, "text", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(title.Type, "image", StringComparison.OrdinalIgnoreCase))
        {
            return "称号类型仅支持 text / image";
        }

        return null;
    }

    private static string? ValidateTitleId(string? id)
    {
        return string.IsNullOrWhiteSpace(id) || !TitleIdRegex.IsMatch(id)
            ? "称号 id 仅允许字母/数字/下划线/连字符（≤64 位）"
            : null;
    }

    private static string? ValidateImage(TitleImageInput input)
    {
        var idError = ValidateTitleId(input.Id);
        if (idError is not null)
        {
            return idError;
        }

        if (!BattlePassStore.GetTitleCatalog().Any(t => string.Equals(t.Id, input.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return "先保存称号目录项再上传图片";
        }

        if (string.IsNullOrWhiteSpace(input.Image))
        {
            return "缺少 image";
        }

        if (!TryDecodePng(input.Image, out _, out var error))
        {
            return error;
        }

        return null;
    }

    private static string? ValidatePlayerTitle(TitlePlayerInput input)
    {
        if (string.IsNullOrWhiteSpace(input.ProfileId) || string.IsNullOrWhiteSpace(input.TitleId))
        {
            return "缺少 profileId 或 titleId";
        }

        if (!BattlePassStore.GetTitleCatalog().Any(t => string.Equals(t.Id, input.TitleId, StringComparison.OrdinalIgnoreCase)))
        {
            return "称号不存在";
        }

        return null;
    }

    private void EnsureRevision(string targetKey, string? expectedBaseRevision)
    {
        if (string.IsNullOrEmpty(expectedBaseRevision))
        {
            return;
        }

        var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
        if (!string.Equals(currentRevision, expectedBaseRevision, StringComparison.Ordinal))
        {
            throw new ChangeConflictException($"目标 {targetKey} 基线已变化: expected={expectedBaseRevision}, actual={currentRevision}");
        }
    }

    private string ApplyUpsert(BpTitle title)
    {
        var titles = BattlePassStore.GetTitleCatalog();
        titles.RemoveAll(t => string.Equals(t.Id, title.Id, StringComparison.OrdinalIgnoreCase));
        titles.Add(title);
        BattlePassStore.SaveTitleCatalog(titles);
        return GetRevision(GetCurrentSnapshot($"titles:title:{title.Id}"));
    }

    private string ApplyDelete(string id)
    {
        var titles = BattlePassStore.GetTitleCatalog();
        titles.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        BattlePassStore.SaveTitleCatalog(titles);
        DeleteImage(id);
        return GetRevision(GetCurrentSnapshot($"titles:title:{id}"));
    }

    private string ApplyImage(TitleImageInput input)
    {
        if (!TryDecodePng(input.Image, out var bytes, out var error))
        {
            throw new InvalidOperationException(error);
        }

        Directory.CreateDirectory(BattlePassStore.TitleImageDir);
        File.WriteAllBytes(BattlePassStore.TitleImagePath(input.Id), bytes);

        var titles = BattlePassStore.GetTitleCatalog();
        var title = titles.FirstOrDefault(t => string.Equals(t.Id, input.Id, StringComparison.OrdinalIgnoreCase));
        if (title is not null)
        {
            title.Type = "image";
            title.ImageFile = input.Id + ".png";
            title.Width = 128;
            title.Height = 32;
            BattlePassStore.SaveTitleCatalog(titles);
        }

        return GetRevision(GetCurrentSnapshot($"titles:title:{input.Id}"));
    }

    private string ApplyGrant(TitlePlayerInput input)
    {
        BattlePassStore.GrantTitle(input.ProfileId, input.TitleId);
        return GetRevision(GetCurrentSnapshot($"titles:player:{input.ProfileId}"));
    }

    private string ApplyRevoke(TitlePlayerInput input)
    {
        BattlePassStore.RevokeTitle(input.ProfileId, input.TitleId);
        return GetRevision(GetCurrentSnapshot($"titles:player:{input.ProfileId}"));
    }

    private static TitleCatalogSnapshot? GetTitleSnapshot(string id)
    {
        var title = BattlePassStore.GetTitleCatalog()
            .FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        var image = ReadImageBase64(id);
        return title is null && image is null
            ? null
            : new TitleCatalogSnapshot { Title = title, ImageBase64 = image };
    }

    private static void RestoreTitle(string id, object? beforeSnapshot)
    {
        var titles = BattlePassStore.GetTitleCatalog();
        titles.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        DeleteImage(id);

        if (beforeSnapshot is not null)
        {
            var snapshot = BattlePassSnapshotCodec.Deserialize<TitleCatalogSnapshot>(beforeSnapshot);
            if (snapshot.Title is not null)
            {
                titles.Add(snapshot.Title);
            }

            if (!string.IsNullOrWhiteSpace(snapshot.ImageBase64) && Convert.FromBase64String(snapshot.ImageBase64) is { Length: > 0 } bytes)
            {
                Directory.CreateDirectory(BattlePassStore.TitleImageDir);
                File.WriteAllBytes(BattlePassStore.TitleImagePath(id), bytes);
            }
        }

        BattlePassStore.SaveTitleCatalog(titles);
    }

    private static void RestorePlayer(string profileId, object? beforeSnapshot)
    {
        var snapshot = beforeSnapshot is null
            ? new TitlePlayerSnapshot { ProfileId = profileId, Titles = new BpPlayerTitles() }
            : BattlePassSnapshotCodec.Deserialize<TitlePlayerSnapshot>(beforeSnapshot);
        BattlePassStore.SavePlayerTitles(profileId, snapshot.Titles ?? new BpPlayerTitles());
    }

    private static BpPlayerTitles ClonePlayerTitles(BpPlayerTitles source)
    {
        return new BpPlayerTitles
        {
            Owned = new HashSet<string>(source.Owned, StringComparer.OrdinalIgnoreCase),
            Equipped = source.Equipped,
        };
    }

    private static void DeleteImage(string id)
    {
        try
        {
            var path = BattlePassStore.TitleImagePath(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 回滚/删除时图片文件缺失不应阻断目录恢复。
        }
    }

    private static string? ReadImageBase64(string id)
    {
        try
        {
            var path = BattlePassStore.TitleImagePath(id);
            return File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeBase64(string? image)
    {
        image = (image ?? "").Trim();
        var comma = image.IndexOf(',');
        return image.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0
            ? image[(comma + 1)..].Trim()
            : image;
    }

    private static bool TryDecodePng(string? image, out byte[] bytes, out string error)
    {
        bytes = [];
        error = "";
        try
        {
            bytes = Convert.FromBase64String(NormalizeBase64(image));
        }
        catch
        {
            error = "图片 base64 解析失败";
            return false;
        }

        if (bytes.Length == 0 || bytes.Length > 512 * 1024)
        {
            error = "图片为空或超过 512KB";
            return false;
        }

        if (bytes.Length < 24 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
        {
            error = "仅支持 PNG 图片";
            return false;
        }

        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        if (width != 128 || height != 32)
        {
            error = $"图片尺寸必须为约定的 128×32（当前 {width}×{height}）";
            return false;
        }

        return true;
    }
}

public record TitleIdInput
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

public record TitleImageInput
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("image")]
    public string Image { get; set; } = "";
}

public record TitlePlayerInput
{
    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("titleId")]
    public string TitleId { get; set; } = "";
}

public record TitleCatalogSnapshot
{
    [JsonPropertyName("title")]
    public BpTitle? Title { get; set; }

    [JsonPropertyName("imageBase64")]
    public string? ImageBase64 { get; set; }
}

public record TitlePlayerSnapshot
{
    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("titles")]
    public BpPlayerTitles? Titles { get; set; }
}
