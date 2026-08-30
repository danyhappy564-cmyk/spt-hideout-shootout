using EFT;
using EFT.Game.Spawning;
using EFT.Hideout;
using EFT.InputSystem;
using EFT.UI;
using EFT.Weather;
using HarmonyLib;
using SPT.Reflection.Patching;
using SPT.Reflection.Utils;
using Comfort.Common;
using Diz.Jobs;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace HideoutShootout
{
    internal static class HideoutBotContextController
    {
        private static BotOwner _trackedBot;
        private static bool _spawnInProgress;
        private static int _bootstrapAttemptCount;
        private static float _lastBootstrapAttemptAt;
        private static bool _loggedBootstrapCandidates;
        private static BotZone _syntheticBotZone;
        private static SpawnPointMarker _syntheticSpawnPointMarker;
        private static string _syntheticBotZoneName = "HideoutShootout_RuntimeZone";
        private static AICorePoint _syntheticCorePoint;
        private static PatrolWay _syntheticPatrolWay;
        private static PatrolPoint _syntheticPatrolPoint;
        private static IBotCreator _realBotCreator;

        private static bool _harmonyLocalPlayerCreateLogged;

        /// <summary>
        /// Logs a spawn-pipeline trace, but only when "Enable Spawn Diagnostics" is on in the mod's
        /// BepInEx config. Warnings and errors are always logged; these are the step-by-step traces
        /// that are only useful when something is being investigated.
        /// </summary>
        private static void LogSpawnDiagnostic(string message)
        {
            if (Settings.EnableSpawnDiagnostics?.Value == true)
            {
                Plugin.LogSource.LogInfo(message);
            }
        }

        /// <summary>
        /// True when the player is in the hideout shooting range, which is the only place a scav
        /// target can be spawned.
        /// </summary>
        public static bool CanSpawnInShootingRange(HideoutPlayerOwner owner)
        {
            return owner != null && owner.InShootingRange;
        }

        /// <summary>
        /// Spawns a scav target, or replaces the existing one, straight from the configured hotkey.
        /// Returns false when the player is not in the shooting range.
        /// </summary>
        public static bool TrySpawnFromHotkey()
        {
            HideoutPlayerOwner owner = FindHideoutPlayerOwner();
            if (!CanSpawnInShootingRange(owner))
            {
                return false;
            }

            _ = SpawnOrReplaceTrackedBot(owner);
            return true;
        }

        private static async Task SpawnOrReplaceTrackedBot(HideoutPlayerOwner owner)
        {
            if (_spawnInProgress || owner == null)
            {
                return;
            }

            _spawnInProgress = true;
            try
            {
                DespawnTrackedBot();
                await SpawnAssaultBotNearPlayer(owner);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Failed spawning hideout bot: {ex}");
            }
            finally
            {
                _spawnInProgress = false;
            }
        }

        private static async Task SpawnAssaultBotNearPlayer(HideoutPlayerOwner owner)
        {
            AbstractGame game = Singleton<AbstractGame>.Instance;
            if (game == null)
            {
                Plugin.LogSource.LogWarning("AbstractGame is unavailable in hideout.");
                return;
            }

            BotsController botsController = FindBotsController(game);

            if (botsController == null)
            {
                Plugin.LogSource.LogWarning("BotsController is unavailable in hideout.");
                return;
            }

            EnsureSyntheticBotZoneForOwner(owner);
            EnsureHideoutSpawnSingletons();

            BotSpawner spawner = await ResolveSpawner(botsController);
            if (spawner == null)
            {
                spawner = await TryBootstrapSpawner(game, botsController);
            }

            if (spawner == null)
            {
                LogHideoutBotPrereqDiagnostics(game, botsController);
                Plugin.LogSource.LogWarning("Bot spawner is unavailable in hideout.");
                return;
            }

            BotOwner createdBot = null;
            Action<BotOwner> botCreatedHandler = bot =>
            {
                if (createdBot == null)
                {
                    createdBot = bot;
                }
            };

            spawner.OnBotCreated += botCreatedHandler;
            try
            {
                bool spawnedDirect = await TrySpawnDirectAtSyntheticPoint(spawner, owner);
                if (!spawnedDirect)
                {
                    // Fallback to the standard force path. Mostly retained for diagnostics; in hideout
                    // SpawnBotByTypeForce typically completes without producing a bot because
                    // SpawnSystem.SelectAISpawnPoints rejects our synthetic point under raid-style
                    // validation rules (see docs/source-notes.md). The direct path above mirrors
                    // SPT.Debugging's PMCBotSpawnLocationPatch and is the supported route.
                    await spawner.SpawnBotByTypeForce(1, WildSpawnType.assault, BotDifficulty.normal, new BotSpawnParams());
                }

                // Allow the async activation pipeline a brief window to invoke OnBotCreated.
                for (int waitTick = 0; waitTick < 30 && createdBot == null; waitTick++)
                {
                    await Task.Delay(100);
                }
            }
            finally
            {
                spawner.OnBotCreated -= botCreatedHandler;
            }

            if (createdBot == null)
            {
                Plugin.LogSource.LogWarning("No bot was created by direct hideout spawn call.");
                return;
            }

            PositionAndFreezeBot(owner, createdBot);
            _trackedBot = createdBot;
            DisableBotCulling(createdBot.GetPlayer);
            Plugin.LogSource.LogInfo("Spawned hideout scav target.");

            // Re-dump renderer state once the bot has had frames to render, to confirm whether the
            // bounds fix actually made the body meshes visible.
            await Task.Delay(2000);
            LogBotRendererState(createdBot.GetPlayer, "2s after spawn");
        }

        /// <summary>
        /// Direct-position spawn path modelled after <c>SPT.Debugging.Patches.PMCBotSpawnLocationPatch</c>:
        /// build a single-bot <see cref="BotCreationData"/>, attach a spawn position with our
        /// synthetic core point id, and hand it to <see cref="BotSpawner.SpawnBotsInZoneOnPositions"/>.
        /// This bypasses <c>SpawnSystem.SelectAISpawnPoints</c>, which rejects our synthetic
        /// hideout spawn point under raid-style validation rules even though the underlying
        /// <see cref="SpawnPointMarker"/> is otherwise valid.
        /// </summary>
        private static async Task<bool> TrySpawnDirectAtSyntheticPoint(BotSpawner spawner, HideoutPlayerOwner owner)
        {
            try
            {
                if (_syntheticBotZone == null || _syntheticSpawnPointMarker == null)
                {
                    Plugin.LogSource.LogDebug("Direct hideout spawn skipped: synthetic zone or spawn point unavailable.");
                    return false;
                }

                ISpawnPoint syntheticSpawnPoint = _syntheticSpawnPointMarker.SpawnPoint;
                if (syntheticSpawnPoint == null)
                {
                    Plugin.LogSource.LogDebug("Direct hideout spawn skipped: SpawnPointMarker.SpawnPoint is null.");
                    return false;
                }

                // PORTING NOTE (SPT 4.0.13): this field is public here (BotSpawner.BotCreator),
                // not the private _botCreator this mod's SPT 4.1 target reflects into.
                IBotCreator botCreator = spawner.BotCreator;
                if (botCreator == null)
                {
                    Plugin.LogSource.LogDebug("Direct hideout spawn skipped: BotSpawner.BotCreator is null.");
                    return false;
                }

                // Defensive: BotCreationData instance ctor dereferences
                // Singleton<GlobalEventDispatcher>.Instance to subscribe OnStopBotSpawn. In hideout the
                // singleton is never created by BaseLocalGame, so we must instantiate it ourselves
                // before the constructor runs.
                EnsureHideoutSpawnSingletons();

                // BotsController.Init was bootstrapped with a NoopBotCreator stub (see
                // TryDirectBotsControllerInit) because BaseLocalGame never runs in hideout to
                // produce the real wave-loading BotProfileClient pipeline. Noop's GenerateProfile
                // returns null, so BotCreationData.Create yields no profiles and no bot is
                // ever created. Swap in a real BotProfileClient-backed BotCreatorClient before the
                // create+activate pipeline runs so SPT's /client/game/bot/generate endpoint is
                // actually queried for a profile.
                if (botCreator is NoopBotCreator)
                {
                    IBotCreator realCreator = EnsureRealBotCreator(spawner);
                    if (realCreator != null)
                    {
                        spawner.BotCreator = realCreator;
                        botCreator = realCreator;
                        LogSpawnDiagnostic("Swapped NoopBotCreator for real BotCreatorClient on BotSpawner.");
                    }
                    else
                    {
                        Plugin.LogSource.LogWarning("Direct hideout spawn aborted: failed to build real IBotCreator (session unavailable?).");
                        return false;
                    }
                }

                GetProfileDataParams profileData = new GetProfileDataParams(
                    EPlayerSide.Savage,
                    WildSpawnType.assault,
                    BotDifficulty.normal,
                    0f,
                    null,
                    false);

                // The Create(...) async state machine unconditionally calls
                // token.GetCancelToken() at IL offset 0x107 of BotCreationData/CG_Create.MoveNext, so passing null
                // for the ITokenGetter token NREs before any work runs. BotSpawner itself
                // implements ITokenGetter and exposes its own CancellationToken, mirroring how the
                // raid wave system passes the spawner as the token source.
                BotCreationDataClass creationData = await BotCreationDataClass.Create(profileData, botCreator, 1, spawner);
                if (creationData == null || creationData.Count == 0)
                {
                    Plugin.LogSource.LogWarning("Direct hideout spawn aborted: BotCreationDataClass.Create yielded no profiles.");
                    return false;
                }

                // BotSpawner.SpawnBotsInZoneOnPositions (CG_Struct293.MoveNext IL_0066-IL_0080) iterates the supplied
                // spawnPoints list and itself calls creationData.AddPosition(p.Position, p.CorePointId)
                // for each one, then routes through _botCreator.ActivateBot. Calling
                // AddPosition ourselves on top of that produced a duplicate position with mismatched
                // count vs. profiles, so we instead nudge the synthetic SpawnPoint to the
                // player-relative position and let CG_Struct293 add it.
                Vector3 spawnPosition = owner?.Player?.Transform != null
                    ? owner.Player.Transform.position + owner.Player.Transform.forward * 2.5f
                    : syntheticSpawnPoint.Position;
                int corePointId = syntheticSpawnPoint.CorePointId;
                _syntheticSpawnPointMarker.transform.position = spawnPosition;
                if (_syntheticCorePoint != null)
                {
                    _syntheticCorePoint.transform.position = spawnPosition;
                }
                if (syntheticSpawnPoint is SpawnPoint concreteSpawnPoint)
                {
                    // SpawnPoint exposes Position as a public field (with an explicit-interface
                    // ISpawnPoint.Position get accessor that just returns the field). Update the
                    // field directly so CG_Struct293's per-spawn-point AddPosition uses our updated
                    // location.
                    AccessTools.Field(typeof(SpawnPoint), "Position")?.SetValue(concreteSpawnPoint, spawnPosition);
                }

                // Pre-flight sanity: the player materialization async state machine
                // (BotCreatorClient/CG_CreateBot.MoveNext) reads Singleton<GameWorld>.Instance and
                // BotsController.CoversData.AICorePointsHolder.GetCorePoint(corePointId) before
                // calling BotOwner.Create(...). If either is missing the activation chain throws
                // and the exception is swallowed by Task.HandleExceptions() on the caller, so log
                // the resolution status before dispatch.
                bool gameWorldReady = Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance != null;
                AICoversData coversDataAtDispatch = UnityEngine.Object.FindObjectOfType<AICoversData>();
                AICorePoint resolvedCorePoint = coversDataAtDispatch?.AICorePointsHolder?.GetCorePoint(corePointId);
                if (!gameWorldReady)
                {
                    Plugin.LogSource.LogWarning("Direct hideout spawn pre-flight: Singleton<GameWorld>.Instance is null; LocalPlayer factory will fail.");
                }
                if (resolvedCorePoint == null)
                {
                    Plugin.LogSource.LogWarning($"Direct hideout spawn pre-flight: AICorePointsHolder.GetCorePoint({corePointId}) returned null; BotOwner.Create will get null StartCorePoint.");
                }
                else
                {
                    Plugin.LogSource.LogDebug($"Direct hideout spawn pre-flight: resolved AICorePoint Id={resolvedCorePoint.Id} for CorePointId={corePointId}.");
                }

                List<ISpawnPoint> spawnPoints = new List<ISpawnPoint> { syntheticSpawnPoint };
                spawner.SpawnBotsInZoneOnPositions(spawnPoints, _syntheticBotZone, creationData, null);
                LogSpawnDiagnostic($"Direct hideout spawn dispatched at {spawnPosition} (CorePointId={corePointId}).");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Direct hideout spawn threw, falling back to force path: {ex.Message}");
                Plugin.LogSource.LogDebug(ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Ensures the runtime singletons that the bot spawn pipeline assumes are present in raid
        /// are also present in hideout. Currently only <see cref="GlobalEventDispatcher"/> is required:
        /// the <see cref="BotCreationData"/> instance constructor unconditionally dereferences
        /// <c>Singleton&lt;GlobalEventDispatcher&gt;.Instance</c> to subscribe its <c>StopSpawn</c> handler
        /// to <c>OnStopBotSpawn</c>. In hideout, <c>BaseLocalGame</c> never runs, so the singleton
        /// is null and any call to <see cref="BotCreationData.Create"/> throws an
        /// NRE deep inside its async state machine. Mirroring the canonical
        /// <c>if (!Singleton&lt;GlobalEventDispatcher&gt;.Instantiated) Singleton&lt;GlobalEventDispatcher&gt;.Create(new GlobalEventDispatcher())</c>
        /// pattern from <c>BaseLocalGame</c> avoids that crash.
        /// </summary>
        private static void EnsureHideoutSpawnSingletons()
        {
            try
            {
                if (!Singleton<GlobalEventDispatcher>.Instantiated)
                {
                    Singleton<GlobalEventDispatcher>.Create(new GlobalEventDispatcher());
                    LogSpawnDiagnostic("Created Singleton<GlobalEventDispatcher> for hideout spawn pipeline.");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Failed to create Singleton<GlobalEventDispatcher> in hideout: {ex.Message}");
                Plugin.LogSource.LogDebug(ex.ToString());
            }
        }

        private static BotsController FindBotsController(object game)
        {
            Type type = game.GetType();
            while (type != null)
            {
                PropertyInfo controllerProperty = type
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(p => typeof(BotsController).IsAssignableFrom(p.PropertyType));

                if (controllerProperty != null)
                {
                    BotsController fromProperty = controllerProperty.GetValue(game) as BotsController;
                    if (fromProperty != null)
                    {
                        return fromProperty;
                    }
                }

                FieldInfo controllerField = type
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(f => typeof(BotsController).IsAssignableFrom(f.FieldType));

                if (controllerField != null)
                {
                    BotsController fromField = controllerField.GetValue(game) as BotsController;
                    if (fromField != null)
                    {
                        return fromField;
                    }
                }

                type = type.BaseType;
            }

            return null;
        }

        /// <summary>
        /// Reads the first member matching any of <paramref name="names"/> off
        /// <paramref name="target"/>, trying a property then a field at each level of the
        /// type hierarchy.
        /// <para>
        /// Two SPT 4.1 details make the naive lookup fail. The game's location members
        /// (<c>Location</c>, <c>PlayerOwner</c>) live on <c>BaseLocalGame&lt;T&gt;</c>, not on
        /// <c>HideoutGame</c>, and their backing fields are private - so a
        /// <c>GetField(..., NonPublic)</c> on the concrete type finds nothing without walking
        /// base types. And on <c>JsonType.LocationSettings.Location</c>, <c>Id</c>, <c>waves</c>
        /// and <c>OpenZones</c> are plain fields, so a property-only lookup silently
        /// returns null.
        /// </para>
        /// </summary>
        private static object GetMemberValue(object target, params string[] names)
        {
            if (target == null)
            {
                return null;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            foreach (string name in names)
            {
                for (Type type = target.GetType(); type != null; type = type.BaseType)
                {
                    PropertyInfo property = type.GetProperty(name, flags);
                    if (property != null && property.CanRead)
                    {
                        return property.GetValue(target);
                    }

                    FieldInfo field = type.GetField(name, flags);
                    if (field != null)
                    {
                        return field.GetValue(target);
                    }
                }
            }

            return null;
        }

        private static void LogHideoutBotPrereqDiagnostics(AbstractGame game, BotsController botsController)
        {
            try
            {
                object locationObj = GetMemberValue(game, "Location", "_location");

                string locationId = "<unknown>";
                if (locationObj != null)
                {
                    locationId = GetMemberValue(locationObj, "Id", "_Id") as string ?? "<unknown>";
                }

                object botSpawnerField = GetMemberValue(botsController, "BotSpawner", "_botSpawner");
                object wavesObj = GetMemberValue(locationObj, "waves", "Waves");

                int wavesCount = 0;
                if (wavesObj is System.Collections.ICollection collection)
                {
                    wavesCount = collection.Count;
                }

                Plugin.LogSource.LogWarning($"Hideout bot diagnostics: game={game.GetType().Name}, location={locationId}, botSpawnerFieldNull={botSpawnerField == null}, wavesCount={wavesCount}, ibotgamePresent={Singleton<IBotGame>.Instantiated}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Failed to collect hideout bot diagnostics: {ex.Message}");
            }
        }

        private static async Task<BotSpawner> TryBootstrapSpawner(AbstractGame game, BotsController botsController)
        {
            if (game == null || botsController == null)
            {
                return null;
            }

            float now = Time.realtimeSinceStartup;
            if (now - _lastBootstrapAttemptAt < 0.5f)
            {
                return null;
            }

            _lastBootstrapAttemptAt = now;
            _bootstrapAttemptCount++;

            try
            {
                if (!_loggedBootstrapCandidates)
                {
                    _loggedBootstrapCandidates = true;
                    LogBootstrapCandidates(game, botsController);
                }

                // Direct BotsController.Init is the only bootstrap that works in the hideout.
                // BaseLocalGame.vmethod_1 is a stub that only EFT.LocalGame overrides with the real
                // bot-controller setup, and BaseLocalGame.Run expects the backendUrl+inventory raid
                // workflow - neither does anything useful for a HideoutGame.
                return await TryDirectBotsControllerInit(game, botsController);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Bot spawner bootstrap attempt #{_bootstrapAttemptCount} failed: {ex.Message}");
                return null;
            }
        }

        private static async Task<BotSpawner> TryDirectBotsControllerInit(AbstractGame game, BotsController botsController)
        {
            try
            {
                MethodInfo initMethod = botsController.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "Init" && m.GetParameters().Length == 13);

                if (initMethod == null)
                {
                    Plugin.LogSource.LogDebug("BotsController.Init method was not found for direct init fallback.");
                    return null;
                }

                ParameterInfo[] parameters = initMethod.GetParameters();
                object[] args = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    Type parameterType = parameters[i].ParameterType;

                    if (parameterType == typeof(bool))
                    {
                        args[i] = i == 5; // botEnable=true, others false by default
                        continue;
                    }

                    if (parameterType == typeof(string))
                    {
                        args[i] = ResolveOpenZones(game) ?? string.Empty;
                        continue;
                    }

                    if (parameterType.IsArray)
                    {
                        object resolvedArray = TryResolveMemberByTypeDeep(game, parameterType, 4)
                            ?? (Singleton<GameWorld>.Instantiated ? TryResolveMemberByTypeDeep(Singleton<GameWorld>.Instance, parameterType, 4) : null)
                            ?? TryResolveMemberByTypeDeep(botsController, parameterType, 3);

                        if (resolvedArray == null && parameterType.GetElementType() == typeof(BotZone))
                        {
                            resolvedArray = ResolveBotZonesFromScene();
                        }

                        if (resolvedArray == null)
                        {
                            args[i] = Array.CreateInstance(parameterType.GetElementType(), 0);
                        }
                        else
                        {
                            args[i] = resolvedArray;
                        }

                        continue;
                    }

                    object resolved = TryResolveMemberByTypeDeep(game, parameterType, 4)
                        ?? (Singleton<GameWorld>.Instantiated ? TryResolveMemberByTypeDeep(Singleton<GameWorld>.Instance, parameterType, 4) : null)
                        ?? TryResolveMemberByTypeDeep(botsController, parameterType, 3);

                    if (resolved == null && parameterType.Name.Contains("IBotGame"))
                    {
                        resolved = new HideoutBotGameAdapter(game, botsController);
                    }
                    else if (resolved == null && parameterType.Name.Contains("IBotCreator"))
                    {
                        // Prefer the real BotProfileClient-backed creator so BotsController.Init wires the
                        // raid-equivalent profile pipeline. Fall back to the noop stub only if we
                        // cannot resolve a session yet, so the rest of Init can still proceed.
                        IBotGame ibotGameForCreator = (args.OfType<IBotGame>().FirstOrDefault())
                            ?? new HideoutBotGameAdapter(game, botsController);
                        resolved = EnsureRealBotCreator(ibotGameForCreator)
                            ?? (object)new NoopBotCreator();
                    }
                    else if (resolved == null && parameterType.Name.Contains("ISpawnSystem"))
                    {
                        resolved = ResolveSpawnSystemDeep(parameterType)
                            ?? TryCreateSpawnSystem(parameterType, game, botsController);
                    }
                    else if (resolved == null && parameterType.Name.Contains("BotLocationModifier"))
                    {
                        resolved = Activator.CreateInstance(parameterType);
                    }
                    else if (resolved == null && parameterType.Name.Contains("IPlayersCollection") && Singleton<GameWorld>.Instantiated)
                    {
                        resolved = Singleton<GameWorld>.Instance;
                    }
                    else if (resolved == null && parameterType.Name.Contains("BotLocationEvents"))
                    {
                        // SPT 4.1: BotsController.Init's last parameter went from
                        // LocationSettingsClass.Location.EventsDataClass to
                        // JsonType.LocationSettings.Location.BotLocationEvents.
                        resolved = Activator.CreateInstance(parameterType);
                    }

                    args[i] = resolved;
                }

                if (args.Any(a => a == null))
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] == null)
                        {
                            Plugin.LogSource.LogDebug($"Direct Init unresolved parameter {i}: {parameters[i].Name} ({parameters[i].ParameterType.Name})");
                        }
                    }

                    Plugin.LogSource.LogDebug("Direct BotsController.Init fallback skipped due to unresolved required arguments.");
                    return null;
                }

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType.IsArray && parameters[i].ParameterType.GetElementType() == typeof(BotZone))
                    {
                        Array zones = args[i] as Array;
                        if (zones == null || zones.Length == 0)
                        {
                            Plugin.LogSource.LogWarning("Direct BotsController.Init fallback blocked: no BotZone objects found in hideout scene. AICoversData.CreateOrFind requires at least one BotZone.");
                            return null;
                        }
                    }
                }

                LogSpawnDiagnostic("Attempting direct BotsController.Init fallback for hideout.");
                EnsureAICoversDataRuntimeDefaults();
                initMethod.Invoke(botsController, args);
                await Task.Delay(100);

                BotSpawner spawner = await ResolveSpawner(botsController);
                if (spawner != null)
                {
                    LogSpawnDiagnostic("Bot spawner became available after direct BotsController.Init fallback.");
                }

                return spawner;
            }
            catch (Exception ex)
            {
                // Even when initMethod.Invoke throws, the exception may have come from a third-party
                // Harmony postfix patch on BotsController.Init (e.g. Orbit.OrbitInitPatch) running AFTER
                // the original method body completed successfully. In that case BotsController._botSpawner will already
                // be assigned. Treat the call as effectively successful and return the spawner.
                BotSpawner spawnerAfterThrow = GetSpawnerFromController(botsController);
                if (spawnerAfterThrow != null)
                {
                    LogSpawnDiagnostic("Direct BotsController.Init body succeeded, but a third-party postfix patch threw. Continuing with resolved spawner.");
                    LogInitExceptionChain(ex, BepInEx.Logging.LogLevel.Debug);
                    return spawnerAfterThrow;
                }

                Plugin.LogSource.LogWarning($"Direct BotsController.Init fallback failed: {ex.Message}");
                LogInitExceptionChain(ex, BepInEx.Logging.LogLevel.Warning);
                return null;
            }
        }

        private static BotZone[] ResolveBotZonesFromScene()
        {
            try
            {
                BotZone[] zones = UnityEngine.Object.FindObjectsOfType<BotZone>();
                if (zones != null && zones.Length > 0)
                {
                    LogSpawnDiagnostic($"Resolved BotZone[] from scene: {zones.Length} zones");
                    return zones;
                }

                Plugin.LogSource.LogWarning("No BotZone objects found in scene during hideout bootstrap.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogDebug($"Failed to resolve BotZone[] from scene: {ex.Message}");
            }

            if (_syntheticBotZone != null)
            {
                LogSpawnDiagnostic("Falling back to synthetic BotZone for hideout bootstrap.");
                return new[] { _syntheticBotZone };
            }

            return Array.Empty<BotZone>();
        }

        /// <summary>
        /// Forces every skinned mesh on the bot to recompute its bounds from live bone positions.
        /// <para>
        /// The bot's body meshes are enabled, on a rendered layer, with valid materials - but Unity
        /// frustum-culls them (isVisible=false) while equipment on the same character renders. That is
        /// the signature of a SkinnedMeshRenderer whose cached bounds do not match where the mesh
        /// actually is, so the bounding box falls outside the frustum. In a raid the bounds are kept
        /// current by the animation/culling machinery; in the hideout the bot is frozen in place and
        /// nothing refreshes them. updateWhenOffscreen makes Unity recompute bounds each frame, which
        /// costs a little per-frame work for one bot and is irrelevant at this scale.
        /// </para>
        /// </summary>
        /// <summary>
        /// Turns off EFT's offline player culling for the bot so its body is actually drawn.
        /// <para>
        /// The bot's body meshes are enabled, correctly bounded, on a rendered layer and have valid
        /// materials, yet are never drawn - because <c>LocalPlayer.botPlayerCulling</c>
        /// (an <see cref="OfflinePlayerCulling"/>) sets <c>Renderer.forceRenderingOff = true</c> on
        /// them. That flag skips rendering while leaving <c>enabled</c> true, which is why every other
        /// measurement looked healthy. It applies only to <c>PlayerBody.GetRenderersNonAlloc</c>, so
        /// equipment keeps rendering - exactly the "gear but no body" symptom.
        /// </para>
        /// <para>
        /// <c>BasePlayerCulling.IsVisible</c> is true when the mode is Disabled or Visible, and in Auto
        /// mode defers to a culling state toggle that nothing drives in the hideout, leaving it false
        /// forever. <c>Disable()</c> sets mode Disabled; <c>ApplyVisibleState()</c> then clears
        /// forceRenderingOff immediately. Culling is pointless for one stationary bot a few metres away.
        /// </para>
        /// </summary>
        internal static void DisableBotCulling(Player botPlayer)
        {
            if (botPlayer == null)
            {
                return;
            }

            try
            {
                // LocalPlayer.botPlayerCulling is private; GetMemberValue walks the hierarchy for us.
                if (!(GetMemberValue(botPlayer, "botPlayerCulling") is OfflinePlayerCulling culling))
                {
                    Plugin.LogSource.LogWarning("Could not resolve LocalPlayer.botPlayerCulling; bot body may stay invisible.");
                    return;
                }

                culling.Disable();
                culling.ApplyVisibleState();
                LogSpawnDiagnostic($"Disabled offline player culling for bot (mode={culling.Mode}, isVisible={culling.IsVisible}).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"DisableBotCulling failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Dumps the bot's renderer state. Called both at activation and a couple of seconds later:
        /// the meshes are correct and enabled at activation, so if the model is still invisible then
        /// something is turning them off afterwards, and comparing the two dumps shows which.
        /// Note <c>Renderer.isVisible</c> reflects the last rendered frame, so it is meaningless in the
        /// activation dump (the bot has not been drawn yet) and only meaningful in the later one.
        /// </summary>
        internal static void LogBotRendererState(Player botPlayer, string phase)
        {
            if (Settings.EnableRendererDiagnostics?.Value != true)
            {
                return;
            }

            if (botPlayer == null || botPlayer.gameObject == null)
            {
                Plugin.LogSource.LogWarning($"Bot renderer state ({phase}): player is null/destroyed.");
                return;
            }

            Transform root = botPlayer.gameObject.transform;
            Plugin.LogSource.LogInfo(
                $"Bot renderer state ({phase}): pos={root.position}, scale={root.lossyScale}, " +
                $"activeInHierarchy={botPlayer.gameObject.activeInHierarchy}, " +
                $"rootLayer={root.gameObject.layer} ({LayerMask.LayerToName(root.gameObject.layer)})");

            foreach (Renderer renderer in botPlayer.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !(renderer is SkinnedMeshRenderer))
                {
                    continue;
                }

                Plugin.LogSource.LogInfo(
                    $"  '{renderer.name}' enabled={renderer.enabled} isVisible={renderer.isVisible} " +
                    $"shadowCasting={renderer.shadowCastingMode} forceRenderingOff={renderer.forceRenderingOff} " +
                    $"boundsCenter={renderer.bounds.center} " +
                    $"material={(renderer.sharedMaterial == null ? "NULL" : renderer.sharedMaterial.name)}");
            }

            LODGroup lodGroup = botPlayer.GetComponentInChildren<LODGroup>(true);
            Plugin.LogSource.LogInfo(
                $"  LODGroup={(lodGroup == null ? "none" : $"enabled={lodGroup.enabled} size={lodGroup.size} lodCount={lodGroup.lodCount}")}");

            Camera mainCamera = Camera.main;
            if (mainCamera != null)
            {
                bool layerRendered = (mainCamera.cullingMask & (1 << root.gameObject.layer)) != 0;
                Plugin.LogSource.LogInfo(
                    $"  camera='{mainCamera.name}' rendersBotLayer={layerRendered} " +
                    $"camPos={mainCamera.transform.position} distanceToBot={Vector3.Distance(mainCamera.transform.position, root.position):F2}");
            }
        }

        private static void EnsureAICoversDataRuntimeDefaults()
        {
            try
            {
                AICoversData coversData = UnityEngine.Object.FindObjectOfType<AICoversData>();
                if (coversData == null)
                {
                    // BotsController.Init internally calls AICoversData.CreateOrFind(true)
                    // which provisions all holder GameObjects, but creates a fresh AIVoxelesData
                    // whose VoxelsList field is null. RestoreData then crashes on VoxelsList.Count(...).
                    // Pre-call CreateOrFind ourselves so we can initialize VoxelsList before Init runs.
                    coversData = AICoversData.CreateOrFind(true);
                    LogSpawnDiagnostic("Pre-created AICoversData via CreateOrFind for hideout runtime.");
                }

                coversData.Points ??= new List<GroupPoint>();
                coversData.Ways ??= new List<GroupPointWay>();

                if (coversData.Voxels == null)
                {
                    AIVoxelesData voxels = UnityEngine.Object.FindObjectOfType<AIVoxelesData>();
                    if (voxels == null)
                    {
                        GameObject voxelsGo = new GameObject("AIVoxelesData");
                        voxels = voxelsGo.AddComponent<AIVoxelesData>();
                    }
                    coversData.Voxels = voxels;
                }

                // Critical: VoxelsList is a bare field with no initializer; AIVoxelesData.RestoreData
                // immediately calls VoxelsList.Count(...) which throws ArgumentNullException if null.
                coversData.Voxels.VoxelsList ??= new List<NavGraphVoxelSimple>();

                // Critical: BotOwner.PreActivate builds a BotGameEventsData, whose KhorovodBotGameEvent
                // constructor enumerates BotsController.CoversData.AIPlaceInfoHolder.Places looking for a
                // ChristmasTreePoI. Raid AI setup provisions that holder; nothing does in the hideout, so
                // it is null and PreActivate throws NullReferenceException - which aborts
                // BotCreatorClient.method_3 before it can call SwitchBotVisual(bot, true) or the callback
                // that raises BotSpawner.OnBotCreated. That is why the bot spawned invisible and was never
                // reported. An empty Places list is correct here: the loop simply finds no tree and the
                // event stays inactive.
                if (coversData.AIPlaceInfoHolder == null)
                {
                    AIPlaceInfoHolder placeInfoHolder = UnityEngine.Object.FindObjectOfType<AIPlaceInfoHolder>();
                    if (placeInfoHolder == null)
                    {
                        GameObject placesGo = new GameObject("AIPlaceInfoHolder");
                        placeInfoHolder = placesGo.AddComponent<AIPlaceInfoHolder>();
                    }

                    coversData.AIPlaceInfoHolder = placeInfoHolder;
                    LogSpawnDiagnostic("Provisioned AIPlaceInfoHolder for hideout runtime.");
                }

                coversData.AIPlaceInfoHolder.Places ??= new List<AIPlaceInfo>();

                Vector3 max = coversData.Voxels.MaxVoxelesValues;
                if (max.x <= 0f || max.y <= 0f || max.z <= 0f)
                {
                    coversData.Voxels.SetMaxValues(new Vector3(1f, 1f, 1f));
                }

                coversData.Patrols ??= UnityEngine.Object.FindObjectOfType<AIPatrolsData>();
                if (coversData.Patrols == null)
                {
                    GameObject patrolsGo = new GameObject("AIPatrolsData");
                    coversData.Patrols = patrolsGo.AddComponent<AIPatrolsData>();
                }

                coversData.Patrols.ContainerLootPoints ??= new List<AILootPoint>();
                coversData.Patrols.SimpleLootPoints ??= new List<AILootPoint>();
                coversData.Patrols.ExfiltrationPoints ??= new List<AIExfiltrationPoint>();
                coversData.Patrols.LootPointClusters ??= new List<AILootPointsCluster>();

                coversData.AIMinesPositions ??= UnityEngine.Object.FindObjectOfType<AIMinesPositionsHolder>();
                if (coversData.AIMinesPositions == null)
                {
                    GameObject minesGo = new GameObject("AIMinesPositionsHolder");
                    coversData.AIMinesPositions = minesGo.AddComponent<AIMinesPositionsHolder>();
                }

                coversData.AIDangerPlacesHolder ??= UnityEngine.Object.FindObjectOfType<AIDangerPlacesHolder>();
                if (coversData.AIDangerPlacesHolder == null)
                {
                    GameObject dangerGo = new GameObject("AIDangerPlacesHolder");
                    coversData.AIDangerPlacesHolder = dangerGo.AddComponent<AIDangerPlacesHolder>();
                }

                coversData.AIManualPointsHolder ??= UnityEngine.Object.FindObjectOfType<AIManualPointsHolder>();
                if (coversData.AIManualPointsHolder == null)
                {
                    GameObject manualGo = new GameObject("AIManualPointsHolder");
                    coversData.AIManualPointsHolder = manualGo.AddComponent<AIManualPointsHolder>();
                }

                coversData.AIManualPointsHolder.ManualPoints ??= new List<GroupPoint>();

                // Ensure AICoversData.AICorePointsHolder is wired. AICoversData.CreateOrFind only
                // assigns the holder when it constructs a fresh AICoversData (see AICoversData.cs
                // L92), so on a subsequent FindObjectOfType-hit-then-reuse path the field can be
                // null. BotCreatorClient/CG_CreateBot.MoveNext dereferences AICorePointsHolder
                // unconditionally, so a null here is the activation chain's silent NRE.
                if (coversData.AICorePointsHolder == null)
                {
                    AICorePointHolder existingHolder = UnityEngine.Object.FindObjectOfType<AICorePointHolder>();
                    if (existingHolder == null)
                    {
                        GameObject holderGo = new GameObject("AICorePointHolder");
                        existingHolder = holderGo.AddComponent<AICorePointHolder>();
                    }
                    existingHolder.CorePoints ??= new List<AICorePoint>();
                    coversData.AICorePointsHolder = existingHolder;
                    LogSpawnDiagnostic("Wired AICoversData.AICorePointsHolder for hideout runtime.");
                }
                else if (coversData.AICorePointsHolder.CorePoints == null)
                {
                    coversData.AICorePointsHolder.CorePoints = new List<AICorePoint>();
                }

                // BotsController.Init line 178-179 calls
                //   this.StationaryWeapons = FindObjectsProxy.FindUnityObjectOfType<AIStationaryController>();
                //   this.StationaryWeapons.Init(this.CoversData_1.AICorePointsHolder);
                // In hideout there is no AIStationaryController in scene -> NRE on .Init(...).
                // Pre-create one with empty Weapons[] so Init() is a no-op pass-through.
                AIStationaryController stationary = UnityEngine.Object.FindObjectOfType<AIStationaryController>();
                if (stationary == null)
                {
                    GameObject stationaryGo = new GameObject("AIStationaryController");
                    stationary = stationaryGo.AddComponent<AIStationaryController>();
                    LogSpawnDiagnostic("Created synthetic AIStationaryController for hideout runtime.");
                }

                Plugin.LogSource.LogDebug("Ensured runtime defaults for AICoversData/Voxels before direct BotsController.Init fallback.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogDebug($"Failed to ensure AICoversData runtime defaults: {ex.Message}");
            }
        }

        private static void EnsureSyntheticBotZoneForOwner(HideoutPlayerOwner owner)
        {
            if (owner?.Player?.Transform == null)
            {
                return;
            }

            Vector3 spawnPosition = owner.Player.Transform.position + owner.Player.Transform.forward * 2.5f;
            float spawnRotation = owner.Player.Transform.eulerAngles.y;

            if (_syntheticBotZone == null)
            {
                GameObject zoneObject = new GameObject(_syntheticBotZoneName);
                _syntheticBotZone = zoneObject.AddComponent<BotZone>();
                _syntheticBotZone.SpawnPointMarkers = new List<SpawnPointMarker>();
                _syntheticBotZone.UnSpawnPoints = Array.Empty<UnspawnPoint>();
                _syntheticBotZone.transform.position = spawnPosition;

                EnsureSyntheticCoreAndPatrolDependencies(spawnPosition, zoneObject.transform);
                _syntheticBotZone.PatrolWays = _syntheticPatrolWay != null
                    ? new[] { _syntheticPatrolWay }
                    : Array.Empty<PatrolWay>();

                LogSpawnDiagnostic("Created synthetic BotZone for hideout runtime spawning.");
            }

            if (_syntheticSpawnPointMarker == null)
            {
                SpawnPointParams spawnParams = new SpawnPointParams
                {
                    Id = "hideoutshootout_runtime_spawnpoint",
                    Position = spawnPosition,
                    Rotation = spawnRotation,
                    Sides = EPlayerSideMask.Savage,
                    Categories = ESpawnCategoryMask.Bot,
                    Infiltration = "hideoutshootout_runtime",
                    BotZoneName = _syntheticBotZone.name,
                    DelayToCanSpawnSec = 0f,
                    CorePointId = 1,
                    ColliderParams = new SpawnSphereParams
                    {
                        Center = Vector3.zero,
                        Radius = 1f
                    }
                };

                _syntheticSpawnPointMarker = SpawnPointMarker.Create(spawnParams, _syntheticBotZone.transform);
                EnsureSpawnPointMarkerCollider(_syntheticSpawnPointMarker);
                if (_syntheticBotZone.SpawnPointMarkers == null)
                {
                    _syntheticBotZone.SpawnPointMarkers = new List<SpawnPointMarker>();
                }

                if (!_syntheticBotZone.SpawnPointMarkers.Contains(_syntheticSpawnPointMarker))
                {
                    _syntheticBotZone.SpawnPointMarkers.Add(_syntheticSpawnPointMarker);
                }

                LogSpawnDiagnostic("Created synthetic spawn point marker for hideout runtime zone.");
            }
            else
            {
                _syntheticSpawnPointMarker.transform.position = spawnPosition;
                _syntheticSpawnPointMarker.transform.rotation = Quaternion.Euler(0f, spawnRotation, 0f);
                _syntheticSpawnPointMarker.BotZone = _syntheticBotZone;
                EnsureSpawnPointMarkerCollider(_syntheticSpawnPointMarker);
            }

            _syntheticBotZone.CenterOfSpawnPoints = spawnPosition;
        }

        // SpawnPointMarker.Create relies on SpawnPointParams.ColliderParams to add a Collider component
        // via SpawnPointParamsExtension.AddSpawnCollider. If that step is skipped (or runs before SpawnPointMarker.Start),
        // SpawnPointMarker._collider stays null and the spawn point fails SpawnPointExtension.IsValid because
        // it logs "spawnPoint.Collider is null" and returns false. SpawnSystem also calls
        // ISpawnPointCollider.Contains during validation, which logs "_collider == null" repeatedly.
        // We force the GameObject to carry a SphereCollider, mirror it into the private _collider
        // field via reflection, and assign the SpawnPoint.Collider so validation succeeds.
        private static void EnsureSpawnPointMarkerCollider(SpawnPointMarker marker)
        {
            if (marker == null)
            {
                return;
            }

            try
            {
                Collider collider = marker.GetComponent<Collider>();
                if (collider == null)
                {
                    SphereCollider sphereCollider = marker.gameObject.AddComponent<SphereCollider>();
                    sphereCollider.isTrigger = true;
                    sphereCollider.center = Vector3.zero;
                    sphereCollider.radius = 1f;
                    collider = sphereCollider;
                }

                FieldInfo colliderField = AccessTools.Field(typeof(SpawnPointMarker), "_collider");
                colliderField?.SetValue(marker, collider);

                ISpawnPoint spawnPoint = marker.SpawnPoint;
                if (spawnPoint != null && spawnPoint is SpawnPoint concreteSpawnPoint)
                {
                    concreteSpawnPoint.Collider = marker;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Failed to attach collider to synthetic spawn point marker: {ex.Message}");
            }
        }

        private static void EnsureSyntheticCoreAndPatrolDependencies(Vector3 position, Transform zoneParent)
        {
            // Prefer the holder already wired into AICoversData (set up by
            // EnsureAICoversDataRuntimeDefaults / AICoversData.CreateOrFind). CG_CreateBot.MoveNext
            // resolves the core point through that exact field, so adding our synthetic point to a
            // different orphan holder makes it invisible to activation.
            AICorePointHolder coreHolder = null;
            AICoversData coversData = UnityEngine.Object.FindObjectOfType<AICoversData>();
            if (coversData != null && coversData.AICorePointsHolder != null)
            {
                coreHolder = coversData.AICorePointsHolder;
            }

            if (coreHolder == null)
            {
                coreHolder = UnityEngine.Object.FindObjectOfType<AICorePointHolder>();
            }

            if (coreHolder == null)
            {
                GameObject coreHolderGo = new GameObject("AICorePointHolder");
                coreHolder = coreHolderGo.AddComponent<AICorePointHolder>();
            }

            if (coversData != null && coversData.AICorePointsHolder == null)
            {
                coversData.AICorePointsHolder = coreHolder;
            }

            coreHolder.CorePoints ??= new List<AICorePoint>();

            if (_syntheticCorePoint == null)
            {
                GameObject corePointGo = new GameObject("HideoutShootout_RuntimeCorePoint");
                corePointGo.transform.position = position;
                _syntheticCorePoint = corePointGo.AddComponent<AICorePoint>();
                // CRITICAL: AICorePoint.Id MUST equal SpawnPointParams.CorePointId (==1).
                // BotCreatorClient/CG_CreateBot.MoveNext (the player materialization async state
                // machine) calls IbotGame_0.BotsController.CoversData.AICorePointsHolder
                //   .GetCorePoint(bornInfo.CorePointId)
                // and passes the result into BotOwner.Create(...) as the StartCorePoint argument.
                // GetCorePoint searches CorePoints by Id; if no match it returns null and the
                // downstream BotOwner.Brain/Group construction NREs and is silently swallowed by
                // Task.HandleExceptions() on the .SpawnBotsInZoneOnPositions(...) caller. Using id=1
                // here aligns AICorePoint.Id with the spawn point's CorePointId.
                _syntheticCorePoint.SetIds(1, 1);

                if (!coreHolder.CorePoints.Contains(_syntheticCorePoint))
                {
                    coreHolder.AddCorePoint(_syntheticCorePoint);
                }
            }
            else
            {
                _syntheticCorePoint.transform.position = position;
                if (!coreHolder.CorePoints.Contains(_syntheticCorePoint))
                {
                    coreHolder.AddCorePoint(_syntheticCorePoint);
                }
            }

            if (_syntheticPatrolWay == null)
            {
                GameObject wayGo = new GameObject("HideoutShootout_RuntimePatrolWay");
                wayGo.transform.SetParent(zoneParent, false);
                _syntheticPatrolWay = wayGo.AddComponent<PatrolWay>();
                _syntheticPatrolWay.Points = new List<PatrolPoint>();

                GameObject pointGo = new GameObject("HideoutShootout_RuntimePatrolPoint");
                pointGo.transform.SetParent(wayGo.transform, false);
                pointGo.transform.position = position;
                _syntheticPatrolPoint = pointGo.AddComponent<PatrolPoint>();
                _syntheticPatrolPoint.Id = 900002;
                _syntheticPatrolPoint.SetCorePoint(_syntheticCorePoint);

                _syntheticPatrolWay.Points.Add(_syntheticPatrolPoint);
                _syntheticPatrolWay.InitPoints();
            }
            else
            {
                if (_syntheticPatrolPoint != null)
                {
                    _syntheticPatrolPoint.transform.position = position;
                    _syntheticPatrolPoint.SetCorePoint(_syntheticCorePoint);
                }

                _syntheticPatrolWay.InitPoints();
            }
        }

        private static string ResolveOpenZones(AbstractGame game)
        {
            try
            {
                object locationObj = GetMemberValue(game, "Location", "_location");

                if (locationObj == null)
                {
                    return null;
                }

                return GetMemberValue(locationObj, "OpenZones", "openZones") as string;
            }
            catch
            {
                return null;
            }
        }

        private sealed class HideoutBotGameAdapter : IBotGame
        {
            private readonly AbstractGame _game;
            private readonly BotsController _botsController;

            public HideoutBotGameAdapter(AbstractGame game, BotsController botsController)
            {
                _game = game;
                _botsController = botsController;
            }

            public GameStatus Status => _game != null ? _game.Status : GameStatus.Started;

            public GameDateTime GameDateTime
            {
                get
                {
                    object value = AccessTools.Property(_game?.GetType(), "GameDateTime")?.GetValue(_game);
                    return value as GameDateTime;
                }
            }

            public BotsController BotsController => _botsController;

            public IWeatherCurve WeatherCurve => WeatherController.Instance?.WeatherCurve;

            public BossSpawnScenario BossSpawnScenario => null;

            public event Action UpdateByUnity
            {
                add { }
                remove { }
            }

            public void BotDespawn(BotOwner bot)
            {
            }
        }

        /// <summary>
        /// Lazily builds (and caches) the real <see cref="IBotCreator"/> pipeline used by raid:
        /// <c>BotProfileClient : ABotProfileCreator : IBotProfileCreator</c> wrapped in <c>BotCreatorClient : IBotCreator</c>.
        /// <para>
        /// Why we need this: in hideout, <c>BaseLocalGame.Run</c> never runs, so the wave-loading
        /// path that constructs <c>BotProfileClient</c> + <c>BotCreatorClient</c> is skipped, and our
        /// reflective <c>BotsController.Init</c> bootstrap injects a <see cref="NoopBotCreator"/>
        /// stub. That stub's <c>GenerateProfile</c> returns null, which makes
        /// <c>BotCreationData.BotCreationData/CG_Create.MoveNext</c> bail out with zero profiles before any
        /// activation work runs. Building the real creator here gives us a working
        /// <c>GenerateProfile</c> that calls <c>BotProfileClient.CreateProfile</c>, which in turn
        /// queries SPT's existing <c>/client/game/bot/generate</c> endpoint. No new server module
        /// is required.
        /// </para>
        /// </summary>
        private static IBotCreator EnsureRealBotCreator(BotSpawner spawner)
        {
            if (_realBotCreator != null)
            {
                return _realBotCreator;
            }

            IBotGame ibotGame = spawner?.BotGame;
            if (ibotGame == null && Singleton<IBotGame>.Instantiated)
            {
                ibotGame = Singleton<IBotGame>.Instance;
            }

            return EnsureRealBotCreator(ibotGame);
        }

        private static IBotCreator EnsureRealBotCreator(IBotGame ibotGame)
        {
            if (_realBotCreator != null)
            {
                return _realBotCreator;
            }

            try
            {
                if (ibotGame == null)
                {
                    Plugin.LogSource.LogWarning("Cannot build real IBotCreator: IBotGame is unavailable.");
                    return null;
                }

                IEftSession session = TryGetBackEndSession();
                if (session == null)
                {
                    Plugin.LogSource.LogWarning("Cannot build real IBotCreator: IEftSession is unavailable.");
                    return null;
                }

                BotProfileClient profileCreator = new BotProfileClient(
                    session,
                    Array.Empty<SpawnWave>(),
                    Array.Empty<BossLocationSpawn>(),
                    null,
                    false);

                Func<GameWorld, Profile, Vector3, Task<LocalPlayer>> playerFactory = CreateBotLocalPlayerAsync;

                _realBotCreator = new BotCreatorClient(ibotGame, profileCreator, playerFactory);
                LogSpawnDiagnostic("Built real BotProfileClient-backed BotCreatorClient for hideout spawning.");
                return _realBotCreator;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Failed to build real IBotCreator: {ex.Message}");
                Plugin.LogSource.LogDebug(ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Resolves <see cref="IEftSession"/> via the canonical SPT pattern
        /// (<c>ClientAppUtils.GetMainApp().GetClientBackEndSession()</c>) used throughout
        /// SPT.Reflection / SPT.SinglePlayer / SPT.Debugging. The session backs profile generation
        /// requests from <see cref="BotProfileClient.CreateProfile"/>.
        /// </summary>
        private static IEftSession TryGetBackEndSession()
        {
            try
            {
                return ClientAppUtils.GetMainApp()?.GetClientBackEndSession();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogDebug($"TryGetBackEndSession failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Loads everything the bot needs to render into the Raid asset pools, so
        /// <c>PoolManagerClass.CreatePlayerObject</c> has a populated <c>PlayerAssetPool</c> to pop
        /// from. Argument shape is taken from <c>BotSpawner.SpawnAndActivateNowDebugClient</c>.
        /// <para>
        /// Two separate sets are required. <c>Profile.GetAllPrefabPaths</c> covers the profile's
        /// customization and gear, but not the base character rig or its animator - in a raid those
        /// arrive via <c>InGameBundles.BUNDLES_TO_PRELOAD</c> during <c>GameWorld.InitLevel</c>, which
        /// never runs in the hideout. Without <c>PLAYER_BUNDLE_NAME</c> there is no skeleton for the
        /// body parts to attach to, and without <c>PLAYER_DEFAULT_ANIMATOR_CONTROLLER</c> the animator
        /// has no layers - which is what produces "LayersDefaultStates.Length N != _animator.layerCount 0".
        /// </para>
        /// <para>
        /// PORTING NOTE (SPT 4.0.13): <c>PoolManagerClass</c> is the obfuscated name this singleton
        /// carries on the older client SPT 4.0.13 ships; <c>ObjectsFactory</c> (this mod's original
        /// SPT 4.1 target) is 4.1's deobfuscated rename of the same class, per SPT's official 4.0-to-
        /// 4.1 client migration notes. The nested <c>PoolsCategory</c>/<c>AssemblyType</c> enums and
        /// the method names below are NOT verified against a real 4.0.13 client; if this fails to
        /// compile, the compiler error will name the exact member that needs remapping.
        /// </para>
        /// </summary>
        private static async Task PreloadProfileBundlesAsync(Profile profile)
        {
            if (!Singleton<PoolManagerClass>.Instantiated)
            {
                Plugin.LogSource.LogWarning("PoolManagerClass singleton is unavailable; bot prefabs cannot be preloaded and the bot will spawn without a model.");
                return;
            }

            bool useSimpleAnimator = profile.Info?.Settings?.UseSimpleAnimator ?? false;

            // The base character rig, animator controller, animation clips and root motion table.
            // LocalPlayer.Create picks the zombie variants when the profile uses the simple animator,
            // so preload the matching set.
            List<ResourceKey> resources = useSimpleAnimator
                ? new List<ResourceKey>
                {
                    InGameBundles.ZOMBIE_BUNDLE_NAME,
                    InGameBundles.ZOMBIE_ANIMATOR_CONTROLLER,
                    InGameBundles.ZOMBIE_ANIMATION_CLIPS_KEEPER,
                    InGameBundles.ZOMBIE_ROOTMOTION_TABLE,
                }
                : new List<ResourceKey>
                {
                    InGameBundles.PLAYER_BUNDLE_NAME,
                    InGameBundles.PLAYER_DEFAULT_ANIMATOR_CONTROLLER,
                    InGameBundles.PLAYER_ANIMATION_CLIPS_KEEPER,
                    InGameBundles.PLAYER_ROOTMOTION_TABLE,
                };

            int characterKeyCount = resources.Count;
            resources.AddRange(profile.GetAllPrefabPaths(false));

            ResourceKey[] prefabPaths = resources.Where(key => key != null).Distinct().ToArray();
            if (prefabPaths.Length == 0)
            {
                Plugin.LogSource.LogWarning($"Profile {profile.Id} reported no prefab paths to preload.");
                return;
            }

            // No ConfigureAwait(false) anywhere on this path: Unity's SynchronizationContext is the
            // main thread, and everything that resumes after these awaits (LocalPlayer.Create, and
            // BotOwner.Create further up the chain) is Unity API work that must run there. Resuming
            // on a thread-pool thread crashes the process.
            await Singleton<PoolManagerClass>.Instance.LoadBundlesAndCreatePools(
                PoolManagerClass.PoolsCategory.Raid,
                PoolManagerClass.AssemblyType.Local,
                prefabPaths,
                JobYieldPriority.General,
                null,
                default);

            LogSpawnDiagnostic($"Preloaded {prefabPaths.Length} prefab bundles for hideout bot profile {profile.Id} ({characterKeyCount} character rig/animator, simpleAnimator={useSimpleAnimator}).");
        }

        /// <summary>
        /// Builds the bot's <see cref="LocalPlayer"/> for a hideout spawn, mirroring
        /// <c>EFT.LocalGame.CreateBotByProfile</c> - the factory the raid game itself hands to
        /// <c>BotCreatorClient</c>. Every argument value below is taken from that method's IL, so the
        /// bot is constructed exactly the way the game constructs its own.
        /// <para>
        /// This used to hand-roll the construction (Player.Create&lt;T&gt; + Player.Init, then wiring up
        /// the inventory controller, health controller, empty hands and AIData by reflection) purely to
        /// dodge SPT's DisableDevMaskCheckPatch transpiler on <c>LocalPlayer/CG_Create.MoveNext</c>,
        /// which double-completed the task when invoked outside a raid. SPT 4.1 no longer ships that
        /// patch, so calling <c>LocalPlayer.Create</c> directly is both correct and far less fragile:
        /// it performs every step the manual path had to replicate.
        /// </para>
        /// <para>
        /// PORTING NOTE (SPT 4.0.13): unverified whether 4.0.13's SPT client mod still ships
        /// DisableDevMaskCheckPatch. If a hideout scav spawn hangs or the bot never activates on
        /// 4.0.13, that transpiler double-completing this task is the most likely cause - check the
        /// BepInEx log for a task-already-completed / InvalidOperationException around
        /// LocalPlayer.Create when EnableSpawnDiagnostics is on.
        /// </para>
        /// </summary>
        private static async Task<LocalPlayer> CreateBotLocalPlayerAsync(GameWorld gameWorld, Profile profile, Vector3 position)
        {
            try
            {
                if (gameWorld == null)
                {
                    Plugin.LogSource.LogError("CreateBotLocalPlayerAsync called with null GameWorld; LocalPlayer construction requires a live GameWorld.");
                    return null;
                }

                if (profile == null)
                {
                    Plugin.LogSource.LogError("CreateBotLocalPlayerAsync called with null Profile; profile generation must have failed silently upstream.");
                    return null;
                }

                LogLocalPlayerCreatePatchOwnersOnce();

                int playerId = UnityEngine.Random.Range(100000, int.MaxValue);

                // LocalGame.CreateBotByProfile marks scavs as spawned-in-session before creating them.
                profile.SetSpawnedInSession(profile.Info.Side == EPlayerSide.Savage);

                // Load the profile's prefabs into the Raid pool before creating the player.
                // Player.Create pops the body from PoolManagerClass.GetPools(Raid).PlayerAssetPool via
                // PoolManagerClass.CreatePlayerObject; in a raid GameWorld.InitLevel fills that pool, but
                // nothing does so in the hideout. Without this the bot spawns with no skeleton or
                // animator - gear renders, the character model does not, and the weapon's
                // FirearmsAnimator then NREs against an animator with layerCount 0.
                // Mirrors BotSpawner.SpawnAndActivateNowDebugClient, the game's own debug spawn path.
                await PreloadProfileBundlesAsync(profile);

                LocalPlayer player = await LocalPlayer.Create(
                    gameWorld,
                    playerId,
                    position,
                    Quaternion.identity,
                    "Player",
                    string.Empty,
                    EPointOfView.ThirdPerson,
                    profile,
                    /*aiControl*/ true,
                    EUpdateQueue.Update,
                    Player.EUpdateMode.Auto,
                    Player.EUpdateMode.Auto,
                    AppEnvironment.Config.CharacterController.BotPlayerMode,
                    () => 1f,
                    () => 1f,
                    new DumbStatisticsManager(),
                    ThirdPersonCustomizationFilter.Default,
                    /*session*/ null,
                    ELocalMode.TRAINING,
                    /*isYourPlayer*/ false,
                    /*isBot*/ true);

                if (player == null)
                {
                    Plugin.LogSource.LogWarning($"LocalPlayer.Create returned null for hideout bot profile {profile.Id} at {position}.");
                    return null;
                }

                LogSpawnDiagnostic($"LocalPlayer.Create succeeded for hideout bot profile {profile.Id} (playerId={playerId}) at {position}.");
                return player;
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException ?? ex;
                Plugin.LogSource.LogError($"CreateBotLocalPlayerAsync threw: {inner.GetType().Name}: {inner.Message}");
                // Full trace at Error so it is visible without enabling debug logging.
                Plugin.LogSource.LogError(inner.ToString());
                throw;
            }
        }

        private static void LogLocalPlayerCreatePatchOwnersOnce()
        {
            if (Settings.EnableHarmonyPatchDiagnostics?.Value != true || _harmonyLocalPlayerCreateLogged)
            {
                return;
            }
            _harmonyLocalPlayerCreateLogged = true;

            try
            {
                MethodInfo createMethod = typeof(LocalPlayer)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 21);
                LogPatchOwners("LocalPlayer.Create(21 args)", createMethod);

                Type createStateMachine = typeof(LocalPlayer).GetNestedType("CG_Create", BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo moveNext = createStateMachine?.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                LogPatchOwners("LocalPlayer+CG_Create.MoveNext", moveNext);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogDebug($"LogLocalPlayerCreatePatchOwnersOnce failed: {ex.Message}");
            }
        }

        private static void LogPatchOwners(string label, MethodInfo target)
        {
            if (target == null)
            {
                Plugin.LogSource.LogDebug($"Harmony patch info: {label} -> target method not found.");
                return;
            }

            HarmonyLib.Patches info = Harmony.GetPatchInfo(target);
            if (info == null)
            {
                Plugin.LogSource.LogInfo($"Harmony patch info: {label} -> no patches attached.");
                return;
            }

            string Owners(IEnumerable<HarmonyLib.Patch> patches) => patches == null ? "(none)" : string.Join(",", patches.Select(p => p.owner));

            Plugin.LogSource.LogInfo(
                $"Harmony patch info: {label} -> prefixes=[{Owners(info.Prefixes)}] postfixes=[{Owners(info.Postfixes)}] transpilers=[{Owners(info.Transpilers)}] finalizers=[{Owners(info.Finalizers)}]");
        }

        // PORTING NOTE (SPT 4.0.13): IBotCreator's real signature on this client build uses
        // BotCreationDataClass (this mod's original SPT 4.1 target called it BotCreationData) and
        // two still-obfuscated parameter types - GClass682 for the position/spawn-note argument
        // and GClass406 for the backup-profiles cache argument. Confirmed by reflecting the
        // installed Assembly-CSharp.dll's IBotCreator interface directly; every member below is a
        // no-op regardless, so the exact identity of GClass682/GClass406 doesn't matter here.
        private sealed class NoopBotCreator : IBotCreator
        {
            public int BotsLoading => 0;
            public bool StartProfilesLoaded => true;
            public int BundlesLoading => 0;

            public Task<Profile> GenerateProfile(BotCreationDataClass data, System.Threading.CancellationToken cancellationToken, bool withDelete)
            {
                return Task.FromResult<Profile>(null);
            }

            public Task ActivateBot(BotCreationDataClass data, BotZone zone, bool shallBeGroup, Func<BotOwner, BotZone, BotsGroup> groupAction, Action<BotOwner> callback, System.Threading.CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task ActivateBot(Profile profile, GClass682 position, BotZone zone, bool shallBeGroup, Func<BotOwner, BotZone, BotsGroup> groupAction, Action<BotOwner> callback, System.Threading.CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public void FillBackupProfilesData(GClass406 resultCache)
            {
            }

            public void AddToTargetBackup(BotDifficulty difficulty, WildSpawnType role, int count)
            {
            }
        }

        private static object TryCreateSpawnSystem(Type spawnSystemType, AbstractGame game, BotsController botsController)
        {
            try
            {
                Type creatorType = spawnSystemType.Assembly.GetType("EFT.Game.Spawning.SpawnSystemFactory")
                    ?? AccessTools.TypeByName("EFT.Game.Spawning.SpawnSystemFactory");

                if (creatorType == null)
                {
                    Plugin.LogSource.LogDebug("EFT.Game.Spawning.SpawnSystemFactory type was not found.");
                    return null;
                }

                MethodInfo createMethod = creatorType
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                        m.Name == "CreateSpawnSystem" &&
                        spawnSystemType.IsAssignableFrom(m.ReturnType) &&
                        m.GetParameters().Length == 3);

                if (createMethod == null)
                {
                    Plugin.LogSource.LogDebug("No compatible CreateSpawnSystem(players,zones,spawnPoints) overload found.");
                    return null;
                }

                ParameterInfo[] parameters = createMethod.GetParameters();
                object[] args = new object[3];
                for (int i = 0; i < 3; i++)
                {
                    Type requiredType = parameters[i].ParameterType;
                    args[i] = TryResolveMemberByTypeDeep(game, requiredType, 3)
                        ?? (Singleton<GameWorld>.Instantiated ? TryResolveMemberByTypeDeep(Singleton<GameWorld>.Instance, requiredType, 3) : null)
                        ?? TryResolveMemberByTypeDeep(botsController, requiredType, 2);

                    if (args[i] == null && requiredType.Name.Contains("ISpawnPoints"))
                    {
                        args[i] = TryCreateSpawnPoints(requiredType);
                    }

                    if (args[i] == null)
                    {
                        Plugin.LogSource.LogWarning($"Could not resolve SpawnSystemCreator argument {i} ({requiredType.Name}).");
                        return null;
                    }
                }

                object created = createMethod.Invoke(null, args);
                if (created != null)
                {
                    LogSpawnDiagnostic($"Created ISpawnSystem via EFT.Game.Spawning.SpawnSystemFactory with args: {args[0].GetType().Name}, {args[1].GetType().Name}, {args[2].GetType().Name}");
                }

                return created;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Failed to create spawn system via EFT.Game.Spawning.SpawnSystemFactory: {ex.Message}");
                return null;
            }
        }

        private static object TryCreateSpawnPoints(Type spawnPointsType)
        {
            try
            {
                Type managerType = spawnPointsType.Assembly.GetType("EFT.Game.Spawning.SpawnPointsCollection")
                    ?? AccessTools.TypeByName("EFT.Game.Spawning.SpawnPointsCollection");

                if (managerType == null)
                {
                    Plugin.LogSource.LogDebug("EFT.Game.Spawning.SpawnPointsCollection type was not found.");
                    return null;
                }

                MethodInfo createFromScene = managerType.GetMethod("CreateFromScene", BindingFlags.Public | BindingFlags.Static);
                if (createFromScene == null)
                {
                    Plugin.LogSource.LogDebug("EFT.Game.Spawning.SpawnPointsCollection.CreateFromScene was not found.");
                    return null;
                }

                object created = createFromScene.Invoke(null, new object[] { null, null });
                if (created != null)
                {
                    LogSpawnDiagnostic($"Created ISpawnPoints via EFT.Game.Spawning.SpawnPointsCollection.CreateFromScene: {created.GetType().Name}");
                }

                return created;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Failed to create ISpawnPoints via EFT.Game.Spawning.SpawnPointsCollection: {ex.Message}");
                return null;
            }
        }

        private static object ResolveSpawnSystemDeep(Type spawnSystemType)
        {
            try
            {
                List<object> roots = new List<object>();

                if (Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance != null)
                {
                    roots.Add(Singleton<AbstractGame>.Instance);
                }

                if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance != null)
                {
                    roots.Add(Singleton<GameWorld>.Instance);
                }

                foreach (object root in roots)
                {
                    object match = TryResolveMemberByTypeDeep(root, spawnSystemType, 2);
                    if (match != null)
                    {
                        return match;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogDebug($"Deep spawn system resolve failed: {ex.Message}");
            }

            return null;
        }

        private static object TryResolveMemberByTypeDeep(object target, Type requiredType, int depth)
        {
            if (target == null || requiredType == null || depth < 0)
            {
                return null;
            }

            object direct = TryResolveMemberByType(target, requiredType);
            if (direct != null)
            {
                return direct;
            }

            if (depth == 0)
            {
                return null;
            }

            Type type = target.GetType();

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object value;
                try
                {
                    value = property.GetValue(target);
                }
                catch
                {
                    continue;
                }

                if (value == null || ReferenceEquals(value, target) || value is string)
                {
                    continue;
                }

                object nested = TryResolveMemberByTypeDeep(value, requiredType, depth - 1);
                if (nested != null)
                {
                    return nested;
                }
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object value;
                try
                {
                    value = field.GetValue(target);
                }
                catch
                {
                    continue;
                }

                if (value == null || ReferenceEquals(value, target) || value is string)
                {
                    continue;
                }

                object nested = TryResolveMemberByTypeDeep(value, requiredType, depth - 1);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static string ResolveLocationId(AbstractGame game)
        {
            try
            {
                object locationObj = GetMemberValue(game, "Location", "_location");

                if (locationObj == null)
                {
                    return null;
                }

                return GetMemberValue(locationObj, "Id", "_Id") as string;
            }
            catch
            {
                return null;
            }
        }

        private static object ResolveInventoryController(HideoutPlayerOwner owner, Type inventoryControllerType)
        {
            if (owner?.Player == null)
            {
                return null;
            }

            try
            {
                object fromProperty = owner.Player.GetType()
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(p => inventoryControllerType.IsAssignableFrom(p.PropertyType))
                    ?.GetValue(owner.Player);

                if (fromProperty != null)
                {
                    return fromProperty;
                }

                return owner.Player.GetType()
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(f => inventoryControllerType.IsAssignableFrom(f.FieldType))
                    ?.GetValue(owner.Player);
            }
            catch
            {
                return null;
            }
        }

        private static void LogBootstrapCandidates(AbstractGame game, BotsController botsController)
        {
            if (Settings.EnableSpawnDiagnostics?.Value != true)
            {
                return;
            }

            try
            {
                IEnumerable<string> gameCandidates = game
                    .GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.GetParameters().Any(p => p.ParameterType.Name.Contains("BotControllerSettings") || p.ParameterType.Name.Contains("ISpawnSystem")))
                    .Take(6)
                    .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");

                IEnumerable<string> botsControllerCandidates = botsController
                    .GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name.IndexOf("init", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(8)
                    .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");

                Plugin.LogSource.LogInfo($"Hideout bootstrap candidates on game: [{string.Join(" | ", gameCandidates)}]");
                Plugin.LogSource.LogInfo($"Hideout bootstrap candidates on BotsController: [{string.Join(" | ", botsControllerCandidates)}]");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogDebug($"Failed to log bootstrap candidates: {ex.Message}");
            }
        }

        private static object TryResolveMemberByType(object target, Type requiredType)
        {
            Type type = target.GetType();
            while (type != null)
            {
                PropertyInfo property = type
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(p => requiredType.IsAssignableFrom(p.PropertyType));

                if (property != null)
                {
                    object value = property.GetValue(target);
                    if (value != null)
                    {
                        return value;
                    }
                }

                FieldInfo field = type
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(f => requiredType.IsAssignableFrom(f.FieldType));

                if (field != null)
                {
                    object value = field.GetValue(target);
                    if (value != null)
                    {
                        return value;
                    }
                }

                type = type.BaseType;
            }

            return null;
        }

        private static async Task<BotSpawner> ResolveSpawner(BotsController botsController)
        {
            BotSpawner spawner = GetSpawnerFromController(botsController);
            if (spawner != null)
            {
                return spawner;
            }

            IBotGame botGame = Singleton<IBotGame>.Instantiated ? Singleton<IBotGame>.Instance : null;
            if (botGame?.BotsController != null)
            {
                spawner = GetSpawnerFromController(botGame.BotsController);
                if (spawner != null)
                {
                    return spawner;
                }
            }

            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(100);
                spawner = GetSpawnerFromController(botsController);
                if (spawner != null)
                {
                    return spawner;
                }
            }

            return null;
        }

        private static BotSpawner GetSpawnerFromController(BotsController controller)
        {
            if (controller == null)
            {
                return null;
            }

            return AccessTools.Method(controller.GetType(), "GetSpawner")?.Invoke(controller, null) as BotSpawner
                ?? GetMemberValue(controller, "BotSpawner", "_botSpawner") as BotSpawner;
        }

        private static void LogInitExceptionChain(Exception ex, BepInEx.Logging.LogLevel level)
        {
            int depth = 0;
            Exception current = ex;
            while (current != null)
            {
                string header = $"Direct BotsController.Init exception[{depth}]: {current.GetType().Name}: {current.Message}";
                Plugin.LogSource.Log(level, header);
                if (!string.IsNullOrEmpty(current.StackTrace))
                {
                    Plugin.LogSource.LogDebug($"Direct BotsController.Init stack[{depth}]:\n{current.StackTrace}");
                }
                current = current.InnerException;
                depth++;
            }
        }

        private static HideoutPlayerOwner FindHideoutPlayerOwner()
        {
            AbstractGame game = Singleton<AbstractGame>.Instance;
            if (game == null)
            {
                return null;
            }

            object ownerObject = GetMemberValue(game, "PlayerOwner", "_playerOwner");

            return ownerObject as HideoutPlayerOwner;
        }

        private static void PositionAndFreezeBot(HideoutPlayerOwner owner, BotOwner bot)
        {
            Vector3 origin = owner.Player.Transform.position;
            Vector3 forward = owner.Player.Transform.forward;
            float distance = Settings.BotSpawnDistance?.Value ?? 3f;
            Vector3 desiredPosition = origin + forward * distance;

            try
            {
                bot.Transform.position = desiredPosition;
                FaceBotTowardPlayer(bot, origin, desiredPosition);
                bot.StopMove();
                bot.SetTargetMoveSpeed(0f);
                bot.Disable();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Failed to freeze/position spawned bot cleanly: {ex.Message}");
            }
        }

        /// <summary>
        /// Turns the spawned bot to look at the player. The bot is placed in front of the player and
        /// keeps whatever rotation it spawned with, which leaves it facing away.
        /// <para>
        /// A player's facing is driven by <c>MovementContext.Rotation</c> (a yaw/pitch pair in degrees),
        /// not by the transform - setting the transform alone is overwritten by the movement context.
        /// Yaw is measured clockwise from +Z, hence Atan2(x, z).
        /// </para>
        /// </summary>
        private static void FaceBotTowardPlayer(BotOwner bot, Vector3 playerPosition, Vector3 botPosition)
        {
            if (Settings.FaceBotTowardPlayer?.Value != true)
            {
                return;
            }

            try
            {
                Vector3 towardPlayer = playerPosition - botPosition;
                towardPlayer.y = 0f;
                if (towardPlayer.sqrMagnitude < 0.0001f)
                {
                    return;
                }

                float yaw = Mathf.Atan2(towardPlayer.x, towardPlayer.z) * Mathf.Rad2Deg;

                Player botPlayer = bot.GetPlayer;
                if (botPlayer?.MovementContext != null)
                {
                    botPlayer.MovementContext.SetRotation(new Vector2(yaw, 0f));
                }
                else
                {
                    bot.Transform.rotation = Quaternion.LookRotation(towardPlayer);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Failed to face spawned bot toward player: {ex.Message}");
            }
        }

        private static void DespawnTrackedBot()
        {
            if (_trackedBot == null)
            {
                return;
            }

            try
            {
                if (_trackedBot.GetPlayer != null)
                {
                    UnityEngine.Object.Destroy(_trackedBot.GetPlayer.gameObject);
                }
                else
                {
                    UnityEngine.Object.Destroy(_trackedBot.gameObject);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Failed to despawn previous tracked bot: {ex.Message}");
            }
            finally
            {
                _trackedBot = null;
            }
        }
    }

    // Sets InShootingRange immediately when the player confirms entry through the context menu,
    // preventing the weapon from being forced to semi-auto during the entry transition.
    internal class Patch_ShootingRangeBehaviour_ManualEnterLocation : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ShootingRangeBehaviour), nameof(ShootingRangeBehaviour.ManualEnterLocation));
        }

        [PatchPostfix]
        private static void Postfix(HideoutPlayerOwner player)
        {
            if (player != null)
            {
                player.InShootingRange = true;
            }
        }
    }

    // Skips HideoutPlayer.SetPatrol(true). The original method calls SetTriggerPressed(false)
    // and SetAim(false) when patrol mode is enabled, which would break full-auto each
    // time DecidePatrolStatus sees the player rotate past the degree limits.
    // SetPatrol(false) is still allowed so the initial unblock on entering the shooting
    // range and the cleanup on ExitShootingRange() (triggered by ESC) work correctly
    internal class Patch_HideoutPlayer_SetPatrol : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(HideoutPlayer), nameof(HideoutPlayer.SetPatrol));
        }

        [PatchPrefix]
        private static bool Prefix(bool patrol)
        {
            return !patrol;
        }
    }

    // Gate HideoutPlayerOwner.ExitShootingRange so it only runs when ESC explicitly asks for it.
    // The game otherwise calls it from ShootingRangeBehaviour.OnExitLocation (for example: walking out
    // of the area trigger), which would lower the weapon and disable full-auto. Our ESC pass-
    // through open ze gate just for that single command.
    internal class Patch_HideoutPlayerOwner_ExitShootingRange : ModulePatch
    {
        public static bool AllowExit = false;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.ExitShootingRange));
        }

        [PatchPrefix]
        private static bool Prefix(ref Task __result)
        {
            if (AllowExit)
            {
                return true;
            }
            __result = Task.CompletedTask;
            return false;
        }
    }

    // Detects the ESC press in shooting-range mode, opens the AllowExit gate so the original
    // ExitShootingRange runs (lowering the weapon and clearing InShootingRange), then closes
    // the gate again
    internal class Patch_HideoutPlayerOwner_TranslateExitScreenInput : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.TranslateExitScreenInput));
        }

        [PatchPrefix]
        private static void Prefix(HideoutPlayerOwner __instance, ECommand command)
        {
            if (command.IsCommand(ECommand.Escape) && __instance.InShootingRange)
            {
                Patch_HideoutPlayerOwner_ExitShootingRange.AllowExit = true;
            }
        }

        [PatchPostfix]
        private static void Postfix()
        {
            Patch_HideoutPlayerOwner_ExitShootingRange.AllowExit = false;
        }
    }

    // Diagnostic only. BotOwner.Create is the last step of the activation chain, and any exception it
    // throws is swallowed by Task.HandleExceptions() on BotCreatorClient's caller - which is why a
    // failed activation shows up only as "No bot was created" with no error anywhere. This surfaces
    // the real exception plus the argument state, since several of these (behaviourTreePrefab,
    // corePointId) are supplied by raid setup that does not run in the hideout.
    internal class Patch_BotOwner_Create_Diagnostics : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotOwner), nameof(BotOwner.Create));
        }

        [PatchPrefix]
        private static void Prefix(Player player, GameObject behaviourTreePrefab, GameDateTime gameDataTime, BotsController botsController, bool isLocalGame, AICorePoint corePointId)
        {
            if (Settings.EnableSpawnDiagnostics?.Value != true)
            {
                return;
            }

            Plugin.LogSource.LogInfo(
                "BotOwner.Create entered: " +
                $"player={(player == null ? "null" : player.ProfileId)}, " +
                $"behaviourTreePrefab={(behaviourTreePrefab == null ? "null" : behaviourTreePrefab.name)}, " +
                $"gameDateTime={(gameDataTime == null ? "null" : "ok")}, " +
                $"botsController={(botsController == null ? "null" : "ok")}, " +
                $"isLocalGame={isLocalGame}, " +
                $"corePoint={(corePointId == null ? "null" : corePointId.Id.ToString())}");
        }

        [PatchFinalizer]
        private static void Finalizer(Exception __exception, BotOwner __result)
        {
            if (__exception != null)
            {
                Plugin.LogSource.LogError($"BotOwner.Create threw: {__exception}");
                return;
            }

            if (Settings.EnableSpawnDiagnostics?.Value != true)
            {
                return;
            }

            // BotCreatorClient.CG_CreateBot sets CharacterController.isEnabled = false immediately
            // after this returns, with no null check - so a missing CharacterController NREs there
            // and the callback that would reveal the bot never runs.
            Player botPlayer = __result?.GetPlayer;
            Plugin.LogSource.LogInfo(
                $"BotOwner.Create returned {(__result == null ? "null" : __result.name)}, " +
                $"player={(botPlayer == null ? "null" : "ok")}, " +
                $"characterController={(botPlayer?.CharacterController == null ? "NULL" : "ok")}, " +
                $"movementContext={(botPlayer?.MovementContext == null ? "NULL" : "ok")}");
        }
    }

    // Diagnostic only. The step that actually reveals and activates a bot: it enables the
    // CharacterController, calls PreActivate with the group from groupAction, then
    // SwitchBotVisual(bot, true) and finally the callback that raises BotSpawner.OnBotCreated.
    // A bot that exists but is invisible and never reported is this method not completing.
    //
    // This method's compiler-generated name (method_3 against the SPT 4.1 client this mod
    // targeted) is an obfuscation artifact that is not guaranteed to stay the same name/index
    // across EFT client builds - including the older client SPT 4.0.13 ships. Rather than
    // hardcode a name that may not exist, find it by its distinctive parameter signature, which
    // obfuscation does not change.
    internal class Patch_BotCreatorClient_Activate_Diagnostics : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            MethodBase target = AccessTools.GetDeclaredMethods(typeof(BotCreatorClient))
                .FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 4
                        && p[0].ParameterType == typeof(BotZone)
                        && p[1].ParameterType == typeof(BotOwner)
                        && p[2].ParameterType == typeof(Action<BotOwner>)
                        && p[3].ParameterType == typeof(Func<BotOwner, BotZone, BotsGroup>);
                });

            if (target == null)
            {
                throw new InvalidOperationException(
                    "Could not find BotCreatorClient's bot-activation method by signature (BotZone, BotOwner, Action<BotOwner>, Func<BotOwner, BotZone, BotsGroup>) on this client build. Spawn diagnostics for bot activation are unavailable.");
            }

            return target;
        }

        [PatchPrefix]
        private static void Prefix(BotZone zone, BotOwner bot, Action<BotOwner> callback, Func<BotOwner, BotZone, BotsGroup> groupAction)
        {
            if (Settings.EnableSpawnDiagnostics?.Value != true)
            {
                return;
            }

            Player botPlayer = bot?.GetPlayer;
            Plugin.LogSource.LogInfo(
                "BotCreatorClient.method_3 entered: " +
                $"zone={(zone == null ? "null" : zone.NameZone)}, " +
                $"bot={(bot == null ? "null" : bot.name)}, " +
                $"player={(botPlayer == null ? "null" : "ok")}, " +
                $"characterController={(botPlayer?.CharacterController == null ? "NULL" : "ok")}, " +
                $"movementContext={(botPlayer?.MovementContext == null ? "NULL" : "ok")}, " +
                $"callback={(callback == null ? "null" : "ok")}, " +
                $"groupAction={(groupAction == null ? "null" : "ok")}");
        }

        [PatchFinalizer]
        private static void Finalizer(Exception __exception, BotOwner bot)
        {
            if (__exception != null)
            {
                Plugin.LogSource.LogError($"BotCreatorClient.method_3 threw: {__exception}");
                return;
            }

            Player botPlayer = bot?.GetPlayer;

            if (Settings.EnableSpawnDiagnostics?.Value == true)
            {
                Renderer[] renderers = botPlayer == null
                    ? Array.Empty<Renderer>()
                    : botPlayer.GetComponentsInChildren<Renderer>(true);
                int enabledCount = renderers.Count(r => r != null && r.enabled);
                int skinnedCount = renderers.Count(r => r is SkinnedMeshRenderer);
                int skinnedEnabled = renderers.Count(r => r is SkinnedMeshRenderer && r.enabled);

                Plugin.LogSource.LogInfo(
                    "BotCreatorClient.method_3 completed: " +
                    $"playerBody={(botPlayer?.PlayerBody == null ? "NULL" : "ok")}, " +
                    $"renderers={renderers.Length} (enabled={enabledCount}), " +
                    $"skinnedMesh={skinnedCount} (enabled={skinnedEnabled})");
            }

            HideoutBotContextController.LogBotRendererState(botPlayer, "at activation");
        }
    }

    // BotSpawnLimiter.IncreaseUsedPlayerSpawns is called from SpawnPoint.IncreaseUsedPlayerSpawnsForNearestPlayer
    // during BotSpawner.SpawnBotByTypeForce. The first thing it does is call its first internal step, which dereferences
    // TarkovApplication.CurrentRaidSettings.SelectedLocation.OfflineNewSpawn. In the hideout there is no
    // active raid, so SelectedLocation is null and the call throws NullReferenceException, aborting the
    // entire spawn. We skip the limiter entirely when no raid SelectedLocation is available; in a real
    // raid the original method runs unchanged.
    internal class Patch_BotSpawnLimiter_IncreaseUsedPlayerSpawns : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotSpawnLimiter), nameof(BotSpawnLimiter.IncreaseUsedPlayerSpawns));
        }

        [PatchPrefix]
        private static bool Prefix()
        {
            TarkovApplication tarkovApplication;
            if (!TarkovApplication.Exist(out tarkovApplication) || tarkovApplication == null)
            {
                return false;
            }

            var raidSettings = tarkovApplication.CurrentRaidSettings;
            if (raidSettings == null || raidSettings.SelectedLocation == null)
            {
                return false;
            }

            return true;
        }
    }

    // Third-party plugin "acidphantasm-botplacementsystem" attaches a Harmony postfix to
    // BotSpawner.SpawnBotsInZoneOnPositions that indexes a list whose contents only exist for raid maps. In the
    // hideout, that list is empty (or smaller than the index they read), so its postfix throws
    // ArgumentOutOfRangeException after our synthetic-zone bot creation already kicked off.
    // The exception propagates through BotSpawner.SpawnBotsInZoneOnPositions's async pipeline and aborts the spawn.
    //
    // We can't modify the third-party assembly. Instead, we attach our own prefix to their
    // postfix method and short-circuit it for any spawn coming through our synthetic hideout
    // BotZone (matched by name). For real raids, the prefix returns true and the original
    // postfix runs unchanged, so this patch is invisible to non-hideout flows.
    //
    // The patch resolves the third-party type lazily via reflection so we degrade gracefully
    // when the user does not have that mod installed.
    internal class Patch_AcidphantasmBotPlacement_BossProgressiveRegressivePostfix : ModulePatch
    {
        private const string TargetTypeName = "acidphantasm_botplacementsystem.Patches.BossProgressiveRegressivePatch";
        private const string TargetMethodName = "PatchPostfix";

        public static void TryEnable()
        {
            try
            {
                Type targetType = AccessTools.TypeByName(TargetTypeName);
                if (targetType == null)
                {
                    Plugin.LogSource.LogDebug($"Third-party patch type '{TargetTypeName}' not found; defensive patch skipped.");
                    return;
                }

                MethodInfo method = AccessTools.Method(targetType, TargetMethodName);
                if (method == null)
                {
                    Plugin.LogSource.LogDebug($"Third-party method '{TargetTypeName}.{TargetMethodName}' not found; defensive patch skipped.");
                    return;
                }

                new Patch_AcidphantasmBotPlacement_BossProgressiveRegressivePostfix().Enable();
                Plugin.LogSource.LogInfo($"Hooked defensive prefix on '{TargetTypeName}.{TargetMethodName}' for hideout zone safety.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"Failed to enable defensive patch '{TargetTypeName}.{TargetMethodName}': {ex.Message}");
            }
        }

        protected override MethodBase GetTargetMethod()
        {
            Type targetType = AccessTools.TypeByName(TargetTypeName);
            return AccessTools.Method(targetType, TargetMethodName);
        }

        [PatchPrefix]
        private static bool Prefix(BotZone zone)
        {
            if (zone != null && IsHideoutSyntheticZone(zone))
            {
                return false;
            }

            return true;
        }

        private static bool IsHideoutSyntheticZone(BotZone zone)
        {
            if (zone == null)
            {
                return false;
            }

            try
            {
                if (!string.IsNullOrEmpty(zone.NameZone)
                    && zone.NameZone.IndexOf("HideoutShootout", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (zone.gameObject != null
                    && !string.IsNullOrEmpty(zone.gameObject.name)
                    && zone.gameObject.name.IndexOf("HideoutShootout", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }
    }

}
