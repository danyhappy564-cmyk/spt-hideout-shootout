using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

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

        // Round 2 targets, informed by round 1's results:
        // - IBotCreator's only implementor is BotCreatorClass (not BotCreatorClient) - need its
        //   full shape to replace `new BotCreatorClient(ibotGame, profileCreator, playerFactory)`.
        // - LocalPlayer.Create takes an `ISession session` param, not IEftSession - almost
        //   certainly the 4.0.13 name for what this mod calls IEftSession.
        // - IGetProfileData's implementors are BotProfileDataClass/GClass688/GClass689/
        //   ProfileDataClass - one of these replaces the missing GetProfileDataParams.
        // - PoolManagerClass has no LoadBundlesAndCreatePools; it has RegisterPools(PoolsCategory,
        //   Transform, ObjectsFactoryDataClass, AssemblyType) instead - need ObjectsFactoryDataClass's
        //   shape to know what that config object requires.
        // - LocalPlayer.Create also takes IStatisticsManager/IViewFilter - need their implementors.
        // Everything from round 1 that still came back with zero matches even after scanning
        // every Managed\*.dll is kept at the end - if it's empty again, those genuinely don't
        // exist in this client build.
        string[] targets =
        {
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
        };

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

        Console.WriteLine("Wrote dump.txt");
        Console.WriteLine("DONE");
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

    static string FormatMethod(MethodBase m)
    {
        string ret = m is MethodInfo mi ? mi.ReturnType.ToString() : "";
        string pars = string.Join(", ", m.GetParameters().Select(p => p.ParameterType + " " + p.Name));
        return $"{ret} {m.Name}({pars})".Trim();
    }
}
