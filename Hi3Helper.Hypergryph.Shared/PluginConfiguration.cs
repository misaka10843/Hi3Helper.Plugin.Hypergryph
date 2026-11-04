using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Hypergryph.Shared;

public sealed class PluginOptions
{
    public int SchemaVersion { get; set; } = 1;
    public bool ManualVerificationRequested { get; set; }
    public string ParentExecutablePath { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PluginOptions))]
internal partial class PluginJsonContext : JsonSerializerContext;

/// <summary>Per-manager, per-installation request state. No pending request is persisted after acceptance.</summary>
public sealed class PluginConfiguration
{
    private static readonly ConditionalWeakTable<IGameManager, PluginConfiguration> States = new();
    private readonly object _gate = new();
    private string? _path;
    private string? _observed;
    private bool _pending;
    private bool _running;

    public static PluginConfiguration For(IGameManager manager)
    {
        var state = States.GetValue(manager, static _ => new());
        manager.GetGamePath(out string? path);
        lock (state._gate)
        {
            string? full = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            if (!string.Equals(state._path, full, StringComparison.OrdinalIgnoreCase))
            {
                state._path = full;
                state._observed = null;
                state._pending = false;
            }
        }
        return state;
    }

    private string ConfigPath => _path == null ? throw new InvalidOperationException("请先设置游戏安装目录。") : PluginPaths.In(_path, "config.json");

    public PluginOptions Read()
    {
        lock (_gate)
        {
            if (_path == null || !Directory.Exists(_path)) throw new DirectoryNotFoundException("请先设置有效的游戏安装目录。");
            PluginPaths.Migrate(_path);
            string path = ConfigPath;
            string? json = File.Exists(path) ? File.ReadAllText(path) : null;
            var options = json == null ? new PluginOptions() :
                JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginOptions) ?? throw new InvalidDataException("插件配置不能为空。");
            if (options.SchemaVersion != 1 || options.ParentExecutablePath == null)
                throw new InvalidDataException("不支持的插件配置版本或父进程路径。");
            _observed = json;
            if (json == null) Save(options);
            return options;
        }
    }

    private void Save(PluginOptions options)
    {
        string path = ConfigPath;
        string? current = File.Exists(path) ? File.ReadAllText(path) : null;
        if (current != _observed) throw new IOException("配置已被其他程序修改，请重新打开设置后再应用。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string json = JsonSerializer.Serialize(options, PluginJsonContext.Default.PluginOptions);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, true);
            _observed = json;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string Snapshot { get { lock (_gate) return _observed ?? ""; } }

    public void Apply(PluginOptions options, string expectedSnapshot)
    {
        lock (_gate)
        {
            if (_running && options.ManualVerificationRequested) throw new InvalidOperationException("验证正在运行，请等待完成。");
            bool request = options.ManualVerificationRequested;
            _observed = expectedSnapshot;
            options.ManualVerificationRequested = false;
            try { Save(options); }
            catch { options.ManualVerificationRequested = request; throw; }
            if (request) _pending = true;
        }
    }

    public bool HasVerificationRequest
    {
        get
        {
            lock (_gate)
            {
                if (_path == null || !Directory.Exists(_path)) return false;
                var options = Read();
                string marker = Path.Combine(_path, ".collapse_verify_game");
                bool legacy = File.Exists(marker);
                if (options.ManualVerificationRequested || legacy)
                {
                    if (_running) throw new InvalidOperationException("验证正在运行，不能重复请求。");
                    options.ManualVerificationRequested = false;
                    Save(options);
                    if (legacy) File.Delete(marker);
                    _pending = true;
                }
                return _pending;
            }
        }
    }

    public bool BeginVerification()
    {
        lock (_gate)
        {
            if (_running) throw new InvalidOperationException("验证已在运行。");
            bool verify = HasVerificationRequest;
            _pending = false;
            _running = true;
            return verify;
        }
    }

    public void EndVerification() { lock (_gate) _running = false; }
}
