using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     Stores launcher credentials outside profile data so profile migrations can safely remove info.password.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class PasswordStoreService
{
    private readonly object syncRoot = new();
    private readonly string passwordFilePath;
    private Dictionary<string, string>? passwordHashes;
    private HashSet<string> provisionalBackupEntries = new(StringComparer.Ordinal);
    private bool loadFailed;

    public PasswordStoreService()
        : this(Path.Combine("user", "passwords.json"))
    {
    }

    internal PasswordStoreService(string passwordFilePath)
    {
        this.passwordFilePath = passwordFilePath;
    }

    public string? GetHash(MongoId profileId)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            return passwordHashes!.GetValueOrDefault(profileId.ToString());
        }
    }

    public bool Verify(MongoId profileId, string? password)
    {
        var storedHash = GetHash(profileId);
        return storedHash is not null && string.Equals(storedHash, Hash(password ?? string.Empty), StringComparison.Ordinal);
    }

    public bool SetPassword(MongoId profileId, string? password)
    {
        return SetHash(profileId, Hash(password ?? string.Empty));
    }

    public bool SetHash(MongoId profileId, string passwordHash)
    {
        if (profileId.IsEmpty || string.IsNullOrWhiteSpace(passwordHash))
        {
            return false;
        }

        lock (syncRoot)
        {
            EnsureLoaded();
            return SetHashAndPersist(profileId.ToString(), passwordHash);
        }
    }

    /// <summary>
    ///     Imports an old profile hash only when no authoritative entry exists. Entries recovered from a
    ///     .bak-merge file are provisional for this process, allowing a newer profile hash to replace them once.
    /// </summary>
    public bool ImportLegacyHash(MongoId profileId, string passwordHash)
    {
        if (profileId.IsEmpty || string.IsNullOrWhiteSpace(passwordHash))
        {
            return false;
        }

        lock (syncRoot)
        {
            EnsureLoaded();
            var key = profileId.ToString();
            if (passwordHashes!.ContainsKey(key) && !provisionalBackupEntries.Contains(key))
            {
                return true;
            }

            return SetHashAndPersist(key, passwordHash);
        }
    }

    public bool Remove(MongoId profileId)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            var key = profileId.ToString();
            if (!passwordHashes!.Remove(key, out var previousHash))
            {
                return true;
            }

            var wasProvisional = provisionalBackupEntries.Remove(key);
            if (Persist())
            {
                return true;
            }

            passwordHashes[key] = previousHash;
            if (wasProvisional)
            {
                provisionalBackupEntries.Add(key);
            }

            return false;
        }
    }

    public static string Hash(string password)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
    }

    private bool SetHashAndPersist(string profileId, string passwordHash)
    {
        var normalizedHash = passwordHash.Trim().ToUpperInvariant();
        var hadPreviousValue = passwordHashes!.TryGetValue(profileId, out var previousHash);
        passwordHashes[profileId] = normalizedHash;

        if (Persist())
        {
            provisionalBackupEntries.Remove(profileId);
            return true;
        }

        if (hadPreviousValue)
        {
            passwordHashes[profileId] = previousHash!;
        }
        else
        {
            passwordHashes.Remove(profileId);
        }

        return false;
    }

    private void EnsureLoaded()
    {
        if (passwordHashes is not null)
        {
            return;
        }

        var sourcePath = passwordFilePath;
        var loadedFromBackup = false;
        if (!File.Exists(sourcePath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(passwordFilePath))!;
            var fileName = Path.GetFileName(passwordFilePath);
            sourcePath = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, $"{fileName}.bak-merge*")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault() ?? passwordFilePath
                : passwordFilePath;
            loadedFromBackup = File.Exists(sourcePath);
        }

        try
        {
            passwordHashes = File.Exists(sourcePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(sourcePath)) ?? []
                : [];
        }
        catch
        {
            passwordHashes = [];
            loadFailed = true;
        }

        passwordHashes = new Dictionary<string, string>(passwordHashes, StringComparer.Ordinal);
        if (loadedFromBackup && !loadFailed)
        {
            provisionalBackupEntries = passwordHashes.Keys.ToHashSet(StringComparer.Ordinal);
            Persist();
        }
    }

    private bool Persist()
    {
        if (loadFailed)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(passwordFilePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        var temporaryPath = $"{fullPath}.tmp";

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(passwordHashes, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, fullPath, true);
            return true;
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Keep the original credentials file intact when cleanup also fails.
            }

            return false;
        }
    }
}
