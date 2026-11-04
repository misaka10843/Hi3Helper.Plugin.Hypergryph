using Hi3Helper.Plugin.Core;
using System.Globalization;

namespace Hi3Helper.Hypergryph.Shared;

public enum SettingsText
{
    PageTitle, DisplaySection, GraphicsSection, PluginSection, DisplayHelp, GraphicsHelp,
    Verification, ParentExecutable, PluginHelp, EndfieldPluginHelp,
    NativeResolution, Width, Height, WindowMode, ExclusiveFullscreen, Borderless, Windowed,
    QualityPreset, Ultra, High, Medium, Low, VeryLow, FrameRate,
    ManagerUnavailable, ReopenSettings, UnknownSetting, InvalidValue, GamePathChanged,
    ParentFileMissing, SetGamePath, GameDirectoryMissing, InvalidJson, UnsupportedConfig,
    ConfigChanged, VerificationRunning, VerificationDuplicate, GameRunning,
    GameConfigMissing, GameConfigChanged, GameConfigReadOnly, GameConfigRestore,
    LinkedPluginDirectory
}

/// <summary>Compile-time translations, including in reflection-free Native AOT builds.</summary>
public static class SettingsLocalization
{
    public static string Get(SettingsText key)
    {
        (string English, string Chinese, string Japanese) text = key switch
        {
            SettingsText.PageTitle => ("Game & plugin settings", "游戏与插件设置", "ゲームとプラグイン設定"),
            SettingsText.DisplaySection => ("Display", "显示设置", "画面設定"),
            SettingsText.GraphicsSection => ("Graphics", "画质设置", "グラフィック設定"),
            SettingsText.PluginSection => ("Plugin", "插件设置", "プラグイン設定"),
            SettingsText.DisplayHelp => ("Close the game before applying. Changes take effect next launch. Native resolution overrides width and height.", "关闭游戏后应用，下次启动生效。原生分辨率会覆盖宽度和高度设置。", "ゲーム終了後に適用してください。次回起動時に反映されます。ネイティブ解像度を使う場合、幅と高さは無視されます。"),
            SettingsText.GraphicsHelp => ("Close the game before applying. The preset and FPS limit take effect next launch; the game may overwrite them.", "关闭游戏后应用画质预设和帧率上限，下次启动生效；游戏可能再次覆盖这些设置。", "ゲーム終了後に適用してください。プリセットとフレームレート上限は次回起動時に反映されます。ゲーム側で上書きされる場合があります。"),
            SettingsText.Verification => ("Verify files once", "完整验证一次", "ファイル検証を予約"),
            SettingsText.ParentExecutable => ("Parent EXE", "父进程 EXE", "親プロセス EXE"),
            SettingsText.PluginHelp => ("Enable verification and Apply; the request resets immediately. Refresh Home and choose Update to run it. Closing the launcher discards the request.", "勾选并应用后登记一次验证并立即复位请求。返回首页刷新，通过更新入口执行；关闭启动器会丢弃请求。", "チェックして適用すると検証を1回予約し、スイッチを解除します。ホームを更新して「更新」から実行してください。終了時に予約は破棄されます。"),
            SettingsText.EndfieldPluginHelp => ("Enable verification and Apply, then refresh Home and choose Update. The request resets on Apply and is discarded on exit. Parent EXE: empty uses PlatformProcess.exe; accepts absolute or game-relative paths with the same signer. Takes effect next launch.", "勾选并应用后登记一次验证并复位请求；返回首页刷新，通过更新入口执行，关闭启动器会丢弃请求。父进程留空使用 PlatformProcess.exe，支持绝对或游戏目录相对路径，签名者须与游戏一致，下次启动生效。", "検証を予約する場合はチェックして適用し、ホームを更新して「更新」から実行してください。予約は終了時に破棄されます。親 EXE は絶対・相対パスに対応し、空欄で PlatformProcess.exe を使用します。同じ署名者が必要で、次回起動時に反映されます。"),
            SettingsText.NativeResolution => ("Native resolution", "原生分辨率", "ネイティブ解像度"),
            SettingsText.Width => ("Width", "宽度", "幅"),
            SettingsText.Height => ("Height", "高度", "高さ"),
            SettingsText.WindowMode => ("Window mode", "窗口模式", "画面モード"),
            SettingsText.ExclusiveFullscreen => ("Exclusive", "独占全屏", "排他フルスクリーン"),
            SettingsText.Borderless => ("Borderless", "无边框全屏", "ボーダーレス"),
            SettingsText.Windowed => ("Windowed", "窗口", "ウィンドウ"),
            SettingsText.QualityPreset => ("Quality preset", "画质预设", "品質プリセット"),
            SettingsText.Ultra => ("Very high", "极高", "最高"),
            SettingsText.High => ("High", "高", "高"),
            SettingsText.Medium => ("Medium", "中", "中"),
            SettingsText.Low => ("Low", "低", "低"),
            SettingsText.VeryLow => ("Very low", "极低", "最低"),
            SettingsText.FrameRate => ("FPS limit", "帧率上限", "FPS 上限"),
            SettingsText.ManagerUnavailable => ("The game manager is unavailable.", "游戏管理器不可用。", "ゲームマネージャーを利用できません。"),
            SettingsText.ReopenSettings => ("Reopen the settings page before editing.", "请重新打开设置页面。", "設定画面を開き直してください。"),
            SettingsText.UnknownSetting => ("Unknown setting: {0}", "未知设置项：{0}", "不明な設定項目：{0}"),
            SettingsText.InvalidValue => ("Invalid value for setting: {0}", "设置值无效：{0}", "設定値が無効です：{0}"),
            SettingsText.GamePathChanged => ("The game directory changed. Reopen settings.", "游戏目录已更改，请重新打开设置。", "ゲームフォルダーが変更されました。設定を開き直してください。"),
            SettingsText.ParentFileMissing => ("The parent source must be an existing EXE file.", "父进程来源必须是存在的 EXE 文件。", "親プロセスには存在する EXE ファイルを指定してください。"),
            SettingsText.SetGamePath => ("Set the game installation directory first.", "请先设置游戏安装目录。", "先にゲームのインストール先を設定してください。"),
            SettingsText.GameDirectoryMissing => ("Set an existing game installation directory first.", "请先设置有效的游戏安装目录。", "先に存在するゲームのインストール先を設定してください。"),
            SettingsText.InvalidJson => ("The plugin configuration is invalid. The original file was preserved.", "插件配置无效，原文件已保留。", "プラグイン設定が無効です。元のファイルは保持されています。"),
            SettingsText.UnsupportedConfig => ("Unsupported configuration version or parent path.", "不支持的插件配置版本或父进程路径。", "設定のバージョンまたは親プロセスのパスが無効です。"),
            SettingsText.ConfigChanged => ("The configuration changed externally. Reopen settings before applying.", "配置已被其他程序修改，请重新打开设置后再应用。", "設定が外部で変更されました。設定画面を開き直してから適用してください。"),
            SettingsText.VerificationRunning => ("A game operation is running. Wait before requesting verification.", "游戏操作正在运行，请等待完成后再请求验证。", "ゲームの処理が実行中です。完了後に検証を予約してください。"),
            SettingsText.VerificationDuplicate => ("A game operation is already running.", "游戏操作已在运行。", "ゲームの処理はすでに実行中です。"),
            SettingsText.GameRunning => ("Close Endfield before applying game settings.", "请关闭 Endfield 后再应用游戏设置。", "Endfield を終了してからゲーム設定を適用してください。"),
            SettingsText.GameConfigMissing => ("Game preferences are missing. Reopen settings.", "游戏配置已消失，请重新打开设置。", "ゲーム設定が見つかりません。設定画面を開き直してください。"),
            SettingsText.GameConfigChanged => ("Game preferences changed externally. Reopen settings.", "游戏配置已被修改，请重新打开设置。", "ゲーム設定が外部で変更されました。設定画面を開き直してください。"),
            SettingsText.GameConfigReadOnly => ("Game preferences are not writable.", "游戏配置不可写。", "ゲーム設定に書き込めません。"),
            SettingsText.GameConfigRestore => ("Could not restore the previous game preferences.", "无法恢复之前的游戏设置。", "以前のゲーム設定を復元できませんでした。"),
            SettingsText.LinkedPluginDirectory => ("The plugin data directory must not be a directory link.", "插件数据目录不能是目录链接。", "プラグインのデータフォルダーにディレクトリリンクは使用できません。"),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        string language = PluginCulture.Locale.Split('-', '_')[0];
        return language.Equals("zh", StringComparison.OrdinalIgnoreCase) ? text.Chinese :
            language.Equals("ja", StringComparison.OrdinalIgnoreCase) ? text.Japanese : text.English;
    }

    public static string Format(SettingsText key, params object?[] values) => string.Format(CultureInfo.InvariantCulture, Get(key), values);
}
