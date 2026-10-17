# 调查报告：联机下种植作物错配（选水稻却种土豆）

> 日期：2026-10-17　分支：`feat/mp-test-harness`
> 结论状态：**已复现；根因高置信度定位；修复待批准**

## 一、一句话结论

联机模式下，本模组的候选过滤后置补丁把 RimWorld 的**惰性枚举提前求值**，
打乱了"产出一格 → 立刻为该格派活"的交错时序，使 `wantedPlantDef` 停留在上一次的作物上，
于是整片种植区按同一种残留作物派活。

## 二、RimWorld 的播种数据链（涉及的 API）

| 层 | API | 作用 |
|---|---|---|
| 区域数据 | `Zone_Growing.plantDefToGrow`、`SetPlantDefToGrow(ThingDef)`、`GetPlantDefToGrow()` | 玩家在 UI 里选的作物 |
| 工作器缓存 | `WorkGiver_Grower.wantedPlantDef`（实例字段） | **当前这一格**要种的作物 |
| 候选枚举 | `WorkGiver_Grower.PotentialWorkCellsGlobal(Pawn)` → `IEnumerable<IntVec3>`（`yield` 惰性） | 列出可工作格子；迭代过程中设置 `wantedPlantDef` |
| 派活 | `WorkGiver_GrowerSow.JobOnCell(Pawn, IntVec3, bool)` | 用当前 `wantedPlantDef` 构造任务 |
| 任务数据 | `Job.plantDefToSow` | 任务携带的作物 |
| 执行 | `JobDriver_PlantSow.MakeNewToils()` → `GenSpawn.Spawn(plantDefToSow, ...)` | 真正种下 |

原版扫描入口 `WorkGiver_Scanner.ScanJobs` 的时序是**交错**的：

```
foreach (cell in PotentialWorkCellsGlobal(pawn))   // 惰性：每 yield 一格前设置 wantedPlantDef
    JobOnCell(pawn, cell)                          // 立刻使用当格的 wantedPlantDef
```

## 三、我们的模组改动了什么

`Source/Patches/PatchHelpers.cs`：

```csharp
// 第 295 / 332 行：单机直接放行（保持惰性原样返回）
if (!MP.enabled || !MP.IsInMultiplayer) return __result;

// 第 315-316 / 352-353 行：联机下先物化再过滤
var resultList = __result.ToList();                                  // ← 关键问题
return resultList.Where(...).ToList();
```

注册位置：`Source/Patches/HarmonyDynamicPatches.cs`，对 `WorkGiver_Scanner` 的各子类统一挂后置。

`ToList()` 会**立即跑完**整个 `yield` 迭代器：所有区域的格子一次性产出，
`wantedPlantDef` 被反复覆盖，最终停在其中某个区域的作物上。
之后原版循环再逐格调用 `JobOnCell` 时，拿到的都是这个残留值。

## 四、时序对比

**正常（单机 / 无补丁）**

```
区域A(水稻) → wantedPlantDef=水稻 → 产出 A 的格子 → JobOnCell → 水稻任务 ✔
区域B(土豆) → wantedPlantDef=土豆 → 产出 B 的格子 → JobOnCell → 土豆任务 ✔
```

**联机 + 我们的后置**

```
ToList(): 区域A→wantedPlantDef=水稻→产出全部A格；区域B→wantedPlantDef=土豆→产出全部B格
之后逐格 JobOnCell: 全部使用最后的 wantedPlantDef（土豆或水稻，取决于遍历顺序）
→ 某个区域整片种错 ✘
```

## 五、为什么表现正好是反馈里那四条

| 反馈特征 | 解释 |
|---|---|
| **只在联机出现** | 单机时后置直接 `return __result`，不物化 |
| **偶现** | `AllZones` 遍历顺序、区域数量、格子数量、扫描时机都会影响；**只有一个种植区时残留值恰好正确 → 不复现** |
| **只能种一种** | 所有区域共用同一残留作物 |
| **重置小人任务无效** | 每次重新扫描都重复同样过程，残留值不变 |
| **关掉模组就正常** | 后置不执行，惰性时序恢复 |

## 六、证据强度（区分已证明与待证明）

**已证明（联机真实会话内，players=2）**

- 建两个种植区：A=水稻、B=土豆（均在 `fertility>0` 地形且已 `RegisterZone`）
- 走一次扫描 → 逐格 `JobOnCell` 比对 `job.plantDefToSow` 与所在区域作物
- 结果：扫描 18 格、播种任务 9 个、**错配 6 个**，例如
  `格(155,0,167) 区域期望 Plant_Potato，实际派活 Plant_Rice`

**已证明（单机对照）**

- `cropprobe` 的 C01–C06：单机走真实扫描路径，作物链路全部正确 → 支持"惰性正常、物化出错"

**待证明（修复前会补做）**

- 上面的复现实验里，小人当时**没有归属**，我们的后置其实走了早退分支；
  错配是由实验自身调用的 `ToList()` 触发的。
- 需要再做一次 A/B：**给小人设置归属**（后置进入物化分支）与不设归属各跑一遍，
  比较错配数，才能把责任 100% 归到我们的补丁上。

## 七、修复方案

把两个候选过滤后置改为**惰性过滤**，保持交错求值：

```csharp
public static IEnumerable<IntVec3> PotentialWorkCellsGlobal_Postfix(
    IEnumerable<IntVec3> __result, Pawn pawn)
{
    if (!MP.enabled || !MP.IsInMultiplayer) return __result;
    if (pawn == null || pawn.Map == null) return __result;

    var comp = pawn.Map.GetComponent<MapComponent_PawnOwnership>();
    if (comp == null) return __result;

    string pawnOwner = pawn.GetOwner();
    if (string.IsNullOrEmpty(pawnOwner)) return __result;

    return FilterCellsLazily(__result, pawn, comp, pawnOwner);   // ← 返回惰性迭代器
}

private static IEnumerable<IntVec3> FilterCellsLazily(
    IEnumerable<IntVec3> source, Pawn pawn,
    MapComponent_PawnOwnership comp, string pawnOwner)
{
    if (!_pawnCellsFilterSet.Add(pawn.thingIDNumber))
    {
        // 递归调用：原样放行，避免死循环
        foreach (IntVec3 c in source) yield return c;
        yield break;
    }

    try
    {
        foreach (IntVec3 c in source)                            // ← 逐格拉取，保持交错
        {
            bool ok;
            try { ok = IsCellAccessibleForCandidate(pawn, c, comp, pawnOwner); }
            catch { ok = true; }
            if (ok) yield return c;
        }
    }
    finally
    {
        _pawnCellsFilterSet.Remove(pawn.thingIDNumber);
    }
}
```

`PotentialWorkThingsGlobal_Postfix` 同理（`FilterThingsLazily`）。

### 注意点

1. **绝对不能保留任何 `ToList()`**；
2. 递归保护必须移入迭代器的 `try/finally`，否则后置返回时就被移除、失效；
3. 迭代期间 `wantedPlantDef` 由原版设置，我们只决定"要不要这一格"，不改变顺序；
4. 过滤谓词抛异常时按放行处理，保持容错。

## 八、验证计划

| 检查 | 期望 |
|---|---|
| 联机 B03 作物错配 | 6 → **0** |
| 联机 M01/M02/M03（搬运过滤） | 保持通过 |
| 联机 B01a–c（建造拦截） | 保持通过 |
| 联机 B02a–b（区域拦截） | 保持通过 |
| 单机 `cropprobe` C01–C06 | 不回退 |
| 新增：单区域用例 | 修复前后都应正确（防止过度修复） |
| 新增：多区域 3+ 用例 | 覆盖残留值场景 |

## 九、风险

| 风险 | 说明 | 对策 |
|---|---|---|
| 惰性枚举被调用方物化 | 若原版某处自己 `ToList()`，交错仍会断 | 实测验证；已知扫描入口是惰性的 |
| 迭代期间集合变化 | 原版也有同样前提 | 不改顺序，风险与原版一致 |
| 递归保护失效 | 若 guard 放错位置 | 放进迭代器 `try/finally`，并加回归用例 |
| 性能 | 逐格判断比批量判断多一次委托调用 | 影响可忽略（每格一次字典查询） |
