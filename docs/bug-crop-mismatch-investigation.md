# 调查报告：联机下种植作物错配（选水稻却种土豆）

> 日期：2026-10-17　分支：`feat/mp-test-harness`
> 状态：**根因已由反编译源码确认；修复已实施；联机门禁与单机回归均通过**

## 一、结论

联机模式下，`PotentialWorkThingsGlobal` / `PotentialWorkCellsGlobal` 的候选过滤后置补丁
**把 RimWorld 的惰性迭代器提前物化（`ToList()`）**，导致原版迭代器内部的
"设置作物 → 产出格子 → 清空作物" 三步与"逐格派活"的**交错关系被破坏**，
`WorkGiver_Grower.wantedPlantDef` 被第一次派活的结果锁死，后续格子全部复用该作物，
表现为整片区域种错作物。

产品代码中没有一处直接修改作物 —— 被改变的是**求值时机**。

## 二、游戏源码（反编译自 `Assembly-CSharp.dll`，1.6.4871）

### 2.1 共享缓存字段

```csharp
public abstract class WorkGiver_Grower : WorkGiver_Scanner
{
    protected static ThingDef wantedPlantDef;   // ★ 静态，全局共享
```

### 2.2 候选枚举：yield 迭代器，设值/清空都在迭代器内部

```csharp
public override IEnumerable<IntVec3> PotentialWorkCellsGlobal(Pawn pawn)
{
    ...
    List<Zone> zonesList = pawn.Map.zoneManager.AllZones;
    for (int j = 0; j < zonesList.Count; j++)
    {
        if (!(zonesList[j] is Zone_Growing growZone)) continue;
        else if (ExtraRequirements(growZone, pawn) && ...)   // ① 在此设置 wantedPlantDef
        {
            for (int k = 0; k < growZone.cells.Count; k++)
                yield return growZone.cells[k];               // ② 逐格产出（然后暂停）
            wantedPlantDef = null;                            // ③ 该区产完才清空
        }
    }
    wantedPlantDef = null;
}
```

### 2.3 设置缓存的真正位置：`ExtraRequirements`

```csharp
protected override bool ExtraRequirements(IPlantToGrowSettable settable, Pawn pawn)
{
    ...
    // 用"该区域第一格"的作物写入静态缓存
    WorkGiver_Grower.wantedPlantDef = WorkGiver_Grower.CalculateWantedPlantDef(c, pawn.Map);
    if (WorkGiver_Grower.wantedPlantDef == null) return false;
    return true;
}
```

### 2.4 派活：只在缓存为空时重算，且自己从不重置

```csharp
public override Job JobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
{
    ...
    if (WorkGiver_Grower.wantedPlantDef == null)          // ★ 只有为空才重算
    {
        WorkGiver_Grower.wantedPlantDef = WorkGiver_Grower.CalculateWantedPlantDef(c, map);
        if (WorkGiver_Grower.wantedPlantDef == null) return null;
    }
    ...
    Job job = JobMaker.MakeJob(JobDefOf.Sow, c);
    job.plantDefToSow = WorkGiver_Grower.wantedPlantDef;  // ★ 任务取缓存值
    return job;
}
```

### 2.5 消费方：逐格处理，且区分 `IList` / 迭代器两条分支

```csharp
// RimWorld.JobGiver_Work
IEnumerable<IntVec3> enumerable2 = scanner.PotentialWorkCellsGlobal(pawn);

if (enumerable2 is IList<IntVec3> list2)
{
    for (int k = 0; k < list2.Count; k++) ProcessCell(list2[k]);
}
else
{
    foreach (IntVec3 item in enumerable2) ProcessCell(item);   // 原版走这里
}
```

`ProcessCell` 内部调用 `scanner.HasJobOnCell(pawn, c)` → `JobOnCell(pawn, c)`。

## 三、逐步骤对比（区域 A=水稻、区域 B=土豆，各 2 格）

### 3.1 原版（惰性，正确）

| # | 谁在动 | 动作 | `wantedPlantDef` | 任务作物 |
|---|---|---|---|---|
| 1 | 迭代器 | 进入 A，`ExtraRequirements` | 水稻 | — |
| 2 | 迭代器 | `yield A1`（暂停） | 水稻 | — |
| 3 | 消费方 | `JobOnCell(A1)` | 水稻（非空，不重算） | 水稻 ✔ |
| 4 | 迭代器 | `yield A2` | 水稻 | — |
| 5 | 消费方 | `JobOnCell(A2)` | 水稻 | 水稻 ✔ |
| 6 | 迭代器 | A 产完 → **置 null** | null | — |
| 7 | 迭代器 | 进入 B，`ExtraRequirements` | 土豆 | — |
| 8 | 迭代器 | `yield B1` | 土豆 | — |
| 9 | 消费方 | `JobOnCell(B1)` | 土豆 | 土豆 ✔ |
| 10 | 迭代器 | `yield B2` / 消费方 `JobOnCell(B2)` | 土豆 | 土豆 ✔ |
| 11 | 迭代器 | 结束 → 置 null | null | — |
| 12 | 消费方 | 循环后为选中格创建正式任务 | null → **现算** | 正确 ✔ |

### 3.2 我方补丁（物化，错误）

```csharp
var resultList = __result.ToList();     // ← 根因：在派活之前把迭代器跑完
```

| # | 谁在动 | 动作 | `wantedPlantDef` |
|---|---|---|---|
| 1-7 | 迭代器 | A/B 全部产出完毕（设值→产出→清空各发生两次） | **null** |
| 8 | — | 返回 `List{...}`（实现 `IList`） | null |
| 9 | 消费方 | `JobOnCell(A1)`：缓存为空 → 按 A1 算 | **水稻（被写入）** |
| 10 | 消费方 | `JobOnCell(A2)`：缓存非空 → 直接用 | 水稻 ✔ |
| 11 | 消费方 | `JobOnCell(B1)`：缓存非空 → 直接用 | **水稻 ✘** |
| 12 | 消费方 | `JobOnCell(B2)`：缓存非空 → 直接用 | **水稻 ✘** |

## 四、为什么表现是反馈里的四条

| 反馈特征 | 解释 |
|---|---|
| 只在联机出现 | 后置有门控 `if (!MP.enabled || !MP.IsInMultiplayer) return __result;`，单机返回原样惰性序列 |
| 偶现 | 取决于区域遍历顺序、区域数量；**只有一个种植区时首次计算恰好正确 → 不复现** |
| "只能种一种" | 静态缓存被第一次派活锁死，后续全部复用 |
| 重置小人任务无效 | 每次重新扫描仍先物化，重复同样的锁死过程 |
| 关掉模组恢复 | 后置不执行，惰性时序恢复 |

## 五、修复

把两个后置从"先物化再过滤"改为**惰性过滤**（`yield return` 包装），保持"取一格→判一格→交一格"的节奏：

```csharp
public static IEnumerable<IntVec3> PotentialWorkCellsGlobal_Postfix(
    IEnumerable<IntVec3> __result, Pawn pawn)
{
    if (!MP.enabled || !MP.IsInMultiplayer) return __result;
    if (__result == null) return null;
    if (pawn == null || pawn.Map == null) return __result;

    var comp = pawn.Map.GetComponent<MapComponent_PawnOwnership>();
    if (comp == null) return __result;

    string pawnOwner = pawn.GetOwner();
    if (string.IsNullOrEmpty(pawnOwner)) return __result;

    return FilterCellsLazily(__result, pawn, comp, pawnOwner);
}

private static IEnumerable<IntVec3> FilterCellsLazily(
    IEnumerable<IntVec3> source, Pawn pawn,
    MapComponent_PawnOwnership comp, string pawnOwner)
{
    if (!_pawnCellsFilterSet.Add(pawn.thingIDNumber))
    {
        foreach (IntVec3 c in source) yield return c;   // 递归时原样放行
        yield break;
    }

    try
    {
        foreach (IntVec3 c in source)                   // ★ 逐格拉取，交错保持
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

`PotentialWorkThingsGlobal_Postfix` 同样处理。

### 修复要点

1. **删掉全部 `ToList()`** —— 这是实质修复；
2. 返回惰性序列后不再是 `IList`，`JobGiver_Work` 自动回到原版的 `foreach` 分支；
3. 递归保护移入迭代器 `try/finally`，覆盖整个惰性求值周期；
4. 过滤逻辑（`IsThingAccessible` / `IsCellAccessibleForCandidate`）一行未改。

## 六、测试结果

### 6.1 联机门禁（`run-mp-tests.ps1`，双客户端 players=2）

B03 采用 A/B 同场对比：同一个小人（设归属以激活后置）、同样的两个种植区。

```
惰性消费（修复后实现）：检查 9 格，错配 0
返回类型是 IList = False
对照（先物化，等价于修复前）：检查 9 格，错配 6
```

- 修复后**错配 0**；
- 同一路径下"先物化"仍产生 6 处错配 → 证明根因就是求值时机；
- `IList=False` 证明游戏走回 `foreach` 分支。

其余联机断言全部保持通过：M01/M02/M03（搬运过滤）、B01a–c（建造拦截）、B02a–b（区域拦截）。

结果：`== 结果: PASS ==`，报告 `artifacts/mp-report.json`。

### 6.2 单机回归

| 场景 | 结果 |
|---|---|
| `cropprobe`（作物链路） | 8/9（与修复前一致；唯一失败是已知的 C08：`WorkGiver_GrowerSow.JobOnCell` 未绑定补丁） |
| `ownership`（归属用例 O01–O11） | **11/11 PASS** |
| L0 门禁（编译 + 补丁审计 31/31） | PASS |

## 七、遗留问题（不在本次范围）

1. `WorkGiver_GrowerSow.JobOnCell` 等 13 个方法因参数名不一致未绑定补丁（C08）；
2. 蓝图/种植区归属按创建者指派，联机共享殖民地时另一方劳动被拦截（建造/种田"不能做"），
   与本次"种错作物"是两个不同问题，修复方向待定。
