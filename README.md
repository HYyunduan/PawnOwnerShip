# PawnOwnership

**RimWorld 1.6 多人联机归属追踪 Mod**

在 RimWorld 多人联机（[Multiplayer](https://steamcommunity.com/sharedfiles/filedetails/?id=2606448745)）环境下追踪殖民者、动物、物品、建筑与区域/格子的归属，让不同玩家的工作互不干扰。单机同样可用（用于区分归属与限制工作）。

## 功能概述

### 1. 归属指派与查看（「所属」页签）

- 选中殖民者 / 动物 / 物品，在检查面板的「所属」页签查看当前归属玩家；
- 点「更改所属」选择玩家或清除归属；
- 页签内同时提供 **显示设置**（归属颜色块开关）与 **调试设置**（模组 Debug，跟随游戏开发者模式或强制开关）。

### 2. 多选批量更改

- 框选多个殖民者 / 动物 / 物品，一次性批量设置或清除归属；
- 混合选择时只处理支持归属的对象；
- 批量逻辑与单选共用同一实现，结果一致。

### 3. 按归属限制工作（工作过滤）

- 工作分配前检查 Thing / Zone / Cell 归属，他人拥有的对象不会被派活；
- 覆盖播种、收割、建造（蓝图 / 框架）、搬运、做饭 / 装料、存储区等路径；
- **强制命令可越过限制**（征召 / 强制指派仍可让小人执行）。

### 4. 归属标记

- 在蓝图、框架、物品上绘制归属颜色块（颜色对应玩家）；
- 可在「所属」页签的显示设置中关闭。

### 5. 归属继承

- 物品销毁产出、连锁挖矿、蓝图建造完成等场景自动继承归属；
- 归属随物品销毁 / 产出链路传递，避免归属丢失。

### 6. 多人同步

- 通过 `[SyncMethod]` 同步 Thing / Zone / Cell 归属变更；
- 延迟同步队列避免联机同步问题。

---

## 安装

1. 安装 RimWorld **1.6**；
2. 订阅并启用 [Multiplayer](https://steamcommunity.com/sharedfiles/filedetails/?id=2606448745)（`rwmt.multiplayer`，其所需前置请一并启用）；
3. 订阅 / 启用 **殖民者所有权**，并确保它在 Mod 列表中排在 Multiplayer **之后**。

---

## 使用方法

### 「所属」页签

| 操作 | 说明 |
|------|------|
| 单选对象 | 查看当前归属 →「更改所属」→ 选择玩家 / 清除 |
| 多选对象 | 「批量更改所属」→ 一次应用到所有支持的对象 |
| 显示设置 | `显示归属颜色块` 开关（关闭后不再绘制色块，检查信息与本页签不受影响） |
| 调试设置 | 模组 Debug：跟随游戏开发者模式，或手动强制开 / 关 |

### 归属规则

| 对象 | 归属来源 |
|------|----------|
| 殖民者 / 动物 | 玩家在「所属」页签手动设置 |
| 物品 | 工作者产出 / 建造、销毁继承、玩家手动更改 |
| 蓝图 / 框架 | 放置玩家 |
| 区域（如种植区） | 创建玩家（区域注册时记录） |
| 采矿格子 | 标记采矿的玩家（连锁挖矿继承） |

> 区域 / 格子的归属目前只在创建 / 标记时记录，暂未提供修改入口；物品与小人的归属可在「所属」页签随时更改。

---

## 技术实现

### 文件结构

```
Source/
├── Mod_PawnOwnership.cs              # Mod 入口 / 设置
├── MapComponent_PawnOwnership.cs     # 核心组件：归属存取、同步、标记绘制
├── PawnOwnershipExtension.cs         # Pawn/Thing 归属扩展方法
├── PawnOwnershipWorldComponent.cs    # 世界级（跨地图）归属数据
├── PawnOwnershipModSettings.cs       # 本地设置（如颜色块开关）
├── UI/
│   ├── ITab_Ownership.cs             # 「所属」页签（单选 / 多选 / 显示 / 调试）
│   ├── Dialog_SetOwner.cs            # 更改归属对话框（单个 / 批量）
│   └── OwnershipBulk.cs              # 批量归属逻辑（UI 与测试共用）
├── Patches/                          # Harmony 静态补丁 + 动态补丁注册
└── Testing/                          # 启动自检与实机测试驱动（仅测试参数下运行）
```

### 静态补丁（PatchAudit 全量审计 31 条）

| 领域 | 目标方法 |
|------|----------|
| 归属记录 / 继承 | `DesignationManager.Add/RemoveDesignation`、`Thing.DeSpawn/SpawnSetup`、`ZoneManager.Register/DeregisterZone`、`Designator_Build.DesignateSingleCell`、`GenSpawn.Spawn`、`GenLeaving.DoLeavingsFor` |
| 工作过滤 | `WorkGiver_ConstructDeliverResources.IsNewValidNearbyNeeder`、`StoreUtility.TryFindBestBetterStoreCellForWorker`、`HaulAIUtility.PawnCanAutomaticallyHaul`、`GenClosest.ClosestThingReachable`、`FoodUtility.SpawnedFoodSearchInnerScan`、`JobDriver.DriverTickInterval` |
| 连锁挖矿 | `JobDriver_Mine.DoDamage` |
| 同步 | `TickManager.TickManagerUpdate` |
| 信息 / UI | `Thing.GetInspectString`、`Thing.GetInspectTabs`、`MainTabWindow_Inspect.CurTabs` |

### 动态补丁（WorkGiver_Scanner 及其子类）

- 目标：`WorkGiver_Scanner` 子类**自己声明**的 6 个方法：
  `JobOnThing` / `HasJobOnThing` / `JobOnCell` / `HasJobOnCell` / `PotentialWorkThingsGlobal` / `PotentialWorkCellsGlobal`；
- 策略：
  - 只给该类自己实现的方法打补丁，继承来的方法直接跳过（由父类处理）；
  - 拦截前缀统一用 Harmony 的 `__args` **按位置取参**，不依赖目标方法的参数名（原版各重写参数名不一致）；
  - 基类 `WorkGiver_Scanner` 不打补丁（其实现只返回空值，无功能收益，且避免 base 调用链重复执行）；
- 失败不再静默：补丁失败会写 Warning 并计入启动自检报告；自检还会逐条断言关键拦截 prefix 是否真的挂载。

---

## 依赖

- RimWorld **1.6**
- **Multiplayer**（Steam Workshop `2606448745`，packageId `rwmt.multiplayer`）
- Harmony / Prepatcher（Multiplayer 所需前置）

---

## 编译

```bash
dotnet build Source/PawnOwnership.csproj
```

输出：`1.6/Assemblies/PawnOwnership.dll`

---

## 测试与门禁

### 门禁命令

```powershell
# L0 静态补丁审计 + 编译
powershell -NoProfile -ExecutionPolicy Bypass -File tools/harness/run-tests.ps1

# L0 + 构建部署 + 启动自检（运行时补丁挂载断言）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/harness/run-tests.ps1 -Deploy -Launch

# 实机场景
powershell -NoProfile -ExecutionPolicy Bypass -File tools/harness/run-tests.ps1 -Deploy -Launch -Scenario ownership -SaveName '<存档名>'
powershell -NoProfile -ExecutionPolicy Bypass -File tools/harness/run-tests.ps1 -Deploy -Launch -Scenario cropprobe -SaveName '<存档名>'
powershell -NoProfile -ExecutionPolicy Bypass -File tools/harness/run-tests.ps1 -Deploy -Launch -Scenario markerprobe -SaveName '<存档名>'

# 双客户端联机门禁（需要 Steam 运行）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/harness/run-mp-tests.ps1 -SaveName '<存档名>'
```

- L0：静态审计全部补丁参数名是否与目标方法一致（31 条）。
- L1：启动自检（`selftest.json`）——运行时真实挂载的方法数、prefix/postfix 数、动态补丁成功/跳过/失败计数，以及关键拦截器是否挂载。
- 实机场景：`ownership`（13 例归属用例）、`cropprobe`（9 例作物用例）、`markerprobe`（标记绘制）等。
- 联机门禁：主机 + 客户端双进程，覆盖搬运过滤、建造归属、种植区归属、作物匹配等断言。
- 报告产物：`artifacts/gate-report.json`、`artifacts/mp-report.json`。

### 当前验证状态（2026-10-23）

| 项目 | 结果 |
|------|------|
| L0 静态审计 | 31/31 通过 |
| L1 启动自检 | `healthy=true`，挂载方法 266 个；动态补丁成功 339 / 跳过继承 620 / 失败 0；拦截器 4/4 挂载 |
| ownership 归属用例 | 13/13 通过 |
| cropprobe 作物用例 | 9/9 通过 |
| markerprobe 标记绘制 | `ownedThings=50`，3 张截图非空 |
| 双客户端联机门禁 | PASS（搬运 / 建造 / 种植区 / 归属过滤 + 作物匹配） |

### 验证边界

- L1 是运行时挂载与拦截器断言，不能替代每个具体业务路径的测试；
- 实机场景覆盖核心链路，但发布前仍建议人工过一遍多人存档；
- 联机测试要求 **Steam 运行**（Prepatcher 依赖），且同机双开需要唯一用户名（测试脚本已自动处理）。

---

## 已知限制

- 区域 / 格子归属只在创建 / 标记时记录，暂无 UI 修改入口；
- 极少数通过 `base.X()` 调用的链路会重复执行一次归属检查（幂等，无行为差异）；
- 归属过滤对**强制命令**放行，这是联机协作的预期行为。

---

## 更新日志

### v0.9.2

- 「所属」页签支持多选：框选多个对象，批量设置或清除归属；
- 新增归属颜色块显示开关（移入「所属」页签，与调试设置同区块）；
- 修复联机播种作物错配：种植区选水稻却种成土豆；
- 补齐归属过滤覆盖：做饭 / 装料、单机播种等路径；
- 动态补丁稳定化：只给声明方法打补丁、拦截补丁不依赖参数名，启动自检可断言拦截器是否挂载。

### v0.9.1

- 物品归属追踪；
- 连锁挖矿支持。

### v0.9.0

- 初始版本；
- 工作过滤；
- 多人同步。

---

## 许可证

MIT License
