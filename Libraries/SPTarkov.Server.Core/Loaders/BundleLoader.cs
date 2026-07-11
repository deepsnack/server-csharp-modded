using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Loaders;

/*
{
    "ModPath" : "/user/mods/Mod3",
    "FileName" : "assets/content/weapons/usable_items/item_bottle/textures/client_assets.bundle",
    "Bundle" : {
        "key" : "assets/content/weapons/usable_items/item_bottle/textures/client_assets.bundle",
        "dependencyKeys" : [ ]
    },
    "Crc" : 1030040371,
    "Dependencies" : [ ]
} */
public class BundleInfo(string modPath, BundleManifestEntry bundle, uint bundleHash)
{
    public string ModPath { get; private set; } = modPath;

    public string FileName { get; private set; } = bundle.Key;

    public BundleManifestEntry Bundle { get; private set; } = bundle;

    public uint Crc { get; private set; } = bundleHash;

    public List<string> Dependencies { get; private set; } = bundle?.DependencyKeys ?? [];
}

[Injectable(InjectionType.Singleton)]
public class BundleLoader(ISptLogger<BundleLoader> logger, JsonUtil jsonUtil, BundleHashCacheService bundleHashCacheService)
{
    private readonly ConcurrentDictionary<string, BundleInfo> _bundles = [];

    public async Task LoadBundlesAsync(SptMod mod)
    {
        await bundleHashCacheService.HydrateCache();

        var modPath = mod.GetModPath();
        var relativeModPath = modPath.Replace('\\', '/');
        var bundlesPath = Path.Join(relativeModPath, "bundles");
        var manifestPath = Path.Join(Directory.GetCurrentDirectory(), modPath, "bundles.json");
        var hasBundleFiles = Directory.Exists(bundlesPath) && Directory.EnumerateFiles(bundlesPath, "*", SearchOption.AllDirectories).Any();

        if (!File.Exists(manifestPath))
        {
            if (!hasBundleFiles)
            {
                logger.Debug($"Mod {mod.ModMetadata.Name} has no bundles, skipping bundle load");
                return;
            }

            logger.Error($"Mod {mod.ModMetadata.Name} has bundle files but no bundles.json manifest, skipping!");
            return;
        }

        var modBundles = await jsonUtil.DeserializeFromFileAsync<BundleManifest>(manifestPath);

        if (modBundles?.Manifest is null)
        {
            if (!hasBundleFiles)
            {
                logger.Debug($"Mod {mod.ModMetadata.Name} has an empty bundle manifest and no bundle files, skipping bundle load");
                return;
            }

            logger.Error($"Could not find manifest entries for mod {mod.ModMetadata.Name} despite bundle files being present, skipping!");
            return;
        }

        await Parallel.ForEachAsync(
            modBundles.Manifest,
            async (bundleManifest, ct) =>
            {
                var bundleLocalPath = Path.Join(bundlesPath, bundleManifest.Key).Replace('\\', '/');

                if (!File.Exists(bundleLocalPath))
                {
                    logger.Warning($"Could not find bundle {bundleManifest.Key} for mod {mod.ModMetadata.Name}");
                    return;
                }

                var bundleHash = await bundleHashCacheService.CalculateMatchAndStoreHash(bundleLocalPath);
                AddBundle(bundleManifest.Key, new BundleInfo(relativeModPath, bundleManifest, bundleHash));
            }
        );

        await bundleHashCacheService.WriteCache();
    }

    /// <summary>
    ///     Handle singleplayer/bundles
    /// </summary>
    /// <returns> List of loaded bundles.</returns>
    public List<BundleInfo> GetBundles()
    {
        var result = new List<BundleInfo>();

        foreach (var bundle in _bundles)
        {
            result.Add(bundle.Value);
        }

        return result;
    }

    public BundleInfo? GetBundle(string bundleKey)
    {
        return _bundles.GetValueOrDefault(bundleKey);
    }

    public void AddBundle(string key, BundleInfo bundle)
    {
        var success = _bundles.TryAdd(key, bundle);
        if (!success)
        {
            logger.Error($"Unable to add bundle: {key}");
        }
    }
}

public record BundleManifest
{
    [JsonPropertyName("manifest")]
    public List<BundleManifestEntry>? Manifest { get; set; }
}

public record BundleManifestEntry
{
    [JsonPropertyName("key")]
    public required string Key { get; set; }

    [JsonPropertyName("dependencyKeys")]
    public List<string>? DependencyKeys { get; set; }
}
