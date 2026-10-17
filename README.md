# PawnOwnership

**RimWorld 多人联机物品归属追踪 Mod**

## 功能概述

在 RimWorld 多人联机（Multiplayer Mod）环境下，追踪物品、建筑、蓝图的归属权，防止玩家之间互相干扰工作。

### 核心功能

1. **物品归属追踪**
   - 追踪物品、建筑、蓝图、框架的归属
   - 归属信息随物品销毁/产出自动继承

2. **工作过滤**
   - 小人只处理属于自己的物品
   - 搬运材料时只搬运归属自己的蓝图
   - 存储区归属过滤

3. **连锁挖矿支持**
   - 支持矿脉连锁开采的归属继承
   - 自动将新开采的格子归属设为当前玩家

4. **多人同步**
   - 延迟同步队列，避免 MP 同步问题
   - 支持 Zone、Thing、Cell 的归属同步

---

## 安装

1. 确保已安装 [Multiplayer Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=1753506840)
2. 将本 Mod 放入 RimWorld Mods 文件夹
3. 在 Mod 管理器中启用 PawnOwnership

---

## 使用方法

### 调试模式

- 默认跟随游戏开发者模式（`Prefs.DevMode`）
- 可通过 UI 按钮手动开启/关闭调试日志

### 归属规则

| 类型 | 归属来源 |
|------|----------|
| 物品 | 工作者产出、销毁继承 |
| 建筑 | 蓝图建造完成 |
| 蓝图 | 玩家放置时记录 |
| 存储区 | 玩家创建时记录 |
| 采矿格子 | 玩家标记时记录 |

---

## 技术实现

### 文件结构

```
Source/
├── Mod_PawnOwnership.cs           # Mod 入口
├── HarmonyPatches.cs              # 动态 Patch 注册
├── MapComponent_PawnOwnership.cs  # 核心组件
├── PatchHelpers.cs                # 辅助方法
└── patches/
    ├── Patch_DesignatorManager.cs           # Designation 归属
    ├── Patch_ZoneManager.cs                 # Zone 归属
    ├── Patch_Thing.cs                       # Blueprint/Frame 归属继承
    ├── Patch_WorkGiver_ConstructDeliverResources.cs  # 搬运过滤
    ├── Patch_Designator_Build.cs            # Blueprint 放置
    ├── Patch_Map.cs                         # 归属标志绘制
    ├── Patch_TickManager.cs                 # 延迟同步处理
    ├── Patch_GenLeaving.cs                  # 销毁产出继承
    ├── Patch_Pawn_JobTracker.cs             # 工作者栈
    ├── Patch_Pawn.cs                        # Pawn 进入地图同步
    ├── Patch_StoreUtility.cs                # 存储区过滤
    └── Patch_JobDriver_Mine.cs              # 连锁挖矿
```

### Harmony Patch 列表

| 目标类 | 方法 | 类型 | 功能 |
|--------|------|------|------|
| DesignationManager | AddDesignation | Postfix | 记录标记归属 |
| DesignationManager | RemoveDesignation | Prefix | 清除标记归属 |
| ZoneManager | RegisterZone | Postfix | 记录存储区归属 |
| ZoneManager | DeregisterZone | Prefix | 清除存储区归属 |
| Thing | DeSpawn | Prefix | 保存蓝图归属到暂存 |
| Thing | SpawnSetup | Postfix | 从暂存恢复归属 |
| WorkGiver_ConstructDeliverResources | IsNewValidNearbyNeeder | Postfix | 过滤非归属蓝图 |
| Designator_Build | DesignateSingleCell | Postfix | 记录蓝图归属 |
| Map | MapUpdate | Postfix | 绘制归属标志 |
| TickManager | TickManagerUpdate | Postfix | 处理延迟同步队列 |
| GenLeaving | DoLeavingsFor | Prefix+Postfix | 销毁产出归属继承 |
| Pawn_JobTracker | StartJob | Postfix | 工作者栈入栈 |
| Pawn_JobTracker | EndCurrentJob | Postfix | 工作者栈出栈 |
| Pawn | SpawnSetup | Postfix | 同步携带物品归属 |
| StoreUtility | TryFindBestBetterStoreCellForWorker | Prefix | 存储区归属过滤 |
| JobDriver_Mine | DoDamage | Prefix+Postfix | 连锁挖矿归属传递 |

### 动态 Patch

针对 `WorkGiver_Scanner` 子类的动态 Patch：
- `JobOnThing` - 工作分配前检查归属
- `HasJobOnThing` - 工作可用性检查
- `JobOnCell` - 格子工作分配
- `HasJobOnCell` - 格子工作可用性
- `PotentialWorkThingsGlobal` - 候选物品过滤
- `PotentialWorkCellsGlobal` - 候选格子过滤

---

## 依赖

- RimWorld 1.6
- Multiplayer Mod
- HarmonyLib

---

## 编译

```bash
dotnet build Source/PawnOwnership.csproj
```

输出：`1.6/Assemblies/PawnOwnership.dll`

---

## 许可证

MIT License

---

## 更新日志

### v0.9.1
- 物品归属追踪
- 连锁挖矿支持

### v0.9.0
- 初始版本
- 工作过滤
- 多人同步

---

## 测试与门禁

### 归属颜色块开关

- 入口：Mod 设置页复选框、地图左上按钮，二者共享同一状态。
- 状态存于本地 `ModSettings`，不参与 MP 同步，各客户端独立。
- 仅控制颜色块显示，不影响 InspectString 与 ITab。

### 门禁命令

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/harness/run-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/harness/run-tests.ps1 -Deploy -Launch
powershell -NoProfile -ExecutionPolicy Bypass -File tools/harness/run-tests.ps1 -Deploy -Launch -Scenario loadsave -SaveName '<存档名>'
```

- L0：仅对补丁参数名做静态审计。
- L1：`-Deploy -Launch`，仅作数量阈值冒烟。
- L2：`-Deploy -Launch -Scenario loadsave -SaveName '<存档名>'`，实机载入存档。
- 参数前置限制：`-Scenario` 要求 `-Launch`，`-Launch` 要求 `-Deploy`，`loadsave` 要求 `-SaveName`；与 31 条审计同为固定门槛。
- 部署会写入游戏目录；测试会强制结束自己启动的进程。真实测试建议使用独立 `savedatafolder` 与存档副本，并确保 `Prefs.runInBackground=True`。
- 本轮真实 L2 已通过（2026-10-17）：`1.6.4871` 加载存档副本 `acceptance`，L1 挂载 255 个方法；绘制数开/关/恢复为 `5/0/5`，三张截图为非空 PNG，退出码 0；原 DLL 已恢复，原存档配置哈希未变。证据在 `artifacts/runtime-acceptance/`（已忽略，不提交）。

### 回归命令

```powershell
dotnet run --project tools/harness/tests/driver/DriverRegression.csproj -- artifacts/qa-driver-run
powershell -NoProfile -ExecutionPolicy Bypass -File tools/harness/tests/test-gate.ps1
```

### 验证边界

- stub 测试不证明真实渲染，也不证明双客户端同步。
- L1 只是数量阈值冒烟，不能证明每个补丁有效。
- L0 只做参数名静态审计，不执行补丁逻辑。
- 尚未做双客户端同步与设置持久化人工验收；日志中的动态 patch 继承注册失败和 `InputLegacyModule` 反射依赖错误为既有观察项，本轮未归因。
