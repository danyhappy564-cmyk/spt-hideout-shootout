using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace HideoutShootout
{
    public class Settings
    {
        private const string GeneralSectionTitle = "1. General";
        private const string DiagnosticsSectionTitle = "2. Diagnostics";

        public static ConfigFile Config;

        public static ConfigEntry<KeyboardShortcut> SpawnHotkey;
        public static ConfigEntry<float> BotSpawnDistance;
        public static ConfigEntry<bool> FaceBotTowardPlayer;
        public static ConfigEntry<bool> EnableSpawnDiagnostics;
        public static ConfigEntry<bool> EnableRendererDiagnostics;
        public static ConfigEntry<bool> EnableHarmonyPatchDiagnostics;

        public static List<ConfigEntryBase> ConfigEntries = new List<ConfigEntryBase>();

        public static void Init(ConfigFile config)
        {
            Config = config;

            ConfigEntries.Add(SpawnHotkey = config.Bind(
                GeneralSectionTitle,
                "Spawn Scav Hotkey",
                new KeyboardShortcut(KeyCode.F11),
                new ConfigDescription(
                    "Key that spawns a scav target in the hideout shooting range, or replaces the current one. Only works while you are in the shooting range",
                    null,
                    new ConfigurationManagerAttributes { })));

            ConfigEntries.Add(BotSpawnDistance = config.Bind(
                GeneralSectionTitle,
                "Bot Spawn Distance",
                3f,
                new ConfigDescription(
                    "How far in front of you the scav is placed when spawned",
                    new AcceptableValueRange<float>(1.5f, 10f),
                    new ConfigurationManagerAttributes { })));

            ConfigEntries.Add(FaceBotTowardPlayer = config.Bind(
                GeneralSectionTitle,
                "Face Scav Toward Player",
                true,
                new ConfigDescription(
                    "Rotate the spawned scav to face you instead of keeping its spawn rotation",
                    null,
                    new ConfigurationManagerAttributes { })));

            ConfigEntries.Add(EnableSpawnDiagnostics = config.Bind(
                DiagnosticsSectionTitle,
                "Enable Spawn Diagnostics",
                false,
                new ConfigDescription(
                    "Log the hideout bot spawn pipeline: bootstrap candidates, bundle preloads, LocalPlayer creation, BotOwner creation and bot activation",
                    null,
                    new ConfigurationManagerAttributes { })));

            ConfigEntries.Add(EnableRendererDiagnostics = config.Bind(
                DiagnosticsSectionTitle,
                "Enable Renderer Diagnostics",
                false,
                new ConfigDescription(
                    "Dump the spawned scav's renderer state (layer, bounds, culling, materials) at activation and again 2 seconds later. Use when the bot spawns invisible",
                    null,
                    new ConfigurationManagerAttributes { })));

            ConfigEntries.Add(EnableHarmonyPatchDiagnostics = config.Bind(
                DiagnosticsSectionTitle,
                "Enable Harmony Patch Diagnostics",
                false,
                new ConfigDescription(
                    "Log which mods have patched LocalPlayer.Create. Use when bot creation fails and another mod is suspected",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true })));

            RecalcOrder();
        }

        private static void RecalcOrder()
        {
            // Set the Order field for all settings, to avoid unnecessary changes when adding new settings
            int settingOrder = ConfigEntries.Count;
            foreach (var entry in ConfigEntries)
            {
                ConfigurationManagerAttributes attributes = entry.Description.Tags[0] as ConfigurationManagerAttributes;
                if (attributes != null)
                {
                    attributes.Order = settingOrder;
                }

                settingOrder--;
            }
        }
    }
}
