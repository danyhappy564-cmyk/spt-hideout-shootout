using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace HideoutShootout
{
    [BepInPlugin("com.moxopixel.hideoutshootout", "MoxoPixel-HideoutShootout", "1.8.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        private void Start()
        {
            LogSource = Logger;
            Settings.Init(Config);

            new Patch_ShootingRangeBehaviour_ManualEnterLocation().Enable();
            new Patch_HideoutPlayer_SetPatrol().Enable();
            new Patch_HideoutPlayerOwner_ExitShootingRange().Enable();
            new Patch_HideoutPlayerOwner_TranslateExitScreenInput().Enable();
            new Patch_BotSpawnLimiter_IncreaseUsedPlayerSpawns().Enable();
            new Patch_BotOwner_Create_Diagnostics().Enable();
            new Patch_BotCreatorClient_Activate_Diagnostics().Enable();
            Patch_AcidphantasmBotPlacement_BossProgressiveRegressivePostfix.TryEnable();

            LogSource.LogInfo("Hideout Weapon Freedom loaded");
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