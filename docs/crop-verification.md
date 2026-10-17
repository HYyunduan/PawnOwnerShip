# 作物选择生效性自动化测试（cropprobe）

> 日期：2026-10-17　分支：`bugfix/crop-selection`
> 背景：玩家反馈"种植区选择水稻，小人却种土豆；重置任务无效"，且偶现。

## 测试做了什么

在真实存档里建一个 `Zone_Growing`，然后按顺序断言整条作物链路：

| 用例 | 断言 |
|---|---|
| C01 | `SetPlantDefToGrow(水稻)` 后 `GetPlantDefToGrow()` 读回水稻 |
| C02 | 区域扩张（新增格子）后作物保持不变 |
| C03 | `WorkGiver_GrowerSow` 派出的任务里 `job.plantDefToSow` == 区域设定作物 |
| C04 | 改成土豆后，区域字段变为土豆 |
| C05 | 改成土豆后，派出的任务随之变为土豆 |
| C06 | 再改回水稻，派活再次生效（双向切换） |
| C07 | 区域归属 = 小人归属时，仍能正常派活（过滤不误伤） |
| C08 | 区域归属为他人时，应被拦截（归属过滤是否生效） |

**关键方法说明**：`WorkGiver_GrowerSow.JobOnCell` 依赖扫描阶段写入的 `wantedPlantDef` 字段，
直接调用会读到上一次扫描残留的作物。测试必须先调用 `PotentialWorkCellsGlobal(pawn)` 走一遍扫描入口，
再调用 `JobOnCell`，否则测到的是测试方法自身的缺陷（第一次运行即因此产生假失败）。

## 结果（存档：新移民1）

```
total=9  passed=8  failed=1
```

**C01–C07 全部通过** → 单机环境下，作物选择链路是正常的：
设定水稻就派水稻、改土豆就派土豆、扩张区域不丢设置。

**C08 失败** → 区域归属为他人时，播种任务没有被拦截。

## C08 的根因（已确认）

运行期补丁报告（`selftest.json` 的 `patchedMethods`）中：

```
在列：RimWorld.WorkGiver_Grower.PotentialWorkCellsGlobal
在列：RimWorld.WorkGiver_GrowerHarvest.HasJobOnCell
缺失：RimWorld.WorkGiver_GrowerSow.JobOnCell
缺失：RimWorld.WorkGiver_GrowerHarvest.JobOnCell
```

即 `WorkGiver_GrowerSow.JobOnCell` **从未被打上补丁**，归属过滤对播种完全不起作用。

原因：Harmony 按参数名绑定，`JobOnCell` 前缀使用参数名 `cell`，而覆盖方法 `WorkGiver_GrowerSow.JobOnCell(Pawn pawn, IntVec3 c, bool forced)` 的参数名是 `c` → 绑定失败且被静默吞掉。

这是已记录缺陷（RULES K-005 / 动态补丁绑定问题），并非本次新增。

## 这个测试能保证什么

- **能**：证明"设定作物 → 派活作物"这条链路正确，并对回归敏感（任何让 `plantDefToSow` 与区域设置不一致的改动都会被 C03/C05/C06 抓到）。
- **不能**：复现"偶现的种错作物"。该症状是概率性的，单机稳定环境不重现；若发生在联机，仍需要双端区域字段比对才能定性。

## 复现命令

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\harness\run-tests.ps1 `
  -Deploy -Launch -Scenario cropprobe -SaveName '新移民1' -LaunchTimeoutSec 420
```

报告：`artifacts/gate-report.json`；逐用例：运行目录下 `crop-report.json`。

## 证据

`artifacts/runs/2026-10-17-crop-probe/`（crop-report.json / scenario-report.json / selftest.json / game.log）
