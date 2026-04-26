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
