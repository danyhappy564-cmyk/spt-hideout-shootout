using HideoutShootout.Server.Patches;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;

namespace HideoutShootout.Server;

// SPT 4.0.13 keeps OnLoadOrder.PreSptModLoader (4.1 renamed it away from this name),
// and IOnLoad.OnLoad() has no CancellationToken parameter here.
[Injectable(TypePriority = OnLoadOrder.PreSptModLoader + 50)]
public class PatchManager(ISptLogger<PatchManager> logger) : IOnLoad
{
    public Task OnLoad()
    {
        new HideoutBotPrereqPatch().Enable();
        logger.Warning("HideoutShootout.Server loaded");
        Console.WriteLine("[HideoutShootout.Server] loaded");
        return Task.CompletedTask;
    }
}
