using HideoutShootout.Server.Patches;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace HideoutShootout.Server;

// SPT 4.1 dropped OnLoadOrder.PreSptModLoader; Preload is the earliest ordering
// that still runs as part of normal mod loading.
[Injectable(TypePriority = OnLoadOrder.Preload + 50)]
public class PatchManager(ISptLogger<PatchManager> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        new HideoutBotPrereqPatch().Enable();
        logger.Warning("HideoutShootout.Server loaded");
        Console.WriteLine("[HideoutShootout.Server] loaded");
        return Task.CompletedTask;
    }
}
