namespace HideoutShootout.Server.Globals;

public static class ModConfig
{
    public static readonly HideoutServerConfig Config = new();
}

public class HideoutServerConfig
{
    public bool EnableHideoutBotPrereqPatch = true;
    public bool HideoutOnly = true;
    public bool VerboseLogging = true;
}
