# 决策记录（Decision Log）

本文件记录 PawnOwnership 的已确认决策、待议项与已发生的变更，避免口头结论丢失。
**新条目追加在末尾，已确认的决策不删除**；如需推翻，新增一条并标注「取代 D-xxx」。

> 工作纪律见 [`RULES.md`](../RULES.md)。

---

## 一、工作规则

审核边界、设计说明格式、变更纪律统一见 [`RULES.md`](../RULES.md) 第二节。
本文件只记录**决策本身**，不重复规则正文，避免两处内容漂移。

---

## 二、已确认决策

### D-001 归属标记显示开关的入口

- **状态**：已确认（2026-10-17）
- **决策**：`Mod` 设置页勾选框 **+** 地图角落小按钮，两个入口共享同一状态
- **持久化**：`ModSettings`（`Config/ModSettings.xml`），**不进存档**
- **MP 同步**：**不同步**，各玩家本地独立（联机时你关了、队友仍开着，属预期行为）
- **理由**：设置页是正式入口、便于发现；地图按钮满足"1 秒切换"的即时需求

### D-002 关闭显示时的作用范围

- **状态**：已确认（2026-10-17）
- **决策**：只隐藏屏幕上的归属颜色块
- **不受影响**：检查信息（InspectString）里的「所属：xxx」、ITab「所属」页签
- **理由**：语义清晰；排查问题时可以关掉颜色块、同时仍能查看归属

---

## 三、待议

### D-003 归属标记的每帧绘制开销

- **状态**：**已确认（2026-10-17）：本轮不动**（此前给出的选项 C）
- **理由**：绘制开销问题尚未被量化证实，且底层材质缓存已由原版提供（见下方更正）。
  按 [`RULES.md`](../RULES.md) 铁律 1，不在未证实的前提下改动代码。
- **后续**：若将来出现实际卡顿，先加只读计数器量化（`trackedThings` / `visibleThings` /
  `drawCallsLastFrame`），再决定 M2 或 M4。
- **背景更正（重要）**：此前报告中的 P2「每帧为每个物品新建 `Material`、从不释放」**结论不成立**。
  `SolidColorMaterials` 的 API 形态显示它内部已按颜色缓存：
  静态字段 `simpleColorMats`、公开属性 `SimpleColorMatCount`、独立的 `NewSolidColorMaterial` 构造入口。
  即 `SimpleSolidColorMaterial(col, careAboutVertexColors)` 命中缓存，不会每帧新建材质。
  → **材质缓存这一项不需要做。**
- **真正剩下的开销**：`DrawOwnershipMarkers()` 每帧遍历全地图的
  `Blueprints / BuildingFrame / HaulableEver`，对每个有归属的物体各调用一次
  `Graphics.DrawMesh`，且**不区分是否在摄像机视野内**。开销量级为 O(全图归属物数量) 次绘制调用/帧。
- **候选方案**：
  | 编号 | 方案 | 复杂度 | 收益 | 风险 |
  |---|---|---|---|---|
  | M2 | 视口剔除：跳过不在 `Find.CameraDriver.CurrentViewRect` 内的物体 | 低（约 20 行） | 大（只看屏幕内，通常降一个数量级） | 极低，纯显示层 |
  | M3 | 降低刷新频率（如 10Hz 重建，或多帧分摊） | 低 | 中 | 颜色块可能轻微滞后/闪烁 |
  | M4 | `Graphics.DrawMeshInstanced` 按颜色分组批量绘制 | 高 | 在数百以上归属物时才明显 | 需自管矩阵数组，与现有逐物体绘制并存时易错 |
- **待定**：是否纳入 S1；若纳入，优先 M2。

---

## 四、已发生的代码变更（审核规则生效前）

2026-10-17，以下改动已完成并通过编译与静态补丁审计（31/31）：

| 文件 | 内容 |
|---|---|
| `Source/Patches/PatchHelpers.cs` | 修复 `JobOnCell_Prefix` 参数名 `c` → `cell`（Harmony 按名绑定，1.6 中 `JobOnCell` 的参数名是 `cell`、`HasJobOnCell` 的是 `c`） |
| `Source/MapComponent_PawnOwnership.cs` | `SetOwner` 的两处无条件 `Log.Message` 收敛为 `DebugLog`，并修正成功分支误写为 "filtered" 的文案 |
| `Source/Patches/Patch_DesignatorManager.cs` | 两处 `Log.Message` 收敛为 `DebugLog` |
| `Source/Patches/Patch_GenLeaving.cs` | 全部 `Log.Message` 收敛为 `DebugLog`，保留 `Log.Error` |

---

## 五、已知问题清单（均未处理）

| 编号 | 问题 | 位置 | 备注 |
|---|---|---|---|
| P1 | `ProcessSyncQueue` 在每客户端 Tick 中调用 `[SyncMethod]` | `Patch_TickManager.cs` → `MapComponent_PawnOwnership.ProcessSyncQueue` | 维护者已说明同步逻辑验证过，暂不处理 |
| P2 | ~~每帧新建 Material~~ | — | **已撤回，见 D-003** |
| P3 | 暂存归属用 `position.GetHashCode()` 作 key，且不清扫残留 | `MapComponent_PawnOwnership.pendingOwnershipMap` | |
| P4 | `syncQueue` / `pendingOwnershipMap` / `OwnershipInheritanceContext` 为静态且读档不重置 | 多处 | |
| P5 | `ShouldTrackOwnership` 恒返回 true，白名单是死代码 | `MapComponent_PawnOwnership.cs:65` | 所有 Thing 都进字典，存档随物品数增长 |
| P6 | InspectString 给所有 Thing 追加「所属：无」 | `Patch_Thing_InspectString.cs` | 画面噪音 |
| P7 | 玩家颜色反射找不到 `color` 时 `break`，放弃全部玩家颜色 | `MapComponent_PawnOwnership.RefreshPlayerColors` | `0MultiplayerAPI.dll` 只有 `IPlayerInfo` 接口 |
| P8 | 移除任一 designation 即清掉整格归属 | `Patch_DesignatorManager.cs` | 同格多标记时误清 |
| P9 | 过滤条件在单机/多人下不一致 | `JobOnThing` 无 MP 判断，其余有 | 抽 `OwnershipRuntime.Active` 可一并解决 |
