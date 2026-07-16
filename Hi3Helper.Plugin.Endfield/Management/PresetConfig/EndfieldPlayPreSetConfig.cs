using Hi3Helper.Hypergryph.Core.Management;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Hypergryph.Core.Management.Api;

namespace Hi3Helper.Plugin.Endfield.Management.PresetConfig;

[GeneratedComClass]
public partial class EndfieldPlayPresetConfig : PluginPresetConfigBase
{
    private const string ExEcutableName = "Endfield.exe";

    private const string ExApiUrl = "https://launcher.gryphline.com/api/proxy/batch_proxy";
    private const string ExWebApiUrl = "https://launcher.gryphline.com/api/proxy/web/batch_proxy";

    private const string ExAppCode = "YDUTE5gscDZ229CW";
    private const string ExLauncherAppCode = "YDUTE5gscDZ229CW";
    private const string ExChannel = "6";
    private const string ExSubChannel = "802";
    private const string ExSeq = "3";

    [field: AllowNull] [field: MaybeNull] public override string GameName => field ??= "Arknights: Endfield";
    [field: AllowNull] [field: MaybeNull] public override string GameExecutableName => field ??= ExEcutableName;

    public override string GameAppDataPath
    {
        get
        {
            string? gamePath = null;
            GameManager?.GetGamePath(out gamePath);
            if (!string.IsNullOrEmpty(gamePath)) return Path.Combine(gamePath, "Endfield_Data");

            return string.Empty;
        }
    }

    [field: AllowNull] [field: MaybeNull] public override string GameLogFileName => field ??= null!;
    [field: AllowNull] [field: MaybeNull] public override string GameVendorName => field ??= "GRYPHLINE";
    [field: AllowNull] [field: MaybeNull] public override string GameRegistryKeyName => field ??= "Endfield";

    [field: AllowNull] [field: MaybeNull] public override string ProfileName => field ??= "EndfieldGlobal";

    [field: AllowNull]
    [field: MaybeNull]
    public override string ZoneDescription =>
        field ??= "Arknights: Endfield is a real-time 3D RPG with strategic elements.";

    [field: AllowNull] [field: MaybeNull] public override string ZoneName => field ??= "Global";

    [field: AllowNull]
    [field: MaybeNull]
    public override string ZoneFullName => field ??= "Arknights: Endfield (Global)";

    [field: AllowNull] [field: MaybeNull] public override string ZoneLogoUrl => field ??= "";
    [field: AllowNull] [field: MaybeNull] public override string ZonePosterUrl => field ??= "";

    [field: AllowNull]
    [field: MaybeNull]
    public override string ZoneHomePageUrl => field ??= "https://endfield.gryphline.com/";

    public override GameReleaseChannel ReleaseChannel => GameReleaseChannel.Public;

    [field: AllowNull] [field: MaybeNull] public override string GameMainLanguage => field ??= "en-US";

    [field: AllowNull]
    [field: MaybeNull]
    public override string LauncherGameDirectoryName => field ??= "Arknights Endfield Game Global";

    [field: AllowNull] [field: MaybeNull] public override List<string> SupportedLanguages => field ??= ["English"];

    public override ILauncherApiMedia? LauncherApiMedia
    {
        get => field ??= new HgLauncherApiMedia(ExWebApiUrl, ExAppCode, ExChannel, ExSubChannel, ExSeq);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => field ??= new HgLauncherApiNews(ExWebApiUrl, ExAppCode, ExChannel, ExSubChannel, ExSeq);
        set;
    }

    public override IGameManager? GameManager
    {
        get => field ??= new HgGameManager(ExEcutableName, ExApiUrl, ExWebApiUrl, ExAppCode, ExLauncherAppCode,
            ExChannel,
            ExSubChannel, ExSeq);
        set;
    }

    public override IGameInstaller? GameInstaller
    {
        get => field ??= new HgGameInstaller(GameManager!);
        set;
    }

    protected override Task<int> InitAsync(CancellationToken token)
    {
        return Task.FromResult(0);
    }
}
