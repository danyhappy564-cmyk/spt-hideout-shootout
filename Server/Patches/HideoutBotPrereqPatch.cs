using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HideoutShootout.Server.Globals;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Spt.Location;
using SPTarkov.Server.Core.Services;

namespace HideoutShootout.Server.Patches;

/// <summary>
/// Scaffold patch point for future hideout bot prerequisite work.
/// Currently pass-through only (no behavior change).
/// </summary>
public class HideoutBotPrereqPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(LocationController), nameof(LocationController.GenerateAll));
    }

    [PatchPostfix]
    public static void Postfix(ref LocationsGenerateAllResponse __result)
    {
        if (!ModConfig.Config.EnableHideoutBotPrereqPatch)
        {
            return;
        }

        if (__result?.Locations == null)
        {
            return;
        }

        LocationBase? hideoutLocation = __result.Locations.Values.FirstOrDefault(location =>
            string.Equals(location?.Id, "hideout", StringComparison.OrdinalIgnoreCase));

        if (hideoutLocation == null)
        {
            return;
        }

        Console.WriteLine("[HideoutShootout.Server] Applying hideout bot prerequisites");

        hideoutLocation.NewSpawn ??= true;
        hideoutLocation.OldSpawn ??= true;

        if (hideoutLocation.BotMax < 1)
        {
            hideoutLocation.BotMax = 1;
        }

        if (hideoutLocation.Waves == null)
        {
            hideoutLocation.Waves = [];
        }

        bool waveExists = hideoutLocation.Waves.Any(w =>
            string.Equals(w.SptId, "hideoutshootout_assault_wave", StringComparison.OrdinalIgnoreCase));

        if (!waveExists)
        {
            hideoutLocation.Waves.Add(new Wave
            {
                SptId = "hideoutshootout_assault_wave",
                BotSide = "Savage",
                WildSpawnType = WildSpawnType.assault,
                SlotsMin = 1,
                SlotsMax = 1,
                TimeMin = 3,
                TimeMax = 6,
                Number = 1,
                SpawnMode = ["regular", "pve"],
                OpenZones = string.Empty,
            });

            Console.WriteLine("[HideoutShootout.Server] Injected hideoutshootout_assault_wave into hideout location");
        }
    }
}
