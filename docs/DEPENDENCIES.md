# A 阶段版本与依赖

当前构建对象为本机 Windows Steam 原版 Terraria 1.4.5.8。自有加载器只加载固定游戏副本、自有桥接及固定 Harmony；不使用上游 Injector、TerrariaModder Core、Vault、DebugTools 或其他游戏模组。借鉴已审查项目的接入路线，游戏方法与更新顺序另由本机静态元数据核实，见 GAME_API。

| 依赖 | 固定值和用途 |
| --- | --- |
| 游戏 | 1.4.5.8；EXE SHA-256 `960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3`；Host 拒绝其他构建 |
| 构建工具 | 本机 .NET SDK 9.0.201；Host/Bridge 为 net48 x86，Controller 为 net8.0 |
| .NET Framework | 本机 4.8 开发引用和运行环境，不在线恢复引用程序集包 |
| XNA | 本机 GAC_32 的 4.0 引用；实际运行可用性仍待游戏启动验证 |
| Harmony | 官方 NuGet `Lib.Harmony 2.3.3`，net48 组没有额外显式 NuGet 依赖 |
| 录像 | 本机 FFmpeg/ffprobe 7.1.1；窗口录制与播放尚未实测 |

Harmony 包下载地址是 `https://api.nuget.org/v3-flatcontainer/lib.harmony/2.3.3/lib.harmony.2.3.3.nupkg`。下载包 SHA-256 为 `87B63DDB92F04FCB89C30B7EBAE473C948DD65E80569EB94AEF08B163BC3BF63`；nuspec 记录源码提交 `395749ff507ab4e2cd4f84cf99085ec18f6870bc`，项目为 [Harmony 官方仓库](https://github.com/pardeike/Harmony)。包内存在 NuGet 签名，但本次未声明证书链或签名验证通过，也没有进行全面漏洞扫描。

包只保存在工作区 `work/dependencies/feed/`，构建脚本使用离线源，其他包源被清除。仓库不提交包、游戏、XNA 或编译二进制。不缺省下载模型，不调用游戏运行时模型 API；当前决策方式为规则。

构建前校验上述包哈希；构建后的 net48 `0Harmony.dll` 及 Host 启动前部署 DLL 都要求 SHA-256 为 `498EDE1D20A87AFAA6C7B1ED5B3BEB505B01DC418B952C1BE5C322204404B033`。此值从固定下载包内对应 DLL 字节计算，用来发现缓存或部署变化，不代表源码到发行二进制的可复现构建已经核验。
