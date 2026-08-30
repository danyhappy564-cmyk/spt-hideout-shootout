using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// One-off tool: dumps type/member info from the real installed Assembly-CSharp.dll so the
// SPT 4.0.13 backport can be fixed against actual signatures instead of guesses. Not part of
// the mod - delete this project once the backport is done.
class Program
{
    static string ManagedDir;

    static void Main(string[] args)
    {
        string sptRoot = args.Length > 0 ? args[0] : @"E:\SPT 4.0.10";
        ManagedDir = Path.Combine(sptRoot, "EscapeFromTarkov_Data", "Managed");

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            string path = Path.Combine(ManagedDir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        var asmPath = Path.Combine(ManagedDir, "Assembly-CSharp.dll");
        if (!File.Exists(asmPath))
        {
            Console.WriteLine("Assembly-CSharp.dll not found at: " + asmPath);
            Console.WriteLine("Pass the SPT root as the first argument if it's not E:\\SPT 4.0.10");
            return;
        }

        var asm = Assembly.LoadFrom(asmPath);

        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray();
            using (var lw = new StreamWriter("loader_exceptions.txt", false, Encoding.UTF8))
            {
                foreach (var le in ex.LoaderExceptions.Take(50))
                    lw.WriteLine(le?.Message);
            }
            Console.WriteLine($"Some types failed to load (see loader_exceptions.txt); continuing with {types.Length} that did.");
        }

        using (var allTypesWriter = new StreamWriter("all_types.txt", false, Encoding.UTF8))
        {
            foreach (var t in types.OrderBy(t => t.FullName))
                allTypesWriter.WriteLine(t.FullName);
        }
        Console.WriteLine($"Wrote {types.Length} type names to all_types.txt");

        string[] targets =
        {
            "AppEnvironment", "BotCreationData", "BotCreationDataClass", "BotCreatorClient",
            "BotProfileClient", "BotSpawner", "DebugBotProfilesStructContainer",
            "DumbStatisticsManager", "GetProfileDataParams", "GlobalEventDispatcher",
            "IBotCreator", "IEftSession", "InGameBundles", "JobYieldPriority",
            "MovementContext", "OfflinePlayerCulling", "PositionNote", "SpawnWave",
            "ThirdPersonCustomizationFilter", "GClass682", "GClass406", "ObjectsFactory",
            "PoolManagerClass", "LocalPlayer", "Profile",
        };

        using (var w = new StreamWriter("dump.txt", false, Encoding.UTF8))
        {
            foreach (var name in targets)
            {
                w.WriteLine("==================================================");
                w.WriteLine("TARGET: " + name);
                w.WriteLine("==================================================");

                var exact = types.Where(t => t.Name == name).ToList();
                if (exact.Count == 0)
                {
                    w.WriteLine("  NOT FOUND by exact name. Candidates containing '" + name + "' (case-insensitive):");
                    var candidates = types
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
                        DumpType(w, t);
                }

                w.WriteLine();
            }
        }

        Console.WriteLine("Wrote dump.txt");
    }

    static void DumpType(StreamWriter w, Type t)
    {
        w.WriteLine("  FullName: " + t.FullName);
        w.WriteLine("  Kind: " + (t.IsInterface ? "interface"
            : t.IsEnum ? "enum"
            : t.IsAbstract && t.IsSealed ? "static class"
            : t.IsAbstract ? "abstract class"
            : t.IsValueType ? "struct"
            : "class"));
        w.WriteLine("  BaseType: " + t.BaseType);

        var ifaces = t.GetInterfaces();
        if (ifaces.Length > 0)
            w.WriteLine("  Implements: " + string.Join(", ", ifaces.Select(i => i.FullName)));

        if (t.IsEnum)
        {
            w.WriteLine("  Values: " + string.Join(", ", Enum.GetNames(t)));
            return;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var c in t.GetConstructors(flags))
            w.WriteLine("  ctor: " + FormatMethod(c));

        foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName))
            w.WriteLine("  method: " + FormatMethod(m));

        foreach (var p in t.GetProperties(flags))
            w.WriteLine("  prop: " + p.PropertyType + " " + p.Name);

        foreach (var f in t.GetFields(flags))
            w.WriteLine("  field: " + f.FieldType + " " + f.Name);

        foreach (var nt in t.GetNestedTypes(flags))
            w.WriteLine("  nested: " + nt.FullName
                + (nt.IsEnum ? " [enum: " + string.Join(",", Enum.GetNames(nt)) + "]" : ""));
    }

    static string FormatMethod(MethodBase m)
    {
        string ret = m is MethodInfo mi ? mi.ReturnType.ToString() : "";
        string pars = string.Join(", ", m.GetParameters().Select(p => p.ParameterType + " " + p.Name));
        return $"{ret} {m.Name}({pars})".Trim();
    }
}
