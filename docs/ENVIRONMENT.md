# Terraria Agent 环境检查

检查日期为 2026 年 10 月 4 日，时区 Asia/Shanghai。以下是文件、目录、工具和官方来源检查的结果；尚未启动 Terraria 或桥接，也没有游戏验收成绩。用户已选择原版 1.4.5.8 加轻量桥接，tModLoader 的检查仅保留为备选路线资料。

## 本机发现

| 项目 | 已检查结果 | 证据与限制 |
| --- | --- | --- |
| 原版 Terraria | 已安装，1.4.5.8 | `E:\SteamLibrary\steamapps\common\Terraria\Terraria.exe` 和服务器 EXE 的文件版本一致；游戏菜单版本未核对 |
| Steam build | 24893155 | 原版 app manifest 105600；不是 tModLoader build |
| tModLoader | 已知位置未发现安装 | 两个已知 Steam 库没有 app manifest 1281930 或 common 安装目录；不能排除其他位置便携版 |
| Git | 2.49.0.windows.1 | 可运行；系统已带 Git Credential Manager |
| GitHub 连接 | 已连接 Thecats-Jfm | 插件支持仓库读写，但未提供新建仓库操作；本机 Git 没有可用的 GitHub 登录凭据 |
| GitHub CLI | 未在 PATH 找到 | 不是模组和规则控制器的运行依赖；可用插件与 Git 完成仓库管理 |
| .NET SDK | 9.0.201 | 另有 .NET 8.0.14 和 9.0.3 运行时；已找到 .NET Framework 4.8 开发引用，匹配原版 Core 的 net48；实际构建未执行 |
| FFmpeg | 7.1.1 | 列出 gdigrab、dshow 与 libx264 编码器；未录制游戏、未验证播放 |
| Python | 只有 WindowsApps 命令入口 | 未认定为可用 Python 运行时；第一版计划不依赖 Python |
| Windows 操作 | 窗口枚举成功 | 可识别 Steam 等窗口；不代表 Terraria 输入或画面捕获通过 |
| 现有存档 | 本地 2 人物、2 世界及伴随备份；云端 remote 目录存在 | 只读目录元数据，未解析角色/世界/地图内容，没有修改，也尚未执行备份 |

## tModLoader 版本与官方 API

本节是备选资料，其中 ModPlayer/ModSystem 与 tmlsavedirectory 仅适用于 tModLoader，不能应用到当前原版桥接。当前候选 Core、加载器与安全边界见 [源码安全审查](SECURITY_REVIEW.md)，空间见 [磁盘预算](DISK_BUDGET.md)。

候选 stable release 为 [v2026.08.3.0](https://github.com/tModLoader/tModLoader/releases/tag/v2026.08.3.0)，发布于 2026 年 10 月 1 日，属于 Terraria 1.4.4 基线。官方的 [1.4.5 移植跟踪](https://github.com/tModLoader/tModLoader/issues/5070) 说明版本过渡情况。候选版本不等于本机安装版本，安装之后必须重新锁定并复核。

固定候选 tag 的官方源码已核实以下声明：

```csharp
public virtual void SetControls()
public virtual void PostUpdateEverything()
```

`ModPlayer.SetControls()` 修改本地角色输入，在 `PreUpdate` 后调用，仅本地客户端执行。文字输入状态也可能触发，需要显式禁止 Agent 输入。参见 [固定版本 ModPlayer 源码](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/ModLoader/ModPlayer.cs#L240-L246) 和 [官方 ModPlayer 文档](https://docs.tmodloader.net/docs/stable/class_mod_player.html)。

`ModSystem.PostUpdateEverything()` 位于网络更新之后，是完整游戏更新的最后 hook，客户端与服务器都可能调用。部分更新或暂停不能按每一渲染帧估计调用频率。通信和模型调用不得阻塞该 hook，TTL 要使用单调真实时间并在输入应用时检查。参见 [固定版本 ModSystem 源码](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/ModLoader/ModSystem.cs#L228-L232) 和 [官方 ModSystem 文档](https://docs.tmodloader.net/docs/stable/class_mod_system.html)。

官方 [命令行参数](https://github.com/tModLoader/tModLoader/wiki/Command-Line#tmlsavedirectory-pathtosavedirectoryfolder) 支持 `-tmlsavedirectory <独立绝对目录>`，与 `-savedirectory` 的目录追加行为不同。实际 SavePath、云端目录影响与存档隔离仍需启动后验证。

## 尚未验证

目前没有完成桥接编译、游戏启动、输入控制、停止/接管、连续游戏录屏、备份校验或 A/B/C/D 中的任何实机验收。下一阶段先锁定原版加载器与桥接的构建和依赖，再验证 A；如果实机操作不可用，将及时记录具体阻塞，不能以编译成功代替游戏验证。
