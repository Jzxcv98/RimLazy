# RimWorld 1.7 工作接口核对

2026-11-12 核对了社区作者 Krafs 发布的 [Krafs.Rimworld.Ref 1.7.5000-unstable](https://www.nuget.org/packages/Krafs.Rimworld.Ref/1.7.5000-unstable)。包内路径为 `ref/net10.0/Assembly-CSharp.dll`。以下成员来自该文件的公开 .NET 元数据；此次没有加载游戏存档或执行游戏代码。

## 数据导出和写回

| 用途 | 已核对的公开成员 | 如何用于 RimLazy |
| --- | --- | --- |
| 枚举工作 | `Verse.DefDatabase<T>.AllDefsListForReading : List<T>`；`Verse.WorkTypeDef.workGiversByPriority : List<RimWorld.WorkGiverDef>`、`relevantSkills : List<RimWorld.SkillDef>`、`naturalPriority : int` | 枚举当前加载的工作定义及任务提供者，生成快照；读取列表时避免修改游戏集合。 |
| 连接工作列与任务来源 | `RimWorld.WorkGiverDef.workType : Verse.WorkTypeDef`、`priorityInType : int`、`requiredCapacities : List<Verse.PawnCapacityDef>` | 按实际定义建立关系；工作列内的优先级和玩家给某人设置的优先级要分开记录。 |
| 访问殖民者工作表 | `Verse.Pawn.workSettings : RimWorld.Pawn_WorkSettings` | 原版的读写入口位于每名 Pawn 的工作设置。 |
| 读取和写入单元格 | `RimWorld.Pawn_WorkSettings.GetPriority(Verse.WorkTypeDef) → int`、`SetPriority(Verse.WorkTypeDef, int) → void`、`WorkIsActive(Verse.WorkTypeDef) → bool` | 用工作类型读取原值，保留撤销所需数据；经实机验证后用相同工作类型写回。签名上的第二个参数为整数，不能据此推断任意整数都可写。 |
| 优先级与模式 | `RimWorld.Pawn_WorkSettings.LowestPriority` 和 `DefaultPriority` 为公开整数常量，此包元数据字面值分别是 **4** 和 **3**；`RimWorld.PlaySettings.useWorkPriorities : bool` | 这两个常量只说明该原版构建声明的值。需要验证开关行为、禁用值和玩家使用的工作表 MOD 改动，核心接收适配层提供的档位。 |
| 同人工作顺序 | `RimWorld.Pawn_WorkSettings.WorkGiversInOrderNormal : List<RimWorld.WorkGiver>`，另有 `WorkGiversInOrderEmergency` | 普通工作排序可与实际选择进行对照；首期处理常态劳动。属性签名本身不能证明同档冲突的完整处理过程。 |
| 禁令和技能 | `Verse.Pawn.WorkTypeIsDisabled(Verse.WorkTypeDef) → bool`、`GetReasonsForDisabledWorkType(Verse.WorkTypeDef) → List<string>`；`RimWorld.Pawn_SkillTracker.GetSkill(RimWorld.SkillDef) → RimWorld.SkillRecord`、`RimWorld.SkillRecord.passion : RimWorld.Passion` | 分开记录整列禁令、技能和激情，为不可分配原因及培养偏好提供输入。 |
| 单张订单和配方门槛 | `RimWorld.Bill.PawnRestriction : Verse.Pawn`、`allowedSkillRange : Verse.IntRange`、`PawnAllowedToStartAnew(Verse.Pawn) → bool`、`ShouldDoNow() → bool`；`Verse.RecipeDef.PawnSatisfiesSkillRequirements(Verse.Pawn) → bool` | 为具体订单构造资格过滤条件。还须实测材料、工位、路径与其他运行时限制，才能估算真正可执行的工作量。 |

## 复查

从上述固定版本页面下载包，用 `Get-FileHash -Algorithm SHA256` 核对。此次下载的 nupkg 散列为 `CC4C0F56886625BC4E750B9A5E69EE45A63C9920454C1546DFEC07459C3618E5`，其中 `Assembly-CSharp.dll` 散列为 `B991E9D85CED61CB15C08937938FDD42349A227B0F0789FE54F61CA535918FF8`。

本次使用 .NET SDK 自带的 `System.Reflection.MetadataLoadContext`。将包内 `ref/net10.0/*.dll` 与 .NET 10 的 `shared/Microsoft.NETCore.App/<版本>/*.dll` 的绝对路径交给 `PathAssemblyResolver`，核心程序集名为 `System.Private.CoreLib`。在此元数据上下文中打开 `Assembly-CSharp.dll`，对完整类型名调用 `GetType`，再读取公开成员；常量值由 `FieldInfo.GetRawConstantValue()` 取得。该版本的几个定位标识如下。

| 成员 | MetadataToken |
| --- | --- |
| `Verse.Pawn.workSettings` | `0x040019F7` |
| `RimWorld.Pawn_WorkSettings.GetPriority` / `SetPriority` | `0x06005E78` / `0x06005E77` |
| `Verse.Pawn.WorkTypeIsDisabled` | `0x06002351` |
| `RimWorld.Bill.PawnAllowedToStartAnew` | `0x06004A08` |
| `Verse.RecipeDef.PawnSatisfiesSkillRequirements` | `0x06000BCB` |

## 1.7 实机待验

Steam 切换后先记录实际构建号，再与上述包版本比对。最小验证场景包括同档工作竞争、手动优先级开关、殖民者工作禁令、有限订单和共享工位。写回后需核对工作表、存档重载和撤销效果，并验证实际启用的工作表扩展。上述参考包没有可供这些行为测试使用的游戏实现。
