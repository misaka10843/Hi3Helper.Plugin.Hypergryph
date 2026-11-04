using System;
using System.Collections.Generic;
using System.Linq;
using Hi3Helper.Hypergryph.Shared;
using Hi3Helper.Plugin.Core.UI.Settings;

namespace Hi3Helper.Plugin.Endfield.Settings;

public sealed partial class EndfieldDisplaySettings
{
    private sealed record Option(int Value, string English, string Chinese, string Japanese);
    private sealed record Preference(string Name, string RegistryName, string English, string Chinese, string Japanese,
        string Group, Option[] Options, bool Volume = false, bool Toggle = false, int Maximum = 0);

    private static string Text(string english, string chinese, string japanese) =>
        PluginCulture.Locale.Split('-', '_')[0].ToLowerInvariant() switch { "zh" => chinese, "ja" => japanese, _ => english };

    private static Option O(int value, string english, string chinese, string japanese) => new(value, english, chinese, japanese);
    private static readonly Option[] OnOff = [O(0, "Off", "关", "オフ"), O(1000, "On", "开", "オン")];
    private static readonly Preference[] Preferences =
    [
        new("graphics.custom", "video_custom_quality_h2367254986", "Custom quality", "自定义画质", "カスタム品質", "graphics", [], Toggle: true),
        new("graphics.shadows", "video_quality_shadowmap_1_h128234669", "Shadows", "阴影", "影", "graphics", [O(1000,"High","高","高"),O(2000,"Medium","中","中"),O(3000,"Low","低","低"),O(4000,"Very low","极低","最低")]),
        new("graphics.textures", "video_texture_quality_1_h3061223496", "Textures", "纹理", "テクスチャ", "graphics", [O(2000,"Medium","中","中"),O(3000,"Low","低","低")]),
        new("graphics.fog", "video_quality_volumetricfog_1_h4151020765", "Volumetric fog", "体积雾", "ボリュームフォグ", "graphics", [O(1000,"Very high","极高","最高"),O(2000,"Medium","中","中"),O(5000,"Off","关闭","オフ")]),
        new("graphics.clouds", "video_quality_volumetriccloud_1_h4135137794", "Volumetric clouds", "体积云", "ボリューム雲", "graphics", [O(1000,"Very high","极高","最高"),O(3000,"Medium","中","中"),O(5000,"Very low","极低","最低")]),
        new("graphics.anisotropy", "video_quality_anisoLevel_1_h3121203451", "Anisotropic filtering", "各向异性采样", "異方性フィルタリング", "graphics", [O(1000,"1×","1×","1×"),O(2000,"2×","2×","2×"),O(3000,"4×","4×","4×"),O(4000,"8×","8×","8×")]),
        new("graphics.ao", "video_quality_ao_1_h2648627865", "Ambient occlusion", "环境光遮蔽", "アンビエントオクルージョン", "graphics", [O(1000,"High","高","高"),O(3000,"Low","低","低"),O(4000,"Very low","极低","最低")]),
        new("graphics.scene", "video_quality_scene_detail_1_h1593605959", "Scene detail", "场景细节", "シーン詳細", "graphics", [O(1000,"Very high","极高","最高"),O(2000,"High","高","高"),O(4000,"Low","低","低")]),
        new("graphics.environment", "video_quality_environment_renderfeature_1_h2552402819", "Environment detail", "环境细节", "環境詳細", "graphics", [O(1000,"High","高","高"),O(3000,"Low","低","低")]),
        new("graphics.grass", "video_quality_grass_sparsity_1_h587916507", "Vegetation density", "植被密度", "植生密度", "graphics", [O(1000,"High","高","高"),O(2000,"Low","低","低")]),
        new("graphics.chromatic", "video_quality_chromatic_aberration_1_h1000454999", "Chromatic aberration", "色差", "色収差", "graphics", OnOff),
        new("graphics.reflections", "video_quality_screenspacereflection_1_h1017734168", "Screen-space reflections", "屏幕空间反射", "スクリーンスペース反射", "graphics", [O(1000,"Very high","极高","最高"),O(4000,"Low","低","低"),O(5000,"Off","关闭","オフ")]),
        new("graphics.contact", "video_quality_contactshadow_1_h2952688721", "Contact shadows", "接触阴影", "コンタクトシャドウ", "graphics", OnOff),
        new("graphics.upscaler", "video_quality_upscaler_2_h1962157691", "Upscaler", "画质提升", "アップスケーラー", "upscaling", [O(1000,"DLSS","DLSS","DLSS"),O(2000,"TAAU","TAAU","TAAU"),O(3000,"AMD FSR 3","AMD FSR 3","AMD FSR 3")]),
        new("graphics.fsr", "video_quality_fsr3_mode_1_h3089342015", "FSR mode", "FSR 模式", "FSR モード", "upscaling", [O(1000,"Ultra performance","超高性能","ウルトラパフォーマンス"),O(2000,"Performance","高性能","パフォーマンス"),O(3000,"Balanced","平衡","バランス"),O(4000,"Quality","高质量","クオリティ"),O(5000,"Native AA","原生 AA","ネイティブ AA")]),
        new("graphics.dlss", "video_quality_dlss_mode_1_h833809667", "DLSS mode", "DLSS 模式", "DLSS モード", "upscaling", [O(1000,"Ultra performance","超级性能","ウルトラパフォーマンス"),O(2000,"Performance","性能","パフォーマンス"),O(3000,"Balanced","平衡","バランス"),O(4000,"Quality","质量","クオリティ"),O(5000,"DLAA","DLAA","DLAA")]),
        new("graphics.sharpness", "video_quality_sharpness_1_h923865732", "Sharpness", "锐化程度", "シャープネス", "upscaling", [], Maximum: 1000),
        new("graphics.framegen", "video_quality_framegen_1_h428217478", "Frame generation", "帧生成", "フレーム生成", "upscaling", [O(1000,"Off","关闭","オフ"),O(2000,"DLSS","DLSS","DLSS"),O(3000,"FSR 3","FSR 3","FSR 3")]),
        new("graphics.dlssg", "video_quality_dlssg_mode_1_h1229296740", "DLSS frame mode", "DLSS 帧生成模式", "DLSS フレームモード", "upscaling", [O(1000,"Auto","自动","自動"),O(2000,"2×","2×","2×"),O(3000,"3×","3×","3×"),O(4000,"4×","4×","4×")]),
        new("graphics.reflex", "video_quality_reflex_1_h358679639", "NVIDIA Reflex", "NVIDIA Reflex", "NVIDIA Reflex", "upscaling", [O(1000,"Off","关闭","オフ"),O(2000,"On","开启","オン"),O(3000,"On + Boost","开启 + 增强","オン + ブースト")]),
        new("audio.master", "audio_global_h50590663", "Master volume", "总音量", "マスター音量", "audio", [], Volume: true),
        new("audio.voice", "audio_voice_h1583466330", "Voice volume", "语音音量", "ボイス音量", "audio", [], Volume: true),
        new("audio.music", "audio_music_h1556961229", "Music volume", "音乐音量", "音楽音量", "audio", [], Volume: true),
        new("audio.sfx", "audio_sfx_h3763990721", "Effects volume", "音效音量", "効果音音量", "audio", [], Volume: true),
        new("audio.output", "audio_suite_mode_h170452302", "Output mode", "输出模式", "出力モード", "audio", [O(1,"Headphones","耳机","ヘッドホン"),O(2,"TV","电视","テレビ"),O(3,"Home theater","家庭影院","ホームシアター"),O(4,"Desktop speakers","桌面音响","デスクトップスピーカー")]),
        new("audio.background", "audio_suspend_unfocused_h977418127", "Mute in background", "后台静音", "バックグラウンドでミュート", "audio", [], Toggle: true),
        new("audio.controller", "audio_controller_h1787073584", "Controller speaker", "控制器喇叭", "コントローラースピーカー", "audio", [], Toggle: true),
        new("language.text", "language_text_change_h1448997164", "Text language", "文字语言", "表示言語", "language", [O(0,"简体中文","简体中文","简体中文"),O(4,"繁體中文","繁體中文","繁體中文"),O(8,"Deutsch","Deutsch","Deutsch"),O(12,"English","English","English"),O(16,"Español","Español","Español"),O(20,"Français","Français","Français"),O(24,"Bahasa Indonesia","Bahasa Indonesia","Bahasa Indonesia"),O(28,"Italiano","Italiano","Italiano"),O(32,"日本語","日本語","日本語"),O(36,"한국어","한국어","한국어"),O(40,"Português (Brasil)","Português (Brasil)","Português (Brasil)"),O(44,"Русский","Русский","Русский"),O(48,"ภาษาไทย","ภาษาไทย","ภาษาไทย"),O(52,"Tiếng Việt","Tiếng Việt","Tiếng Việt")]),
        new("language.voice", "language_audio_h2756590110", "Voice language", "语音语言", "音声言語", "language", [O(1,"中文","中文","中文"),O(2,"English","English","English"),O(3,"日本語","日本語","日本語"),O(4,"한국어","한국어","한국어")])
    ];

    private IEnumerable<GameSettingsSection> AdditionalSections()
    {
        foreach (string group in new[] { "graphics", "upscaling", "audio", "language" })
        {
            var entries = new List<GameSettingEntry>();
            foreach (var spec in Preferences.Where(p => p.Group == group))
            {
                if (!_values.TryGetValue(spec.Name, out long value)) continue;
                string label = Text(spec.English, spec.Chinese, spec.Japanese);
                entries.Add(spec.Volume ? GameSettingEntry.Slider(spec.Name, label, (int)Math.Round(BitConverter.Int64BitsToDouble(value) * 10), 0, 10) :
                    spec.Toggle ? GameSettingEntry.Toggle(spec.Name, label, value == 1) :
                    spec.Maximum > 0 ? GameSettingEntry.Slider(spec.Name, label, value / 1000.0, 0, 1, 0.1) :
                    GameSettingEntry.Choice(spec.Name, label, value.ToString(System.Globalization.CultureInfo.InvariantCulture), spec.Options.Select(o => new GameSettingChoice(o.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), Text(o.English,o.Chinese,o.Japanese))).ToArray()));
            }
            if (entries.Count == 0) continue;
            string title = group switch {
                "graphics" => Text("Graphics detail", "画质细项", "グラフィック詳細"),
                "upscaling" => Text("Upscaling & frames", "超分辨率与帧生成", "超解像とフレーム生成"),
                "audio" => Text("Audio", "音频设置", "オーディオ設定"),
                _ => Text("Language", "语言设置", "言語設定") };
            string help = group == "language"
                ? Text("Apply with the game closed; takes effect next launch. Install the selected voice pack in the game first.", "关闭游戏后应用，下次启动生效。语音包请先在游戏内安装。", "ゲーム終了後に適用し、次回起動時に反映されます。使用する音声パックは先にゲーム内でインストールしてください。")
                : Text("Apply with the game closed; takes effect next launch. Only recorded options are listed. GPU-dependent options require compatible hardware.", "关闭游戏后应用，下次启动生效。仅列出已确认选项；显卡相关选项需要兼容硬件。", "ゲーム終了後に適用し、次回起動時に反映されます。確認済みの選択肢のみ表示します。GPU 関連の機能には対応ハードウェアが必要です。");
            yield return new(title, entries) { Description = help };
        }
    }
}
