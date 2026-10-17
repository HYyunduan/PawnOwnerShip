# 双客户端联机自动化测试（L3）验证结论

> 日期：2026-10-17　分支：`feat/mp-test-harness`
> 方案：[plan-mp-harness.md](plan-mp-harness.md)

## 结论

**联机门禁已跑通并通过。** 归属过滤在真实双客户端联机环境下按预期生效：

| 编号 | 断言 | 结果 |
|---|---|---|
| M01 | 同归属 → `CheckThingOwnership` 放行 | ✅ True |
| M02 | 异归属 → `CheckThingOwnership` 拦截 | ✅ True |
| M03 | MP 门控的候选列表过滤（`PotentialWorkThingsGlobal_Postfix`）：输入 2 个物品，过滤后只剩自己的 | ✅ True |

两端均进入同一会话（`session 存在，players=2`）。

## 怎么跑

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\harness\run-mp-tests.ps1 `
    -SaveName '新移民1' -Port 30502 -HostTimeoutSec 240 -ClientTimeoutSec 300
```

脚本会自动：

1. 备份 MP 设置文件
2. 写入主机用户名 `MPHost` → 启动主机 → 等日志 `Server started.`
3. 写入客户端用户名 `MPClient` → 启动客户端 → 等待 `scenario-report.json`
4. 汇总到 `artifacts/mp-report.json`，产物复制到 `artifacts/runs/<时间>-mp-observe/`
5. 强杀两个进程并恢复 MP 设置

## 实现要点（踩过的坑）

| 问题 | 结论 |
|---|---|
| **日志冲突** | 两端各自 `-logFile`，不使用默认 `Player.log` |
| **配置目录无法隔离** | `GenFilePaths.ConfigFolderPath` 与 `SaveDataFolderPath` 是独立路径，`-savedatafolder` 只隔离存档，两端共享 Config |
| **用户名重复** | MP 的活动设置文件是 `Config/Mod_2606448745_Multiplayer.xml`（workshop packageId），**不是** `Mod_Multiplayer_Multiplayer.xml`；且用户名在**游戏启动时**读取并缓存，运行中修改无效 → 必须启动前串行改写文件 |
| **建房入口** | `HostUtil.HostServer` 会因缺少端点解析而空引用；正确路径：设置 `ServerSettings` → `HostWindow.TryParseEndpoints` → `new HostWindow(null)` → 设置 `serverSettings` 字段 → 实例方法 `TryHost()` |
| **连接入口** | `ServerBrowser.DirectConnect("127.0.0.1:30502")` 可用 |
| **Steam** | 两端设置 `steam=false / direct=true`，走 UDP 30502 直连（`Get-NetTCPConnection` 查不到，需用 `Get-NetUDPEndpoint`） |
| **测试断言入口** | `CheckThingOwnership`（反射，私有静态）与 `PotentialWorkThingsGlobal_Postfix`（公开）可直接调用，不依赖原版的可达性/仓储条件 |

## 单机 vs 联机的门控差异（重要）

| 路径 | 单机 | 联机 |
|---|---|---|
| `JobOnThing` / `HasJobOnThing` / `JobOnCell` / `HasJobOnCell` 前缀 | **生效**（内部 `CheckThingOwnership` 无 MP 门控） | 生效 |
| `PotentialWorkThingsGlobal` / `PotentialWorkCellsGlobal` 候选过滤 | **不生效**（`!MP.IsInMultiplayer` 直接放行） | 生效 |
| `HaulAIUtility` / `GenClosest` / `StoreUtility` 等 | 不生效 | 生效 |

因此：验证"候选列表过滤"必须用联机门禁；验证"谓词本身"单机即可。

## 产物

- `artifacts/mp-report.json`
- `artifacts/runs/20261017-210410-mp-observe/`（host.log / client.log / 两端报告）

---

## 真实 bug 验证（2026-10-17）

用联机门禁复现并定位了玩家反馈的两个问题。**全部为归属过滤按设计生效的结果，不是随机故障。**

### Bug A：另一个玩家的小人不会去建造

| 检查 | 结果 |
|---|---|
| 蓝图归属他人 + 非强制工作 | **被拦截**（`JobOnThing_Prefix` 返回 false） |
| 蓝图归属他人 + 强制命令（右键优先） | 放行（`if (forced) return true;`） |
| 蓝图归属自己 + 非强制 | 放行 |

**机制**：蓝图归属由 `Designator_Build.DesignateSingleCell` 的后缀按**发起建造的玩家**写入。
玩家 A 规划的建筑属于 A，玩家 B 的小人在常规工作（非强制）时被 `CheckThingOwnership` 拦截，
表现为"怎么都不去建"。强制命令能绕过，所以右键指派仍可用——这与反馈中"不听命令"的表述吻合
（实际是常规工作被拦，非强制指派不受影响）。

### Bug B：种田"只能种一种"、选了作物不生效

| 检查 | 结果 |
|---|---|
| 种植区归属他人 → 该格被拦截 | **被拦截** |
| 种植区归属自己 → 放行 | 放行 |

**机制**：种植区归属由 `ZoneManager.RegisterZone` 后缀按**创建者**写入。玩家 A 建的种植区属于 A，
玩家 B 的小人无法在其格子上播种/收割，只能去自己能工作的区域 → 表现为"只有一种作物能种"。
玩家看到的作物选择是 A 的区域，但实际干活的 B 小人被挡在外面，于是继续种自己区域里的那种。

### 结论与修复方向（待审核）

两个 bug 同源：**归属按"创建者"自动指派，而联机下全队共享一个殖民地，导致另一方的劳动被拦。**

候选修复方向：

| 方案 | 说明 | 影响面 |
|---|---|---|
| 1. 建造/区域默认归属"全队" | 把蓝图、种植区等**工作型对象**的归属默认为共享，不参与者过滤 | 直接解决两个问题，但削弱"按人分田/分活"的能力 |
| 2. 过滤只对**拥有物品**生效，对"待完成的工作"不生效 | 蓝图/区域视为任务而非财产 | 语义更合理，改动集中在 `CheckThingOwnership`/`CheckCellOwnership` |
| 3. 增加"共享归属"选项 | 给玩家一个可切换的归属模式（个人/全队） | 最灵活，工作量最大 |

建议先做方案 1 或 2（需维护者选择）。

### 复现命令

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\harness\run-mp-tests.ps1 -SaveName '新移民1'
```
输出中查看 M01–M03、B01a–c、B02a–b。
---

## 真实 bug 复现：种植作物错配（2026-10-17）

### 现象

同一片种植区设定作物后，派出的播种任务却带着**另一种作物**，且重置任务无效。

### 复现方法（联机门禁客户端内）

1. 建两个种植区：A 设为水稻、B 设为土豆（都必须建在 `fertility > 0` 的地形上，且要 `RegisterZone` 注册到 `AllZones`，否则扫描返回 0 格）
2. 走一遍 `WorkGiver_GrowerSow.PotentialWorkCellsGlobal(pawn)` 扫描
3. 对扫描到的每个格子调用 `JobOnCell`，比较 `job.plantDefToSow` 与该格所在区域的作物

### 实测结果

```
扫描到 18 格
播种任务 9 格
错配 6 格
作物错配：格 (155,0,167) 区域期望 Plant_Potato，实际派活 Plant_Rice
作物错配：格 (156,0,167) 区域期望 Plant_Potato，实际派活 Plant_Rice
作物错配：格 (157,0,167) 区域期望 Plant_Potato，实际派活 Plant_Rice
```

**土豆区派出了水稻任务** —— 与玩家反馈"选水稻却种土豆"是同一机制（方向相反，取决于区域遍历顺序）。

### 根因（高置信度）

`WorkGiver_GrowerSow` 依赖 `WorkGiver_Grower.wantedPlantDef` 这一**实例字段**保存当前要种的作物，
正常情况下由扫描流程"每产出一格就设置一次"，与 `JobOnCell` 逐格调用**交错**进行。

而我们的后置补丁在联机下会把惰性枚举**提前物化**：

```csharp
// Source/Patches/PatchHelpers.cs
var resultList = __result.ToList();                 // ← 破坏交错
return resultList.Where(...).ToList();
```

物化后，`wantedPlantDef` 停留在最后一次扫描的区域作物上；随后 `ScanJobs` 再逐格调用 `JobOnCell` 时，
所有格子都拿到这个残留作物 → 整片区域种错。

**这解释了全部特征**：
- 只在联机出现（单机时后置直接 `return __result`，不物化）
- 偶现（取决于区域遍历顺序、扫描时机、区域内格子数量）
- "只能种一种"（所有区域被同一种残留作物覆盖）
- 关掉模组即恢复（后置不执行）

### 修复方向（待审核）

把两个候选过滤后置从"提前物化"改为**惰性过滤**，保持原有的交错求值语义：

```csharp
private static IEnumerable<Thing> FilterThingsLazily(IEnumerable<Thing> src, Pawn pawn, ...)
{
    foreach (Thing t in src)
        if (IsThingAccessible(pawn, t, comp, pawnOwner)) yield return t;
}
```

影响文件：`Source/Patches/PatchHelpers.cs`（`PotentialWorkThingsGlobal_Postfix` / `PotentialWorkCellsGlobal_Postfix`）。
改动很小，但属于产品逻辑修改，需维护者批准。