using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace HideoutShootout.Server;

// SPT 4.0.13 uses the AbstractModMetadata base record instead of 4.1's IModMetadata
// interface - all members are overrides, IsBundleMod replaces HasPrepatcher, and
// there is no separate IModWebMetadata to implement.
public record HideoutShootoutServerMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.moxopixel.hideoutshootout.server";
    public override string Name { get; init; } = "hideout-shootout-server";
    public override string Author { get; init; } = "MoxoPixel";
    public override List<string>? Contributors { get; init; }
    public override Version Version { get; init; } = new("1.1.0");
    public override Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}
