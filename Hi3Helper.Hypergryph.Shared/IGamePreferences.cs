using Hi3Helper.Plugin.Core.UI.Settings;

namespace Hi3Helper.Hypergryph.Shared;

public interface IGamePreferences
{
    IEnumerable<GameSettingsSection> Sections { get; }
    void Set(string name, string value);
    void Apply();
    void Restore();
}
