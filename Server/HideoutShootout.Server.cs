using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace HideoutShootout.Server;

// SPT 4.1 replaced the AbstractModMetadata base record with the IModMetadata
// interface, dropped IModWebMetadata and IsBundleMod, and added HasPrepatcher.
public record HideoutShootoutServerMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.moxopixel.hideoutshootout.server";
    public string Name { get; init; } = "hideout-shootout-server";
    public string Author { get; init; } = "MoxoPixel";
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new("1.1.0");
    public Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public bool HasPrepatcher { get; init; }
    public string License { get; init; } = "MIT";
}
