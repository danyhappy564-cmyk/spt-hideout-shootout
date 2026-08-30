using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

// One-off tool: dumps type/member info from the real installed EFT client assemblies so the
// SPT 4.0.13 backport can be fixed against actual signatures instead of guesses. Not part of
// the mod - delete this project once the backport is done.
class Program
{
    static string ManagedDir;

    static void Main(string[] args)
    {
        string sptRoot = args.Length > 0 ? args[0] : @"E:\SPT 4.0.10";
        ManagedDir = Path.Combine(sptRoot, "EscapeFromTarkov_Data", "Managed");

        if (!Directory.Exists(ManagedDir))
        {
            Console.WriteLine("Managed folder not found at: " + ManagedDir);
            Console.WriteLine("Pass the SPT root as the first argument if it's not " + sptRoot);
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            string path = Path.Combine(ManagedDir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        // The 10 symbols that came back with zero matches when only Assembly-CSharp.dll was
        // scanned (IEftSession, BotCreatorClient, BotProfileClient, SpawnWave, etc.) are most
        // likely defined in a different Managed\*.dll - Unity/BSG spread the client across
        // several assemblies. Load every one we can and pool their types together instead of
        // guessing which specific DLL holds what.
        var allTypes = new List<Type>();
        var loadErrors = new List<string>();
        foreach (string dllPath in Directory.GetFiles(ManagedDir, "*.dll"))
        {
            string fileName = Path.GetFileNameWithoutExtension(dllPath);
            // Skip pure engine/BCL assemblies - they cannot contain EFT/BSG gameplay types and
            // skipping them cuts load time and noise substantially.
            if (fileName.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                || fileName is "mscorlib" or "netstandard" or "UnityEngine")
            {
                continue;
            }

            try
            {
                var asm = Assembly.LoadFrom(dllPath);
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                allTypes.AddRange(types);
            }
            catch (Exception ex)
            {
                loadErrors.Add($"{fileName}: {ex.Message}");
            }
        }

        using (var errWriter = new StreamWriter("load_errors.txt", false, Encoding.UTF8))
            foreach (var e in loadErrors)
                errWriter.WriteLine(e);

        Type[] typesArr = allTypes.Distinct().ToArray();

        using (var allTypesWriter = new StreamWriter("all_types.txt", false, Encoding.UTF8))
        {
            foreach (var t in typesArr.OrderBy(t => t.FullName))
                allTypesWriter.WriteLine(t.FullName);
        }
        Console.WriteLine($"Wrote {typesArr.Length} type names (from all loadable Managed\\*.dll) to all_types.txt");

        // Round 6 targets, informed by round 5's results:
        // - TarkovApplication confirmed: EFT.TarkovApplication : CommonClientApplication<ISession>,
        //   inheriting ClientApplication<ISession>.GetClientBackEndSession(). Singleton<T> (already
        //   used elsewhere in this file for GameWorld/IBotGame) is almost certainly how mod code
        //   gets the live instance - wired into Patches.cs as Singleton<TarkovApplication>.Instance.
        // - LoadBundlesAndCreatePools's raw signature came back: its 4th parameter is a
        //   GDelegate62 callback, and that delegate class is itself malformed (the CLR refuses to
        //   load it as unsealed) - meaning no C# code, ours or anyone else's, can construct a
        //   matching argument for it. This method is likely uncallable from outside its own
        //   assembly. RegisterPools(PoolsCategory, Transform, ObjectsFactoryDataClass, AssemblyType)
        //   has no such parameter and is fully known - switching the whole preload strategy to it.
        // Remaining LocalPlayer.Create args still need concrete replacements:
        //   AppEnvironment.Config.CharacterController.BotPlayerMode (CharacterControllerSpawner.Mode
        //   enum value), DumbStatisticsManager (an IStatisticsManager), ThirdPersonCustomizationFilter
        //   .Default (an IViewFilter). InGameBundles (the bundle path constants) also still missing -
        //   searching by field name since those are static fields, not methods.
        string[] targets =
        {
            // Round 11: IEasyAssets turned out to be a dead end (just one property, a dependency
            // graph object). The method-name search found a much more promising candidate instead:
            // IAssetsManager.LoadBundlesAsync(string[] resourceKeys) -> Comfort.Common.IOperation,
            // with AssetsManagerClass as its concrete implementor. Need: IAssetsManager's full shape
            // and how to obtain a live instance (likely Singleton<IAssetsManager>, hence the static-
            // member-by-type search below), Comfort.Common.IOperation (to know how to await/poll the
            // result), and ResourceKey (profile.GetAllPrefabPaths returns these, but LoadBundlesAsync
            // wants plain strings - need to know how to get the bundle path string out of one).
            "IAssetsManager", "AssetsManagerClass", "ResourceKey", "IOperation",
            "IEasyAssets",
            "HideoutGame", "ISpawnSystem",
            "LocalPlayerCullingHandlerClass", "GClass917",
            "BasePlayerCulling", "OfflinePlayerCulling",
            "CharacterControllerSpawner",
            "GClass2265", "GClass2268", "GClass2269", "LocationStatisticsCollectorAbstractClass",
            "GClass1854", "GClass1855", "GClass1856",
            "ClientApplication`1", "TarkovApplication", "PatchConstants", "GameApplication",
            "GClass680", "BotsPresets",
            "GInterface21", "ClientAppUtils",
            "BotCreatorClass", "ISession",
            "BotProfileDataClass", "ProfileDataClass", "GClass688", "GClass689",
            "ObjectsFactoryDataClass", "GClass407",
            "IStatisticsManager", "IViewFilter",
            "IEftSession", "BotProfileClient", "BotCreatorClient", "SpawnWave", "ObjectsFactory",
            "AppEnvironment", "GlobalEventDispatcher", "InGameBundles", "JobYieldPriority",
            "DumbStatisticsManager", "OfflinePlayerCulling", "PositionNote",
            "ThirdPersonCustomizationFilter", "DebugBotProfilesStructContainer", "GetProfileDataParams",
            "GClass682", "GClass406", "IGetProfileData", "PoolManagerClass", "LocalPlayer", "Profile",
            "BotCreationData", "BotCreationDataClass", "BotSpawner", "IBotCreator", "MovementContext",

            // Round 12: LoadBundlesAsync returns a real IOperation (bundle count matches what the
            // profile reports), and it's being driven every frame via StartCoroutine(yield return
            // operation), but Completed/Succeed/Failed never flip even after 90s in the hideout
            // scene. AssetsManagerClass.LoadBundlesAsync's ctor takes a BundlesManagerClass, so the
            // actual file I/O is almost certainly owned by BundlesManagerClass, not AssetsManagerClass
            // itself - if BundlesManagerClass is a MonoBehaviour that normally only runs/ticks during
            // real raid loading (not in the hideout scene), that would explain an operation that's
            // created successfully but never progresses. Dumping it plus the concrete IOperation
            // implementors nested inside AssetsManagerClass (one of Class3515-3526 is almost
            // certainly what LoadBundlesAsync actually returns) to find the ticking mechanism.
            "BundlesManagerClass", "GClass730",
            "Class3515", "Class3516", "Class3517", "Class3518", "Class3519", "Class3520", "Class3526",
        };

        // InGameBundles.PLAYER_BUNDLE_NAME etc. are static fields, not methods - a field-name
        // search across every loaded type instead of guessing which class holds them now.
        // Same idea for GlobalEventDispatcher: search by a shorter substring in case it was
        // renamed to something that doesn't contain the whole original name.
        using (var fsw = new StreamWriter("field_search.txt", false, Encoding.UTF8))
        {
            string[] fieldNameNeedles = { "BUNDLE_NAME", "ANIMATOR_CONTROLLER", "ROOTMOTION" };
            foreach (var needle in fieldNameNeedles)
            {
                fsw.WriteLine("==================================================");
                fsw.WriteLine("STATIC FIELD NAME CONTAINS: " + needle);
                fsw.WriteLine("==================================================");
                foreach (var t in typesArr)
                {
                    FieldInfo[] fields;
                    try { fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    foreach (var f in fields)
                    {
                        if (f.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        fsw.WriteLine("  " + t.FullName + "." + f.Name + " : " + SafeTypeName(f.FieldType));
                    }
                }
                fsw.WriteLine();
            }

            fsw.WriteLine("==================================================");
            fsw.WriteLine("TYPE NAME CONTAINS: EventDispatcher");
            fsw.WriteLine("==================================================");
            foreach (var t in typesArr)
                if (t.Name.IndexOf("EventDispatcher", StringComparison.OrdinalIgnoreCase) >= 0)
                    fsw.WriteLine("  " + t.FullName);
            fsw.WriteLine();

            fsw.WriteLine("==================================================");
            fsw.WriteLine("TYPE NAME CONTAINS: Culling");
            fsw.WriteLine("==================================================");
            foreach (var t in typesArr)
                if (t.Name.IndexOf("Culling", StringComparison.OrdinalIgnoreCase) >= 0)
                    fsw.WriteLine("  " + t.FullName + " (base: " + SafeTypeName(t.BaseType) + ")");
            fsw.WriteLine();

            string[] spawnSystemNeedles = { "SpawnSystem", "SpawnPointsCollection" };
            foreach (var needle in spawnSystemNeedles)
            {
                fsw.WriteLine("==================================================");
                fsw.WriteLine("TYPE NAME CONTAINS: " + needle);
                fsw.WriteLine("==================================================");
                foreach (var t in typesArr)
                    if (t.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        fsw.WriteLine("  " + t.FullName + " (base: " + SafeTypeName(t.BaseType) + ")");
                fsw.WriteLine();
            }
        }
        Console.WriteLine("Wrote field_search.txt");

        // ClientAppUtils.GetMainApp() has no matching type name anywhere in this client, so find
        // its equivalent by searching every loaded type's static methods by name instead.
        string[] methodNameSearches = { "GetMainApp", "MainApp", "GetClientBackEndSession", "BackEndSession", "CreateSpawnSystem", "CreateFromScene", "LoadBundle", "LoadAsync", "IsLoaded", "EnsureLoaded", "PreloadBundle" };
        using (var msw = new StreamWriter("method_search.txt", false, Encoding.UTF8))
        {
            foreach (var needle in methodNameSearches)
            {
                msw.WriteLine("==================================================");
                msw.WriteLine("METHOD NAME CONTAINS: " + needle);
                msw.WriteLine("==================================================");

                foreach (var t in typesArr)
                {
                    MethodInfo[] methods;
                    try
                    {
                        methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    }
                    catch { continue; }

                    foreach (var m in methods)
                    {
                        if (m.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        string sig;
                        try { sig = FormatMethod(m); }
                        catch (Exception ex) { sig = m.Name + " <error: " + ex.Message + ">"; }

                        msw.WriteLine("  " + t.FullName + (m.IsStatic ? " [static]" : "") + " :: " + sig);
                    }
                }

                msw.WriteLine();
            }

            // GetMainApp() might really be a static property/field instead of a method (e.g. a
            // Singleton<T>-style accessor or a static field SPT's own patches populate).
            string[] typeNameNeedles = { "ClientApplication", "TarkovApplication", "PatchConstants", "IAssetsManager" };
            foreach (var needle in typeNameNeedles)
            {
                msw.WriteLine("==================================================");
                msw.WriteLine("STATIC MEMBERS WHOSE TYPE CONTAINS: " + needle);
                msw.WriteLine("==================================================");

                foreach (var t in typesArr)
                {
                    try
                    {
                        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            if (SafeTypeName(p.PropertyType).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                                msw.WriteLine("  prop: " + t.FullName + "." + p.Name + " : " + SafeTypeName(p.PropertyType));

                        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            if (SafeTypeName(f.FieldType).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                                msw.WriteLine("  field: " + t.FullName + "." + f.Name + " : " + SafeTypeName(f.FieldType));
                    }
                    catch { }
                }

                msw.WriteLine();
            }
        }
        Console.WriteLine("Wrote method_search.txt");

        using (var w = new StreamWriter("dump.txt", false, Encoding.UTF8))
        {
            foreach (var name in targets)
            {
                w.WriteLine("==================================================");
                w.WriteLine("TARGET: " + name);
                w.WriteLine("==================================================");
                w.Flush();

                try
                {
                    var exact = typesArr.Where(t => t.Name == name).ToList();
                    if (exact.Count == 0)
                    {
                        w.WriteLine("  NOT FOUND by exact name. Candidates containing '" + name + "' (case-insensitive):");
                        var candidates = typesArr
                            .Where(t => t.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(t => t.FullName)
                            .OrderBy(n => n)
                            .Take(40)
                            .ToList();

                        if (candidates.Count == 0)
                            w.WriteLine("    (no candidates)");
                        else
                            foreach (var c in candidates)
                                w.WriteLine("    " + c);
                    }
                    else
                    {
                        foreach (var t in exact)
                            DumpType(w, t, typesArr);
                    }
                }
                catch (Exception ex)
                {
                    w.WriteLine("  ERROR while dumping this target: " + ex);
                }

                w.WriteLine();
                w.Flush();
            }
        }

        // CharacterControllerSpawner.Mode is a nested config type, not an enum - Type.Name for it
        // is just "Mode" (too common a word to search by name alone), so match by full name
        // instead. Also look for any existing static field/property already typed as it, in case
        // a ready-made "bot mode" preset exists somewhere (the way AppEnvironment.Config.
        // CharacterController.BotPlayerMode worked on this mod's SPT 4.1 target).
        using (var w2 = new StreamWriter("dump.txt", true, Encoding.UTF8))
        {
            w2.WriteLine("==================================================");
            w2.WriteLine("TARGET: CharacterControllerSpawner+Mode (by FullName)");
            w2.WriteLine("==================================================");

            Type modeType = typesArr.FirstOrDefault(t => t.FullName == "CharacterControllerSpawner+Mode");
            if (modeType == null)
            {
                w2.WriteLine("  NOT FOUND by exact FullName.");
            }
            else
            {
                DumpType(w2, modeType, typesArr);

                w2.WriteLine("  --- static members anywhere typed as this Mode ---");
                foreach (var t in typesArr)
                {
                    try
                    {
                        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            if (p.PropertyType == modeType)
                                w2.WriteLine("  prop: " + t.FullName + "." + p.Name);

                        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            if (f.FieldType == modeType)
                                w2.WriteLine("  field: " + t.FullName + "." + f.Name);
                    }
                    catch { }
                }
            }
            w2.WriteLine();
        }

        Console.WriteLine("Wrote dump.txt");

        // Round 13: reflection signatures alone can't explain WHY the LoadBundlesAsync operation
        // never completes (Completed/Succeed/Failed stay false for 90s even though it's driven via
        // a coroutine) - AssetsManagerClass+Class3515 (the concrete operation LoadBundlesAsync
        // returns) exposes no overridden MoveNext/completion method of its own, so the actual
        // polling/completion logic must live in its base class, Comfort.Common.Operation`1. Reading
        // the actual method bodies (via ICSharpCode.Decompiler, working straight off the same PE
        // files reflection already loaded) instead of guessing further.
        using (var dw = new StreamWriter("decompile.txt", false, Encoding.UTF8))
        {
            void DecompileWholeType(string fullName)
            {
                dw.WriteLine("==================================================");
                dw.WriteLine("DECOMPILE TYPE: " + fullName);
                dw.WriteLine("==================================================");
                dw.Flush();
                Type t = typesArr.FirstOrDefault(x => x.FullName == fullName);
                if (t == null)
                {
                    dw.WriteLine("  NOT FOUND.");
                    dw.WriteLine();
                    return;
                }
                dw.WriteLine(TryDecompile(t.Module.FullyQualifiedName, MetadataTokens.EntityHandle(t.MetadataToken)));
                dw.WriteLine();
            }

            void DecompileMethodByName(string typeFullName, string methodName)
            {
                dw.WriteLine("==================================================");
                dw.WriteLine("DECOMPILE METHOD: " + typeFullName + "." + methodName);
                dw.WriteLine("==================================================");
                dw.Flush();
                Type t = typesArr.FirstOrDefault(x => x.FullName == typeFullName);
                if (t == null)
                {
                    dw.WriteLine("  TYPE NOT FOUND.");
                    dw.WriteLine();
                    return;
                }
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                MethodInfo[] methods;
                try { methods = t.GetMethods(flags).Where(m => m.Name == methodName).ToArray(); }
                catch (Exception ex) { dw.WriteLine("  could not enumerate methods: " + ex.Message); dw.WriteLine(); return; }

                if (methods.Length == 0)
                {
                    dw.WriteLine("  METHOD NOT FOUND on this type.");
                    dw.WriteLine();
                    return;
                }

                foreach (var m in methods)
                    dw.WriteLine(TryDecompile(t.Module.FullyQualifiedName, MetadataTokens.EntityHandle(m.MetadataToken)));
                dw.WriteLine();
            }

            DecompileWholeType("Comfort.Common.Operation`1");
            DecompileWholeType("Comfort.Common.AbstractOperation");
            DecompileWholeType("AssetsManagerClass+Class3515");
            DecompileMethodByName("BundlesManagerClass", "LoadBundleAsync");
            DecompileMethodByName("AssetsManagerClass", "LoadBundlesAsync");
        }
        Console.WriteLine("Wrote decompile.txt");

        Console.WriteLine("DONE");
    }

    static readonly Dictionary<string, CSharpDecompiler> _decompilerCache = new();

    static CSharpDecompiler GetDecompiler(string dllPath)
    {
        if (_decompilerCache.TryGetValue(dllPath, out var cached))
            return cached;

        var mainModule = new PEFile(dllPath);
        string targetFramework = mainModule.DetectTargetFrameworkId();

        // Cross-assembly type references (e.g. a method in Assembly-CSharp.dll referencing a type
        // from Comfort.dll) need the resolver to know where to look - every dependency lives
        // alongside dllPath in the same Managed folder, so that one search directory covers it.
        var resolver = new UniversalAssemblyResolver(dllPath, throwOnError: false, targetFramework);
        resolver.AddSearchDirectory(ManagedDir);
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        var decompiler = new CSharpDecompiler(mainModule, resolver, settings);
        _decompilerCache[dllPath] = decompiler;
        return decompiler;
    }

    static string TryDecompile(string dllPath, EntityHandle handle)
    {
        try
        {
            return GetDecompiler(dllPath).DecompileAsString(new[] { handle });
        }
        catch (Exception ex)
        {
            return "  <decompile failed: " + ex + ">";
        }
    }

    static void DumpType(StreamWriter w, Type t, Type[] allTypes)
    {
        w.WriteLine("  FullName: " + t.FullName);
        w.WriteLine("  Kind: " + (t.IsInterface ? "interface"
            : t.IsEnum ? "enum"
            : t.IsAbstract && t.IsSealed ? "static class"
            : t.IsAbstract ? "abstract class"
            : t.IsValueType ? "struct"
            : "class"));
        w.WriteLine("  BaseType: " + t.BaseType);

        try
        {
            var ifaces = t.GetInterfaces();
            if (ifaces.Length > 0)
                w.WriteLine("  Implements: " + string.Join(", ", ifaces.Select(i => i.FullName)));
        }
        catch (Exception ex) { w.WriteLine("  Implements: <error: " + ex.Message + ">"); }

        if (t.IsEnum)
        {
            w.WriteLine("  Values: " + string.Join(", ", Enum.GetNames(t)));
            return;
        }

        // If this is an interface, also list every loaded type that implements it - useful for
        // finding the concrete class behind an interface-typed parameter (e.g. IGetProfileData).
        if (t.IsInterface)
        {
            try
            {
                var implementors = allTypes.Where(c => !c.IsInterface && t.IsAssignableFrom(c)).Select(c => c.FullName).OrderBy(n => n).Take(20).ToList();
                if (implementors.Count > 0)
                    w.WriteLine("  Implementors: " + string.Join(", ", implementors));
            }
            catch (Exception ex) { w.WriteLine("  Implementors: <error: " + ex.Message + ">"); }
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // A parameter/field/return type that itself fails to load (a delegate class that isn't
        // sealed, a cross-assembly type that didn't resolve, etc.) throws the instant .ToString()
        // touches it. Iterate the raw MemberInfo[] with per-member try/catch instead of a lazy
        // Select() pipeline, so one bad member logs an error line and formatting continues -
        // that's what silently swallowed a PoolManagerClass method and a MovementContext field
        // last run (both hidden behind an unloadable GDelegateNN parameter/field type).
        SafeDump(w, "ctor", SafeGetMembers(() => t.GetConstructors(flags), t, "ctor"), FormatMethod);
        SafeDump(w, "method", SafeGetMembers(() => t.GetMethods(flags).Where(m => !m.IsSpecialName).ToArray(), t, "method"), FormatMethod);
        SafeDump(w, "prop", SafeGetMembers(() => t.GetProperties(flags), t, "prop"), p => p.PropertyType + " " + p.Name);
        SafeDump(w, "field", SafeGetMembers(() => t.GetFields(flags), t, "field"), f => f.FieldType + " " + f.Name);
        SafeDump(w, "nested", SafeGetMembers(() => t.GetNestedTypes(flags), t, "nested"),
            nt => nt.FullName + (nt.IsEnum ? " [enum: " + string.Join(",", Enum.GetNames(nt)) + "]" : ""));
    }

    static TMember[] SafeGetMembers<TMember>(Func<TMember[]> get, Type owner, string label)
    {
        try
        {
            return get();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (could not enumerate {label} on {owner.FullName}: {ex.Message})");
            return Array.Empty<TMember>();
        }
    }

    static void SafeDump<TMember>(StreamWriter w, string label, TMember[] members, Func<TMember, string> format) where TMember : MemberInfo
    {
        foreach (var m in members)
        {
            try
            {
                w.WriteLine("  " + label + ": " + format(m));
            }
            catch (Exception ex)
            {
                string name = "?";
                try { name = m.Name; } catch { }
                w.WriteLine("  " + label + ": " + name + " <error formatting signature: " + ex.Message + ">");
            }
        }
    }

    static string SafeTypeName(Type t)
    {
        if (t == null) return "null";
        try { return t.ToString(); }
        catch (Exception ex) { return "<?:" + ex.GetType().Name + ">"; }
    }

    static string FormatMethod(MethodBase m)
    {
        // One unloadable parameter/return type (an unsealed delegate class, e.g. GDelegateNN)
        // used to fail the whole signature. Format each piece independently so the rest of a
        // method's real parameters still show up - that was hiding LoadBundlesAndCreatePools'
        // actual parameter list behind a single bad delegate-typed argument last round.
        string ret = "";
        if (m is MethodInfo mi)
        {
            try { ret = mi.ReturnType.ToString(); }
            catch (Exception ex) { ret = "<?:" + ex.GetType().Name + ">"; }
        }

        ParameterInfo[] parameters;
        try
        {
            parameters = m.GetParameters();
        }
        catch (Exception)
        {
            // GetParameters() itself throws when ANY parameter's type can't load (observed with
            // PoolManagerClass.LoadBundlesAndCreatePools's callback parameter - an unsealed
            // GDelegateNN class the CLR refuses to load at all). Reflection can't recover from
            // that, but the raw metadata (the signature blob + parameter name table) is still
            // sitting in the file - read it directly with System.Reflection.Metadata instead of
            // going through the type loader.
            string raw = TryRawFormatMethod(m);
            return raw ?? $"{m.Name}(<could not get parameters even via raw metadata>)";
        }

        var parts = new List<string>();
        foreach (var p in parameters)
        {
            try { parts.Add(p.ParameterType + " " + p.Name); }
            catch (Exception ex) { parts.Add("<?:" + ex.GetType().Name + "> " + (p.Name ?? "?")); }
        }

        return $"{ret} {m.Name}({string.Join(", ", parts)})".Trim();
    }

    static readonly Dictionary<string, MetadataReader> _readerCache = new();
    static readonly List<PEReader> _peReadersKeepAlive = new();

    static MetadataReader GetRawReader(string dllPath)
    {
        if (_readerCache.TryGetValue(dllPath, out var cached))
            return cached;

        var pe = new PEReader(File.OpenRead(dllPath));
        _peReadersKeepAlive.Add(pe); // keep the PEReader (and its stream) alive for the reader's lifetime
        var mr = pe.GetMetadataReader();
        _readerCache[dllPath] = mr;
        return mr;
    }

    // Decodes a method's signature straight from the metadata tables/blob heap, bypassing the
    // CLR type loader entirely - the only way to see a signature that references an unloadable
    // type (e.g. a malformed obfuscator-generated delegate class).
    static string TryRawFormatMethod(MethodBase m)
    {
        try
        {
            string dllPath = m.Module.FullyQualifiedName;
            var mr = GetRawReader(dllPath);

            EntityHandle handle = MetadataTokens.EntityHandle(m.MetadataToken);
            if (handle.Kind != HandleKind.MethodDefinition)
                return null;

            var mdHandle = (MethodDefinitionHandle)handle;
            MethodDefinition md = mr.GetMethodDefinition(mdHandle);
            MethodSignature<string> sig = md.DecodeSignature(new RawTypeNameProvider(), genericContext: null);

            var paramNamesBySequence = new Dictionary<int, string>();
            foreach (var ph in md.GetParameters())
            {
                Parameter p = mr.GetParameter(ph);
                if (p.SequenceNumber == 0) continue; // sequence 0 is the return value, not a parameter
                paramNamesBySequence[p.SequenceNumber] = mr.GetString(p.Name);
            }

            var parts = new List<string>();
            for (int i = 0; i < sig.ParameterTypes.Length; i++)
            {
                string name = paramNamesBySequence.TryGetValue(i + 1, out var n) ? n : ("arg" + (i + 1));
                parts.Add(sig.ParameterTypes[i] + " " + name);
            }

            return $"{sig.ReturnType} {m.Name}({string.Join(", ", parts)}) [via raw metadata]".Trim();
        }
        catch (Exception ex)
        {
            return $"{m.Name}(<raw metadata read also failed: {ex.Message}>)";
        }
    }
}

// Minimal ISignatureTypeProvider that resolves type names by reading metadata tables/strings
// directly - it never asks the CLR to load a Type, so it can't hit a TypeLoadException.
sealed class RawTypeNameProvider : ISignatureTypeProvider<string, object>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => elementType;
    public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(",", typeArguments) + ">";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        TypeDefinition td = reader.GetTypeDefinition(handle);
        return reader.GetString(td.Name);
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        TypeReference tr = reader.GetTypeReference(handle);
        return reader.GetString(tr.Name);
    }

    public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        TypeSpecification ts = reader.GetTypeSpecification(handle);
        return ts.DecodeSignature(this, genericContext);
    }
}
