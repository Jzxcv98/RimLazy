# RimLazy 开发环境与实机测试

## 2026-11-12 的本机基线

| 项目 | 已验证 |
| --- | --- |
| 游戏 | D 盘 Steam 库中 `RimWorld/Version.txt` 为 **1.6.4871 rev590**，Steam BuildID `23969874`。1.7 尚未用于运行本工程。 |
| [RimWorld Mod MCP](https://github.com/mushroomTW/rimworld-mod-mcp) | Windows x64 v0.3.1；[发布文件的 SHA-256](https://github.com/mushroomTW/rimworld-mod-mcp/releases/expanded_assets/v0.3.1) 已核对。按 `rimworld` 注册到 Codex，使用 `stdio`。握手、19 个工具的发现和 `rimworld_status` 已通过；明确识别 1.6 不受支持，未对其建索引。 |
| [RimWorld Modding Skill](https://github.com/Kon-on/rimworld-modding-skill/tree/41f609d4395b1f8c272052768f52b1e4569b407f) | 1.2.0，安装在 Codex 用户技能目录的 `rimworld-modding/`，自带索引校验已通过。使用本地 MCP 即可；不需要额外配置 RimSage。 |
| [微软 .NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | 10.0.404，核对微软 SHA-512 后安装到 `%LOCALAPPDATA%/RimLazy/dotnet`。工作区脚本和 MCP 使用此 SDK，无须替换系统的 .NET 8/9。MSTest 只用于开发，不是玩家要装的游戏依赖。 |
| Git | 已有 2.51.0，沿用现有工作区、分支及远端。 |

本机 MCP 的可执行文件放在 `%LOCALAPPDATA%/RimLazy/tools/rimworld-mod-mcp/v0.3.1/`，独立于 Git 检出位置。服务器环境中显式设定 `RIMWORLD_MOD_MCP_GAME_PATH` 为 Steam 游戏根目录，并为构建子进程设置 .NET 10 路径。更换 Steam 库或迁移机器时，更新这些设置，再用 `codex mcp get rimworld`、`rimworld_status` 检查。新工具未显示时重新打开 Codex 会话。

## 先检验哪些 MOD

当前 **1.6** 的 `ModsConfig.xml` 有 **294 个启用条目**，包括 Core、5 个 DLC 和 288 个第三方条目（含语言包）。在第三方条目中，155 个本机清单列出 1.7，128 个没有列出，5 个未匹配到清单。逐条加载顺序和清单匹配结果保留在本机 `.local/mod-inventory-1.6.json`。以游戏实际加载的 Def 和代码为准，Workshop 声明不是通过测试的证明。

| 本机条目 | 发现与测试顺序 |
| --- | --- |
| `frozensnowfox.complexjobs` · [FSF] Complex Jobs | 本机带 1.7 文件，例如核心 XML 新增护理 `FSFNurse` 和手术 `FSFSurgeon` 两列。先用它验证工作发现、未映射提示和分列资格。 |
| `lauriichan.prioritymaster` · PriorityMaster | 本机 About 只列到 1.6，版本目录亦止于 1.6。首次 1.7 测试不加载它。确认有适配版后另测档位和写回。 |
| `soulwolf.ch.prioritymod` · PriorityMaster_zh | 上项的汉化包；其 About 标注的版本不代表本体可运行。 |

## 切换到 1.7 后

1. 按 [Ludeon 官方说明](https://store.steampowered.com/news/app/294100/view/672882362547178633) 在 Steam 游戏属性的测试版中选 `unstable`。如出现启动选项，选 `Play 1.7 Unstable`。不需要打开日常的 1.6 存档。
2. 检查实际 1.7 安装路径与构建号。只有 MCP 状态确认识别到 1.7 才运行一次 `rebuild_index`；等源码索引完成再查工作选择逻辑。比对 [参考包证据](api-evidence.md) 与这台机器上该构建的接口。
3. 在隔离的新测试档中先用原版基线。导出一张小殖民地快照，对照禁令、两个同档工作、一个有限订单及一个共享工位。实现写回后验收应用、保存、读档、撤销。
4. 在同样的隔离环境分批加入已验证支持该 1.7 构建的工作扩展。比较操作前后的工作列、档位和估计工时。游戏文件、反编译输出、工具和玩家存档不提交到仓库。
