# 分工设计的攻略与代码核对

查阅日期为 2026-11-12。正文方案见[玩家输入与自动分配](work-assignment-factors-1.7.md)。以下 24 个攻略、社区条目及玩家实践页面用于找实际问题。文末单列模组作者资料。涉及游戏行为的技术判断均以本机 **1.7.5005 rev1082** 游戏定义和程序集为准。网页抓取或近期修改时间不能证明作者在 1.7 测试过。

## 这次实际阅读的网上资料

| # | 来源、适用范围 | 对分工设计有何启发 |
| ---: | --- | --- |
| 1 | 2026 年 10 月玩家讨论，具体构建未注明。[玩家如何管理工作优先级](https://www.reddit.com/r/RimWorld/comments/1xg96mr/how_do_you_manage_your_pawns_work_priorities/) | 玩家用指定工人、技能范围、原料半径和成品落点控制生产。RimLazy 应读取这些设定。 |
| 2 | 2026-10-29 发表，作者明确以 1.6 为机制基准。[殖民地开局与意识形态](https://www.mneurix.quest/news/rimworld-colony-start-ideology-guide/) | 开局、专家与殖民地阶段是偏好示例。所需战略应允许玩家混合。发布日期与适用版本应分别记录。 |
| 3 | 中文综合指南，未注明验证的 1.7 构建。[工作优先级与效率分工](https://rimworld.yxhtl.com/rimworld-guide-work.html) | 资格、材料、路线和布局会让“亮黄技能框”无法兑现。攻略的开局模板不应变成算法的强制保底。 |
| 4 | PC 1.6.4850，无 DLC，作者说明为资料整理。[殖民者为什么不工作](https://prenoca.com/en/games/rimworld/guides/rimworld-work-priority/) | 适合设计诊断顺序。先看人能否做，再看区域、目标和材料。 |
| 5 | 早期玩家实战文章，具体玩法用作场景。[搬运与清洁](https://steamcommunity.com/sharedfiles/filedetails/?id=1787398171) | 拆墙之后来回搬少量材料，可能比换一个更高建筑技能的人浪费更多时间。 |
| 6 | Wiki 专题，未声明统一的 1.7 构建。[工单 Bill](https://rimworldwiki.com/wiki/Bill) | 配方次序、允许材料、取料半径、技能范围和成品去向都应进入任务估算。 |
| 7 | RimWorld Access 作者文档，描述其使用界面。[Bills](https://rimworldaccess.com/building/bills/) | 同一“做衣服”的目标可以有不同停止条件和运送安排。这些条件应从工单继承。 |
| 8 | Wiki 工作专题。[Work](https://rimworldwiki.com/wiki/Work) | 一张表表达尝试工作顺序。原版说明中的四档不覆盖扩展工作表。 |
| 9 | Wiki 入门攻略。[Basics](https://rimworldwiki.com/wiki/Basics) | 原料架、成品架和低优先级后备提供了实用场景。该页将无火学习倍率写成 33%，本机原版代码为 35%。 |
| 10 | Wiki 搬运专题，页面标有待扩充。[Hauling](https://rimworldwiki.com/wiki/Hauling) | 是否有搬运目标与搬运效率是两个问题。物品和目的地必须配对。 |
| 11 | Wiki 属性资料。[Move Speed](https://rimworldwiki.com/wiki/Move_Speed) | 人类种族基值与通用 StatDef 默认值不同。地点和走路条件也影响结果。 |
| 12 | Wiki 研究专题。[Research](https://rimworldwiki.com/wiki/Research) | 已选项目、可用研究台和研究员共同决定能否产生科研点。 |
| 13 | Wiki 属性资料。[General Labor Speed](https://rimworldwiki.com/wiki/General_Labor_Speed) | 并非所有加工都按工艺技能提速，有些配方主要在品质上区分工匠。 |
| 14 | Wiki 技能资料。[Skills](https://rimworldwiki.com/wiki/Skills) | 技能门槛、当期效果和学习是不同用途，需回到实际任务判断。 |
| 15 | Wiki 培养攻略，页面标注有待核实内容。[Training](https://rimworldwiki.com/wiki/Training) | 用低风险日常工作培养后备是合理意图。实际收益以任务经验和学习倍率核算。 |
| 16 | Wiki 灵感专题。[Mental inspiration](https://rimworldwiki.com/wiki/Mental_inspiration) | 持续型效果和下一次行为消耗的机会需要不同处理。 |
| 17 | Wiki 品质专题。[Quality](https://rimworldwiki.com/wiki/Quality) | 产物能否评品质、由谁完工，以及专家与一次性创作效果都值得区分。 |
| 18 | Wiki 厨师个人属性专题。[Food Poison Chance](https://rimworldwiki.com/wiki/Food_Poison_Chance) | 个人污染概率、房间污染、食客发病概率是三种量。 |
| 19 | Wiki 医疗专题。[Medical Surgery Success Chance](https://rimworldwiki.com/wiki/Medical_Surgery_Success_Chance) | 医生面板值属于手术结果的一个因素。配方、患者和工位仍要核对。 |
| 20 | Wiki 收获专题。[Plant Harvest Yield](https://rimworldwiki.com/wiki/Plant_Harvest_Yield) | 原料产出不能用种植速度代替。药用植物的属性另有入口。 |
| 21 | Wiki 动物专题。[Tame Animal Chance](https://rimworldwiki.com/wiki/Tame_Animal_Chance) | 动物目标影响成功率。同一名工人面对不同目标会得到不同估计。 |
| 22 | 作者明确列出 1.6.4633 数值。[厨房、冰柜和取料布局](https://rimworldhub.com/post/how-to-make-kitchen-rimworld) | 短走线与洁净度对厨房产生不同影响，其数值用本机 1.7 定义复核。 |
| 23 | 中文玩家自述，发表于 2022-01-23，含作者所用 MOD。[环世界工作优先级](https://www.bilibili.com/opus/618838339922822920) | 主责、后备与开局抢收体现玩家战略。作者使用的工种与档位不能当作通用规则。 |
| 24 | 玩家以不同种植布局讨论，具体构建未注明。[为肥沃土多走多少格值得](https://www.reddit.com/r/RimWorld/comments/1ldmd4s/how_many_tiles_of_walking_is_fertile_soil_really/) | 播种、收获和运输频率随作物与布局变化，固定“远田最多几格”阈值难以泛用。 |

单列的一手资料包括三份有 1.7 信息的模组作者说明。Food Safety Controls 作者明确记录在 1.7.4977 unstable 上试验，并能修改厨艺分级的污染率。因此固定的原版数值只适合当回归样例。WorkRoles 和 Adaptive Work Priorities 的作者页面标注支持 1.7，提供角色、手工保护、扩展工种或学习的不同产品做法。这些用于了解兼容需求。[Food Safety Controls](https://steamcommunity.com/sharedfiles/filedetails/?id=3814602056) · [WorkRoles](https://steamcommunity.com/sharedfiles/filedetails/?id=3760146134) · [Adaptive Work Priorities](https://steamcommunity.com/sharedfiles/filedetails/?id=3792993740)

官方 DLC 资料用来核对基因与角色的适用背景。VEF 作者文档说明有的服装会赋予特性、修改能力或禁止工作。角色面板上确实存在一个特性，也不能据此认定它永久属于人物。[Biotech](https://rimworldgame.com/biotech/) · [Ideology](https://rimworldgame.com/ideology/) · [VEF 装备扩展](https://github.com/Vanilla-Expanded/VanillaExpandedFramework/wiki/ApparelExtension)

## 1.7.5005 实际代码证据

本机 `Assembly-CSharp.dll` SHA-256 为 `82387f78262461e53bb06a67c3945912b876d1fdb07235122bbb6e59d073f125`。以下为本机 RimWorld Mod MCP 反编译源码索引的路径、方法与便于复查的行号，其他构建的行号可能不同。只列机制结论，不随仓库分发游戏源码。

| 代码入口 | 在这个构建里读到的行为 | 对设计的约束 |
| --- | --- | --- |
| `RimWorld/StatWorker.cs`，`GetValueUnfinalized` 393–396，`FinalizeValue` 798 起 | 当前灵感的属性偏移和乘数已进入 Stat。还有衣物、能力、属性部件、曲线、其他属性因子等阶段。 | 读取有效移速后不可再乘移动狂热。反推脱装备数值时也不能统一减一个最终面板偏移。 |
| `RimWorld/Inspiration.cs`，`AgeDays`、`InspirationTick` 48 起 | 状态随时间增长，达到定义的持续天数后结束。 | 系统能自行估计当前持续效果的覆盖时间，未知的自定义行为要标明估计。 |
| `Verse/Pawn.cs`，`TicksPerMove` 1965 起。`Verse/AI/Pawn_PathFollower.cs`，`CostToMoveIntoCell` | 先取有效移速。爬行、约束、抱人、室外天气、斜向、路径代价等仍有各自规则。 | 移动影响行程，不提高整项工作所有步骤的速度。 |
| `Verse/Pawn_CarryTracker.cs`，`MaxStackSpaceEver` 63 起 | 一次可搬数量受堆叠上限与可搬容量相对单位体积限制。 | 一件物品和一批材料要分别计算节省的趟数。 |
| `Verse/AI/Pawn_JobTracker.cs`，`DetermineNextJob`。`RimWorld/JobGiver_Work.cs`，`GetPriority` 19–46 | 思考树选择工作与其他行为。Work、Anything、Sleep 等时间安排赋予工作分支不同优先程度。 | 预计可劳动时间结合日程和需要。 |
| `RimWorld/Pawn_WorkSettings.cs`，`CacheWorkGiversInOrder` 207 起。`WorkTypeDef.ResolveReferences` | 启用列按玩家档位及 `naturalPriority` 排序，列内按实际工作提供者排序，另有紧急列表。 | 当前游戏表格和扩展档位是写回依据。档位不表达工时比例。 |
| `RimWorld/JobGiver_Work.cs`，`TryIssueJobPackage`、`PawnCanUseWorkGiver` 268 起 | 工作标签、禁工、所需能力、ShouldSkip、目标优先/可达性、禁止状态等还会筛掉候选。 | “搬运排第一”并不表示地图上所有东西都能立刻搬。 |
| `RimWorld/WorkGiver_DoBill.cs`，`JobOnThing`、`StartOrResumeBillJob` 188 起。`Bill_Production.ShouldDoNow` | 检查有效订单、允许工人、门槛、原料、工位、预留、半成品绑定。失败取料分支约第 265 行会写回下次检查时间并抽随机间隔。 | 原有工单是权威约束。批量预览不能简单地执行真实接单函数。 |
| `Verse/AI/Toils_Recipe.cs`，`DoRecipeWork` 103–118。`Verse/GenRecipe.cs`，`MakeRecipeProducts` 和 `PostProcessProduct` | 操作进度取配方指定角色属性，若配方指定工位速度属性且适用则再乘工位值。未指定角色速度时该项用 1。经验依配方的工作技能、学习因子及操作 ticks 单算。产量和品质在产品链上另行处理。 | 相同工作列下的配方可能有不同工效、成长和品质。 |
| `RimWorld/JobDriver_Research.cs`，`MakeNewToils` 28–44 | 先去交互格。研究按研究员速度乘研究台因子推进，智识经验单独累计。还有项目、台子与供电等条件。 | 高效率研究员站在没有项目的台子前，不能算成实际科研工时。 |
| `RimWorld/JobDriver_PlantWork.cs`，`WorkDonePerTick`、68–79 | 推进工作量与收获结算是不同步骤，普通与药用作物可使用不同产量 Stat。 | 读取实际作物，分别估计工时和期望收获。 |
| `RimWorld/SkillRecord.cs`，`GetLevel` 334 起、`LearnRateFactor` 371–395 | 有效等级可能包含天赋。普通经验的三档基础倍率为 0.35、1、1.5，后续还有学习属性及饱和等修正。 | 火焰加入学习收益，技能已进入速度或品质公式后避免重复奖励。 |
| `RimWorld/CompFoodPoisonable.cs`，`Notify_RecipeProduced`、`PreAbsorbStack`、`PostIngested` | 做饭时先判断房间，再判断厨师。混堆按数量加权，进食还会读取进食者的因素。 | 分开预测生产污染、入库混合及食用后果。 |
| `RimWorld/QualityUtility.cs`，`GenerateQualityCreatedByPawn(Pawn,…)` 236–255。`SurgeryOutcomeComp_Inspired.Affects/PreApply` | 创作与手术灵感分别在对应结算中判断并可能消耗。品质还可有持久角色修正。 | 预览普通品质和常规成功率，保留持久修正。不要调用真实结算来预测一次性机会。 |
| `RimWorld/Tradeable.cs`、`RimWorld/TradeDeal.cs` 218 附近。本机构建交易灵感 Def | 交易灵感既能改价格属性，也能在成交时被消耗。 | “面板有影响”不足以证明该效果可每天反复使用。普通工作分配排除这种机会。 |

三个直接可查的定义锚点分别对应移动狂热对 `MoveSpeed` 的乘数、厨师个人风险曲线、房间洁净度风险曲线。[灵感定义](D:/Steam/steamapps/common/RimWorld/Data/Core/Defs/InspirationDefs/Inspirations.xml:20) · [厨师属性](D:/Steam/steamapps/common/RimWorld/Data/Core/Defs/Stats/Stats_Pawns_WorkRecipes.xml:138) · [房间属性](D:/Steam/steamapps/common/RimWorld/Data/Core/Defs/Rooms/RoomStats.xml:292)
