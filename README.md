# FServer FTP

[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)](https://github.com/landeyucc/fserver)
[![Runtime](https://img.shields.io/badge/runtime-.NET%20Framework%204.8-512BD4)](https://dotnet.microsoft.com/download/dotnet-framework/net48)
[![Rust](https://img.shields.io/badge/rust-stable-DEA584)](https://www.rust-lang.org/)
[![License](https://img.shields.io/badge/license-MIT-blue)](./LICENSE)

一款面向个人与小团队的**轻量级 FTP 服务器**。Rust 协议栈保障性能与内存安全，现代化 WPF 面板让配置、用户管理、流量统计一目了然。

 · [GitHub](https://github.com/landeyucc/fserver) · [Releases](https://github.com/landeyucc/fserver/releases)

---

## ✨ 特性

- **Rust 原生协议栈** — 基于 [libunftp](https://github.com/bolcom/libunftp) + tokio，零拷贝文件 IO，内存安全
- **多用户隔离** — 独立根目录、读写权限位、单 IP 连接数限制；SQLite 加密存储
- **现代化 WPF 面板** — 深/浅双主题，实时日志、在线会话、流量统计一屏全览
- **托盘静默运行** — 支持静默启动、HKCU 注册表开机自启（无管理员）
- **真实流量统计** — 断点续传片段不计入，`daily_stats` 严格累加完整传输大小
- **单实例锁** — Named Mutex 全局唯一，二次启动自动提示

---

## 🖥️ 系统要求

| 项 | 要求 |
|---|---|
| **操作系统** | Windows 10 1903+ / Windows 11 / Windows Server 2019+（x64）|
| **运行时** | .NET Framework 4.8（Win10 1903+ 默认已装）|
| **安装权限** | 用户级（`{autopf}`，无需管理员）|
| **默认端口** | `2121`（被动端口 `49152–65535` 自动分配）|

---

## 🚀 快速开始

### 方式一：安装包（推荐）

从 [Releases](https://github.com/landeyucc/fserver/releases) 下载 `FServer-FTP-setup-*.exe`，双击安装，打开面板 → 新增用户 → 点击「启动 FTP」即可。

### 方式二：从源码构建

**前置条件**：Rust 工具链（1.70+）、.NET SDK 8+（目标 net48）。

```powershell
# Release 完整构建（Rust + .NET + publish + zip）
.\build.ps1

# Debug 开发版
.\build.ps1 -Dev

# 全量重建
.\build.ps1 -Clean

# 编译 Inno Setup 安装包
ISCC.exe script\setup-FServer.iss
```

产物：

```
publish\FServer.App\Release\   # 可直接运行目录（无需安装）
release\                       # setup 安装包
```

---

## 🏗️ 架构

双层分离：协议栈与 UI 解耦，未来换面板或协议栈互不影响。

```
┌─────────────────────────────────────────────────────────────────┐
│               FServer.App.exe  (C# / WPF / net48)                │
│                                                                 │
│  MainViewModel        SettingsRepository        RegistryHelper   │
│  (配置 / 用户 / 统计)   (SQLite)               (开机自启 HKCU)   │
│                          ▲                                       │
│                          │ P/Invoke (FFI)                        │
└──────────────────────────┼──────────────────────────────────────┘
                           │
┌──────────────────────────┼──────────────────────────────────────┐
│               ftp_core.dll  (Rust / tokio)                       │
│                          │                                       │
│  ReadOnlyWrapper  →  libunftp StorageBackend (多用户根隔离)        │
│  SimpleDataListener → DataEvent → SESSION / TRANSFER / LOG       │
└─────────────────────────────────────────────────────────────────┘
```

| 层 | 职责 |
|---|---|
| **Rust ftp_core** | 监听端口、处理会话、验证用户、读写文件；DataListener 事件流推送 C# |
| **C# WPF App** | 控制面板 / 托盘 / 配置持久化；双通道接收 Rust 事件（会话级 + 传输完成）|
| **SQLite** | `fserver.db` 存 AppSettings / Users / daily_stats |
| **FServer.Icons.dll** | 独立图标嵌入，托盘 Idle ↔ Active 切换 |

---


## 🔌 FTP 统计口径（设计说明）

FileZilla / FlashFXP 常使用 REST + RETR **断点续传**，一个完整文件可能被拆成 N 段传输——每段都会触发一条 `SESSION:DATA:DOWNLOAD` 事件，旧实现里这段直接累加进 daily_stats 会被多算 N 倍。

本项目通过**双通道事件**精确统计：

| 事件类型 | 来源 | 用途 | 是否进 daily_stats |
|---|---|---|---|
| `SESSION:DATA:*` | DataListener（所有片段）| 实时传输活动 Tab 显示 | ❌ |
| `SESSION:DOWNLOAD_COMPLETE` / `UPLOAD_COMPLETE` | ReadOnlyWrapper（仅 start_pos==0）| daily_stats 统计口径 | ✅ |

即 `daily_stats` 统计的是**完整文件传输次数 + 完整文件总大小**，续传片段只在传输活动 Tab 可见。

---

## 📄 许可证

[MIT LICENSE](./LICENSE) 
