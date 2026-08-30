using System;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace HideoutShootout
{
    [BepInPlugin("com.moxopixel.hideoutshootout", "MoxoPixel-HideoutShootout", "1.8.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;
        public static Plugin Instance;

        private void Start()
        {
            Instance = this;
            LogSource = Logger;
            Settings.Init(Config);

            // Each patch is enabled independently: some target internals whose names or
            // signatures can shift between EFT client builds (e.g. across SPT versions), so one
            // patch failing to find its target must not stop the rest from loading.
            TryEnablePatch("ShootingRangeBehaviour_ManualEnterLocation", () => new Patch_ShootingRangeBehaviour_ManualEnterLocation().Enable());
            TryEnablePatch("HideoutPlayer_SetPatrol", () => new Patch_HideoutPlayer_SetPatrol().Enable());
            TryEnablePatch("HideoutPlayerOwner_ExitShootingRange", () => new Patch_HideoutPlayerOwner_ExitShootingRange().Enable());
            TryEnablePatch("HideoutPlayerOwner_TranslateExitScreenInput", () => new Patch_HideoutPlayerOwner_TranslateExitScreenInput().Enable());
            TryEnablePatch("BotSpawnLimiter_IncreaseUsedPlayerSpawns", () => new Patch_BotSpawnLimiter_IncreaseUsedPlayerSpawns().Enable());
            TryEnablePatch("BotOwner_Create_Diagnostics", () => new Patch_BotOwner_Create_Diagnostics().Enable());
            TryEnablePatch("BotCreatorClient_Activate_Diagnostics", () => new Patch_BotCreatorClient_Activate_Diagnostics().Enable());
            TryEnablePatch("AcidphantasmBotPlacement_BossProgressiveRegressivePostfix", () => Patch_AcidphantasmBotPlacement_BossProgressiveRegressivePostfix.TryEnable());
            TryEnablePatch("HollywoodFX_ForceEffectsInHideout", () => Patch_HollywoodFX_ForceEffectsInHideout.TryEnable());
            TryEnablePatch("HollywoodFX_GoreDiagnostics", () => Patch_HollywoodFX_GoreDiagnostics.TryEnable());

            LogSource.LogInfo("Hideout Weapon Freedom loaded");
        }

        private static void TryEnablePatch(string name, Action enable)
        {
            try
            {
                enable();
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Patch '{name}' failed to enable and was skipped: {ex.Message}");
            }
        }

        private void Update()
        {
            // KeyboardShortcut.IsDown() handles the press-once semantics and any modifiers the user
            // configures, so no manual edge tracking is needed here.
            if (!Settings.SpawnHotkey.Value.IsDown())
            {
                return;
            }

            if (!HideoutBotContextController.TrySpawnFromHotkey())
            {
                LogSource.LogDebug($"{Settings.SpawnHotkey.Value} pressed, but hideout shooting-range context is not active.");
            }
        }
    }
}