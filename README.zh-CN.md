<p align="center">
  <img width="512px" height="auto" src="./.github/assets/CollapseLauncherIdolType.png"/>
</p>

<div align="center">

# Hi3Helper.Plugin.Hypergryph

[English](./README.md) · **简体中文**

为 [Collapse Launcher (Collapse 启动器)](https://collapselauncher.com/) 开发的第三方插件，旨在支持 **鹰角网络**
发布的游戏下载、更新与启动。

**插件当前状态**: 当前基本功能均已实现，扩展功能需要等待官方支持和Collapse的更新

您可以通过[Collapse Launcher官方](https://collapselauncher.com/plugin/catalog.html)
或[本人插件网站(推荐)](https://cl-plugins.sakurakoi.top/)下载插件

<img width="80%" alt="Plugin Preview" src="https://github.com/user-attachments/assets/f3f572c0-bfb7-4436-b8e7-47765f42c052" />

</div>

<p align="center">
  <a href="https://github.com/palmcivet/awesome-arknights-endfield"><img src="https://github.com/palmcivet/awesome-arknights-endfield/blob/main/assets/badge-for-the-badge.svg" alt="Awesome Arknights Endfield badge" /></a>
  <a href="https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/graphs/contributors" target="_blank"><img alt="GitHub contributors" src="https://img.shields.io/github/contributors/misaka10843/Hi3Helper.Plugin.Hypergryph?style=for-the-badge&logo=github"></a>
  <a href="https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/stargazers" target="_blank"><img alt="GitHub Repo stars" src="https://img.shields.io/github/stars/misaka10843/Hi3Helper.Plugin.Hypergryph?style=for-the-badge&label=%E2%AD%90STAR"></a>
</p>

---

> [!IMPORTANT]
> 此插件并不是Collapse官方维护的，所以请不要前往Collapse的官方仓库提交issue和前往官方Discord提交issue
>
> 请优先在此仓库中提交issue，在其他的渠道提交issue将不会第一时间得到支持！
>
> 当前自更新有网络问题，请不要依靠自更新，而是重新下载对应最新版本插件，我正在编写解决方案，将在下个版本实施

**如果这个插件帮到了您，还请您点个⭐来支持我！**

## ✨ 功能特性

### ✅ 当前已支持

- **版本检测**：自动检测客户端版本是否为最新。
- **资讯获取**：自动拉取并展示官方背景图、Banner 以及最新新闻公告。
- **游戏管理**：支持完整的游戏下载、安装、启动及运行检测。
- **游戏更新**：支持更新游戏。
- **多服支持**
- **增量游戏更新**：当前增量游戏更新为beta功能，可能会导致更新失败/报错/游戏文件损坏等问题，请在更新前备份游戏文件。
- **完整性校验**：在更新后会自动进行完整性校验与游戏修复。

### 🚧 开发计划 / 待办事项 (ToDo)

- [ ] **预下载支持**：需等待官方启动器实装相关接口。
- [ ] **手动完整性校验**：手动校验目前Collapse启动器似乎暂未提供相关 API 接口，需等待上游更新。
- [ ] **社媒面板**：集成官方社交媒体动态展示。(现基本支持，但是因为icon无法通过api获取所以暂时关闭)
- [x] **增量游戏更新**：等待官方启动器支持增量更新，在官方启动器支持后会着手编写相关功能。

---

## 🧩 如何安装插件

**前置要求：**
在使用本插件前，请确保您的 Collapse Launcher 版本为 `1.83.14` 或更高版本。

### 安装步骤

1. **下载插件**
   前往 [Releases 页面](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/releases/latest) 下载最新的插件压缩包（
   `.zip` 文件）。

   ![Release Download Page](./.github/assets/img.png)

2. **进入插件管理**
   打开启动器，进入 **设置 (Settings)** 页面，向下滚动找到并点击 `打开插件管理菜单`。

   ![Settings Menu](./.github/assets/img_2.png)

3. **添加并应用**
   在弹出的窗口中，点击 `点击添加 .zip 或 manifest.json` 按钮，选择刚刚下载的 `.zip` 文件。

   完成添加后，**重启启动器**即可生效。

   ![Add Plugin Dialog](./.github/assets/img_1.png)

---

## ⚠️ 免责声明

本项目是第三方开源插件，与 _GRYPHLINE_ 或 _Hypergryph_ 无关。
