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

        // Highest-priority unknowns first, so a crash partway through still saves the most
        // valuable data. Already-confirmed types (BotCreationDataClass, BotSpawner, IBotCreator,
        // MovementContext) are last since we already have those from the previous run.
        string[] targets =
        {
            "GClass682", "GClass406",
            "IEftSession", "BotProfileClient", "BotCreatorClient", "SpawnWave",
            "AppEnvironment", "GlobalEventDispatcher", "InGameBundles", "JobYieldPriority",
            "DumbStatisticsManager", "OfflinePlayerCulling", "PositionNote",
            "ThirdPersonCustomizationFilter", "DebugBotProfilesStructContainer",
            "GetProfileDataParams", "IGetProfileData",
            "ObjectsFactory", "PoolManagerClass", "LocalPlayer", "Profile",
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

        TryEach(w, "ctor", () => t.GetConstructors(flags).Select(c => FormatMethod(c)));
        TryEach(w, "method", () => t.GetMethods(flags).Where(m => !m.IsSpecialName).Select(m => FormatMethod(m)));
        TryEach(w, "prop", () => t.GetProperties(flags).Select(p => p.PropertyType + " " + p.Name));
        TryEach(w, "field", () => t.GetFields(flags).Select(f => f.FieldType + " " + f.Name));
        TryEach(w, "nested", () => t.GetNestedTypes(flags).Select(nt => nt.FullName + (nt.IsEnum ? " [enum: " + string.Join(",", Enum.GetNames(nt)) + "]" : "")));
    }

    static void TryEach(StreamWriter w, string label, Func<IEnumerable<string>> getLines)
    {
        try
        {
            foreach (var line in getLines())
                w.WriteLine("  " + label + ": " + line);
        }
        catch (Exception ex)
        {
            w.WriteLine("  " + label + ": <error: " + ex.Message + ">");
        }
    }

    static string FormatMethod(MethodBase m)
    {
        string ret = m is MethodInfo mi ? mi.ReturnType.ToString() : "";
        string pars = string.Join(", ", m.GetParameters().Select(p => p.ParameterType + " " + p.Name));
        return $"{ret} {m.Name}({pars})".Trim();
    }
}
