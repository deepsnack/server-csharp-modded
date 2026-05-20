using System.Runtime;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Loaders;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Services;

[Injectable(InjectionType.Singleton)]
public class SptServerStartupService(
    IReadOnlyList<SptMod> loadedMods,
    BundleLoader bundleLoader,
    App app,
    ConfigServer configServer
)
{
    public async Task Startup()
    {
        if (ProgramStatics.MODS())
        {
            foreach (var mod in loadedMods)
            {
                if (mod.ModMetadata.IsBundleMod == true)
                {
                    await bundleLoader.LoadBundlesAsync(mod);
                }
            }
        }

        await app.InitializeAsync();

        // Run garbage collection now the server is ready to start
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);

        // Print registration page URL
        var httpConfig = configServer.GetConfig<HttpConfig>();
        var registerUrl = $"https://{httpConfig.Ip}:{httpConfig.Port}/register/index.html";
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("============================================");
        Console.WriteLine("  用户注册页面:");
        Console.WriteLine($"  {registerUrl}");
        Console.WriteLine("============================================");
        Console.ResetColor();
    }
}
