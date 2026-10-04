# Terraria Agent GitHub 配置

公开仓库 [Thecats-Jfm/terraria-agent](https://github.com/Thecats-Jfm/terraria-agent) 已首次同步，默认分支 `main`。GitHub connector 实际核实了公开可见性、`push=true`、`admin=true` 权限及登录账号 `Thecats-Jfm`。先初始化 README，再创建完整源码树、提交并以非强制方式更新 main；完整源码提交为 `9eab61e23a4871b36dd9fd8be31113861d0df11c`，随后已通过连接器重新读取该提交。

本地项目在工作区新目录 `outputs\terraria-agent`。为公开提交使用账号的 GitHub noreply 邮箱，配置只作用于这个仓库，不改全局 Git 身份。源码和文档纳入 Git；存档、备份、游戏二进制、凭据、本机配置、原始日志和录像由 `.gitignore` 排除。

首推前 CLI 的 `git ls-remote` 曾退出为 0 并返回空 refs。随后非交互 `git push` 因没有可用凭据而失败，没有弹出认证窗口；已改用连接器完成同步。原生 Git 的后续 fetch 遇到连接重置，一次带低速超时的 HTTP/1.1 重试也连接失败。连接器同步成功不代表原生 Git 推送认证或网络已正常。

早期准备阶段曾遇到浏览器读取及 GitHub 连接超时，Git Credential Manager 的设备登录尝试也未完成。上述内容属于历史尝试；当前实际查询已确认仓库存在且可访问，不再将网页创建或网络不可访问记为当前阻碍。未根据插件登录结果假定原生 Git 凭据已建立。

首次同步的 48 个 UTF-8 文本文件逐个从本地 Git 暂存区导出并核对 blob SHA；远程源码树 `99277ed27cc44bc010e8b8012da538a7bb7b2d39` 与本地核查快照完全一致。原存档、版权游戏文件、凭据、私人路径、截图和原始日志均未上传。本地旧历史保留在 `local/pre-public-history-20261004`，没有推送；本地公开 main 根提交 `81e9b4830ba60838ef9608995c60d4b6c5f69c2d` 与首轮远程源码树相同，但提交历史不同。原生 fetch 恢复后应先核对远程源码树并对齐历史，不能强制覆盖远程 main，也不能推送本地历史备份分支。

本文件记录首次同步事实；后续文档更正会产生新提交，不改变首次完整源码提交的标识。A 尚未通过，没有创建阶段通过标签。

后续按 A、B、C、D 分别提交代码和验收摘要。只有真实通过的阶段才创建通过标签。自动测试与 CI 必须注明验证范围，不能把构建成功显示为 Boss 或采集任务通过。
