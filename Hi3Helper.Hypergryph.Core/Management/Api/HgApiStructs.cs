using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hi3Helper.Hypergryph.Core.Management.Api;

// ==========================================
// 请求结构
// ==========================================
public class HgBatchRequest
{
    [JsonPropertyName("seq")] public string Seq { get; set; } = null!;

    [JsonPropertyName("proxy_reqs")] public List<HgProxyRequest> ProxyReqs { get; set; } = new();
}

public class HgProxyRequest
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = null!;

    [JsonPropertyName("get_latest_game_req")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HgGetLatestGameReq? GetLatestGameReq { get; set; }

    // 通用请求体用于 Banner, News, BgImage, Sidebar
    [JsonPropertyName("get_banner_req")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HgCommonReq? GetBannerReq { get; set; }

    [JsonPropertyName("get_announcement_req")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HgCommonReq? GetAnnouncementReq { get; set; }

    [JsonPropertyName("get_main_bg_image_req")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HgCommonReq? GetMainBgImageReq { get; set; }

    [JsonPropertyName("get_sidebar_req")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HgCommonReq? GetSidebarReq { get; set; }
}

public class HgGetLatestGameReq
{
    [JsonPropertyName("appcode")] public string AppCode { get; set; } = null!;
    [JsonPropertyName("channel")] public string Channel { get; set; } = null!;
    [JsonPropertyName("sub_channel")] public string SubChannel { get; set; } = null!;
    [JsonPropertyName("version")] public string Version { get; set; } = null!;
    [JsonPropertyName("launcher_appcode")] public string LauncherAppCode { get; set; } = null!;
}

public class HgCommonReq
{
    [JsonPropertyName("appcode")] public string AppCode { get; set; } = null!;
    [JsonPropertyName("language")] public string Language { get; set; } = "zh-cn";
    [JsonPropertyName("channel")] public string Channel { get; set; } = null!;
    [JsonPropertyName("sub_channel")] public string SubChannel { get; set; } = null!;
    [JsonPropertyName("platform")] public string Platform { get; set; } = "Windows";
    [JsonPropertyName("source")] public string Source { get; set; } = "launcher";
}

// ==========================================
// 响应结构
// ==========================================
public class HgBatchResponse
{
    [JsonPropertyName("proxy_rsps")] public List<HgProxyResponse>? ProxyRsps { get; set; }
}

public class HgProxyResponse
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }

    [JsonPropertyName("get_latest_game_rsp")]
    public HgGetLatestGameRsp? GetLatestGameRsp { get; set; }

    [JsonPropertyName("get_banner_rsp")] public HgGetBannerRsp? GetBannerRsp { get; set; }

    [JsonPropertyName("get_announcement_rsp")]
    public HgGetAnnouncementRsp? GetAnnouncementRsp { get; set; }

    [JsonPropertyName("get_main_bg_image_rsp")]
    public HgGetMainBgImageRsp? GetMainBgImageRsp { get; set; }

    [JsonPropertyName("get_sidebar_rsp")] public HgGetSidebarRsp? GetSidebarRsp { get; set; }
}

// --- 版本信息 ---
public class HgGetLatestGameRsp
{
    [JsonPropertyName("action")] public int Action { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("pkg")] public HgPkgInfo? Pkg { get; set; }
    [JsonPropertyName("patch")] public HgPatchInfo? Patch { get; set; }
    [JsonPropertyName("pre_patch")] public HgPatchInfo? PrePatch { get; set; }
}

// --- 增量更新 / 预下载 ---
public class HgPatchInfo
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("md5")] public string? Md5 { get; set; }
    [JsonPropertyName("package_size")] public string? PackageSize { get; set; }
    [JsonPropertyName("total_size")] public string? TotalSize { get; set; }
    [JsonPropertyName("patches")] public List<HgPack>? Patches { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }

    [JsonPropertyName("v2_patch_info_url")]
    public string? V2PatchInfoUrl { get; set; }

    [JsonPropertyName("v2_patch_info_md5")]
    public string? V2PatchInfoMd5 { get; set; }
}

public class HgPatchManifest
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("vfs_base_path")] public string? VfsBasePath { get; set; }
    [JsonPropertyName("files")] public List<HgPatchFile>? Files { get; set; }
}

// --- 增量更新内容 ---
public class HgPatchFile
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("md5")] public string? Md5 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("diffType")] public int DiffType { get; set; }
    [JsonPropertyName("local_path")] public string? LocalPath { get; set; }
    [JsonPropertyName("patch")] public List<HgPatchNode>? Patches { get; set; }
}

public class HgPatchNode
{
    [JsonPropertyName("base_file")] public string? BaseFile { get; set; }
    [JsonPropertyName("base_md5")] public string? BaseMd5 { get; set; }
    [JsonPropertyName("patch")] public string? PatchPath { get; set; }
}

public class HgPkgInfo
{
    [JsonPropertyName("packs")] public List<HgPack>? Packs { get; set; }
    [JsonPropertyName("file_path")] public string? FilePath { get; set; }
}

public class HgPack
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("md5")] public string? Md5 { get; set; }
    [JsonPropertyName("package_size")] public string? PackageSize { get; set; }
}

// --- Banner ---
public class HgGetBannerRsp
{
    [JsonPropertyName("banners")] public List<HgBanner>? Banners { get; set; }
}

public class HgBanner
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("jump_url")] public string? JumpUrl { get; set; }
}

// --- 公告 ---
public class HgGetAnnouncementRsp
{
    [JsonPropertyName("tabs")] public List<HgAnnouncementTab>? Tabs { get; set; }
}

public class HgAnnouncementTab
{
    [JsonPropertyName("tabName")] public string? TabName { get; set; }
    [JsonPropertyName("announcements")] public List<HgAnnouncement>? Announcements { get; set; }
}

public class HgAnnouncement
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("jump_url")] public string? JumpUrl { get; set; }
    [JsonPropertyName("start_ts")] public string? StartTs { get; set; } // 时间戳字符串
}

// --- 背景图 ---
public class HgGetMainBgImageRsp
{
    [JsonPropertyName("main_bg_image")] public HgBgImageInfo? MainBgImage { get; set; }
}

public class HgBgImageInfo
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("video_url")] public string? VideoUrl { get; set; }
}

// --- Sidebar ---
public class HgGetSidebarRsp
{
    [JsonPropertyName("sidebars")] public List<HgSidebar>? Sidebars { get; set; }
}

public class HgSidebar
{
    [JsonPropertyName("media")] public string? Media { get; set; }
    [JsonPropertyName("pic")] public HgSidebarPic? Pic { get; set; }
    [JsonPropertyName("jump_url")] public string? JumpUrl { get; set; }
    [JsonPropertyName("sidebar_labels")] public List<HgSidebarLabel>? SidebarLabels { get; set; }
}

public class HgSidebarPic
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public class HgSidebarLabel
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("jump_url")] public string? JumpUrl { get; set; }
}

// --- 游戏完整性校验节点 ---
public class HgManifestNode
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("md5")] public string? Md5 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}
