# RimLazy

RimLazy 的目标是让玩家设定战略，由 MOD 协调殖民地的日常生产。第一个可玩的功能是工作分配：点击一次，为全体殖民者生成工作优先级，显示目标、预计投入和无法满足的原因。库存、生产订单和种植规划可以逐步接入同一套需求模型。

**目标游戏：RimWorld 1.7。当前阶段：开发环境和数据契约。** 仓库有可独立编译的 .NET 10 核心项目、战略预设样例和测试；尚未实现求解器，也没有可安装到游戏里的 MOD。

| 从哪里开始 | 内容 |
| --- | --- |
| [Phase 0 设计](docs/phase-0.md) | 工时口径、玩家锁定、订单和工位约束、离散优先级、实机验收 |
| [1.7 API 核对记录](docs/api-evidence.md) | 从固定版本参考程序集读取的签名，以及仍需实机验证的行为 |
| [开发环境与测试顺序](docs/development.md) | MCP、技能、SDK、Steam 分支、本机 MOD 兼容线索 |
| [原始 handoff](RimWorld_Codex_Development_Handoff_2026-11-12.md) | 用户确认的产品边界和测试案例 T01–T17 |

从仓库根目录运行 `./scripts/test.ps1`；其他平台安装 .NET 10 SDK 后可运行 `dotnet test RimLazy.slnx -c Release`。首次还原测试框架需要 NuGet。当前测试验证“全科研、零生存”的 JSON 预设能保留用户设置，不需要游戏、Steam 或联网推理。测试素材放在 `tests/fixtures/synthetic/`，本机真实快照放在 Git 忽略的 `.local/`。
