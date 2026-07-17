> [!CAUTION]
> Please upgrade the plugin to version 1.1.0 or higher immediately!
> 
> It has been confirmed that all older versions below 1.1.0 suffer from a critical update bug. Issues within `SharpHDiffPatch.Core` prevent the plugin from performing effective patching, which in turn triggers the client to re-download approximately 30GB of data from the server.
> 
> This issue has been fully resolved in version 1.1.0. To avoid unnecessary bandwidth consumption and long download times, please be sure to update your plugin immediately!

<p align="center">
  <img width="512px" height="auto" src="./.github/assets/CollapseLauncherIdolType.png"/>
</p>

<div align="center">

# Hi3Helper.Plugin.Hypergryph

**English** · [简体中文](./README.zh-CN.md)

A third-party plugin developed for [Collapse Launcher](https://collapselauncher.com/), designed to support the
downloading, updating, and launching of games published by **Hypergryph**.

**Plugin Status**: Currently, all basic features have been implemented. Extended features await official support and
updates from Collapse.

You can download the plugin via the [Official Collapse Launcher Website](https://collapselauncher.com/plugin/catalog.html) or [My Plugin Website (Recommended)](https://cl-plugins.sakurakoi.top/).

<img width="80%" alt="Plugin Preview" src="https://github.com/user-attachments/assets/f3f572c0-bfb7-4436-b8e7-47765f42c052" />

</div>

<p align="center">
  <a href="https://github.com/palmcivet/awesome-arknights-endfield"><img src="https://github.com/palmcivet/awesome-arknights-endfield/blob/main/assets/badge-for-the-badge.svg" alt="Awesome Arknights Endfield badge" /></a>
  <a href="https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/graphs/contributors" target="_blank"><img alt="GitHub contributors" src="https://img.shields.io/github/contributors/misaka10843/Hi3Helper.Plugin.Hypergryph?style=for-the-badge&logo=github"></a>
  <a href="https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/stargazers" target="_blank"><img alt="GitHub Repo stars" src="https://img.shields.io/github/stars/misaka10843/Hi3Helper.Plugin.Hypergryph?style=for-the-badge&label=%E2%AD%90STAR"></a>
</p>

-----

>[!IMPORTANT]
>This plugin is not officially maintained by Collapse. Please do not submit issues to the official Collapse repository or official Discord.
>
>Please prioritize submitting issues in this repository. Submitting issues through other channels will not receive immediate support!
>
>Self-update is currently unstable. Please perform a manual update by downloading the latest release. A fix is in development and will be implemented in the next version.

**If this plugin helps you, consider giving it a ⭐!**

## ✨ Features

### ✅ Currently Supported

- **Version Detection**: Automatically detects if the client version is up to date.
- **Information Retrieval**: Automatically pulls and displays official background images, banners, and the latest
  news/announcements.
- **Game Management**: Supports complete game downloading, installation, launching, and process detection.
- **Game Update**: Supports updating the game.
- **Multi-Server Support**
- **Incremental Game Updates**: Current incremental game update is a beta feature, which may lead to update
  failures/errors/file corruption. Please back up game files before updating.
- **Integrity Verification**: Automatically performs integrity verification and game repair after an update.

### 🚧 Development Plan / ToDo

- [x] **Pre-download Support**: Awaiting the official launcher to implement relevant interfaces.
- [ ] **Manual Integrity Check**: Collapse Launcher does not seem to provide relevant API interfaces for manual
  verification; awaiting upstream updates.
- [ ] **Social Media Panel**: Integrate official social media feed displays (Basic support exists, but currently
  disabled as icons cannot be retrieved via API).
- [x] **Incremental Game Updates**: Awaiting official Collapse support for incremental updates; will begin coding
  related functions once supported.

-----

## 🧩 How to Install the Plugin

**Prerequisites:**
Before using this plugin, please ensure your Collapse Launcher version is `1.83.14` or higher.

### Installation Steps

1. **Download the Plugin**
   Go to the [Releases page](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/releases/latest) and download
   the latest plugin archive (`.zip` file).

    ![Release Download Page](./.github/assets/img.png)

2. **Enter Plugin Management**
   Open the launcher, go to the **Settings** page, scroll down, and click `Open Plugin Management Menu`.

   ![Settings Menu](./.github/assets/img_2.png)

3. **Add and Apply**
   In the pop-up window, click the `Click to add .zip or manifest.json` button and select the `.zip` file you just
   downloaded. After adding, **restart the launcher** for the changes to take effect.

   ![Add Plugin Dialog](./.github/assets/img_1.png)

-----

## ⚠️ Disclaimer

This project is a third-party open-source plugin and is not affiliated with *GRYPHLINE* or *Hypergryph*.