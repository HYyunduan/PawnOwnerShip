using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;
using Verse.AI;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Multiplayer.API;

namespace PawnOwnership
{
    public static class HarmonyPatches
    {
        private static Harmony _harmony;
        
        // ==========================================
        // 动态 Patch 注册
        // ==========================================
        
        /// <summary>
        /// 注册动态 Patch
        /// </summary>
        public static void RegisterDynamicPatches(Harmony harmony)
        {
            _harmony = harmony;
            
            var scannerType = typeof(WorkGiver_Scanner);
            int patchCount = 0;
            
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        // 只处理具体的子类（非抽象类）
                        if (!type.IsSubclassOf(scannerType) || type.IsAbstract)
                            continue;
                        
                        patchCount += PatchMethodIfExists(type, "JobOnThing", 
                            new[] { typeof(Pawn), typeof(Thing), typeof(bool) },
                            typeof(PatchHelpers), "JobOnThing_Prefix");
                        
                        // JobOnThing Postfix - 检查搬运目标区域归属
                        patchCount += PatchMethodIfExists(type, "JobOnThing", 
                            new[] { typeof(Pawn), typeof(Thing), typeof(bool) },
                            typeof(PatchHelpers), "JobOnThing_Postfix", postfix: true);
                        
                        patchCount += PatchMethodIfExists(type, "HasJobOnThing", 
                            new[] { typeof(Pawn), typeof(Thing), typeof(bool) },
                            typeof(PatchHelpers), "HasJobOnThing_Prefix");
                        
                        patchCount += PatchMethodIfExists(type, "JobOnCell", 
                            new[] { typeof(Pawn), typeof(IntVec3), typeof(bool) },
                            typeof(PatchHelpers), "JobOnCell_Prefix");
                        
                        patchCount += PatchMethodIfExists(type, "HasJobOnCell", 
                            new[] { typeof(Pawn), typeof(IntVec3), typeof(bool) },
                            typeof(PatchHelpers), "HasJobOnCell_Prefix");
                        
                        // 候选物品过滤 - PotentialWorkThingsGlobal（仅多人模式）
                        if (MP.enabled)
                        {
                            patchCount += PatchMethodIfExists(type, "PotentialWorkThingsGlobal",
                                new[] { typeof(Pawn) },
                                typeof(PatchHelpers), "PotentialWorkThingsGlobal_Postfix", postfix: true);
                        }
                        
                        // 候选格子过滤 - PotentialWorkCellsGlobal（仅多人模式）
                        if (MP.enabled)
                        {
                            patchCount += PatchMethodIfExists(type, "PotentialWorkCellsGlobal",
                                new[] { typeof(Pawn) },
                                typeof(PatchHelpers), "PotentialWorkCellsGlobal_Postfix", postfix: true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[PawnOwnership] 扫描程序集 {assembly.FullName} 时出错: {ex.Message}");
                }
            }
            
            Log.Message($"[PawnOwnership] 动态 Patch 完成，共注册 {patchCount} 个补丁");
        }
        
        /// <summary>
        /// 如果方法存在，则打 Patch
        /// 返回：1 = 成功 patch，0 = 未 patch
        /// </summary>
        private static int PatchMethodIfExists(Type targetType, string methodName, 
            Type[] paramTypes, Type patchClass, string patchMethodName, bool postfix = false)
        {
            try
            {
                var method = targetType.GetMethod(methodName, paramTypes);
                if (method == null) return 0;
                
                var patchMethod = patchClass.GetMethod(patchMethodName, 
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                
                if (patchMethod == null)
                {
                    Log.Warning($"[PawnOwnership] 找不到 {(postfix ? "Postfix" : "Prefix")} 方法: {patchMethodName}");
                    return 0;
                }
                
                var harmonyMethod = new HarmonyMethod(patchMethod);
                
                if (postfix)
                {
                    _harmony.Patch(method, postfix: harmonyMethod);
                }
                else
                {
                    _harmony.Patch(method, prefix: harmonyMethod);
                }
                
                DebugLog($"[PawnOwnership] Dynamic patch: {targetType.Name}.{methodName} ({(postfix ? "Postfix" : "Prefix")})");
                return 1;
            }
            catch (Exception ex)
            {
                DebugLog($"[PawnOwnership] Patch {targetType.Name}.{methodName} 失败: {ex.Message}");
                return 0;
            }
        }
        
        private static void DebugLog(string message)
        {
            MapComponent_PawnOwnership.DebugLog(message);
        }
        
        // ==========================================
        // Designation 归属记录
        // ==========================================
        
        [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.AddDesignation))]
        static class Patch_DesignationManager_AddDesignation
        {
            static void Postfix(DesignationManager __instance, Designation newDes)
            {
                if (!MapComponent_PawnOwnership.ShouldProcessOwnership()) return;
                if (newDes == null) return;
                
                var comp = __instance.map?.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null) return;
                
                string currentPlayer = MapComponent_PawnOwnership.GetCurrentPlayer();
                int mapId = __instance.map.uniqueID;
                
                // 检查是否是 Cell target（如采矿）
                if (!newDes.target.HasThing)
                {
                    IntVec3 cell = newDes.target.Cell;
                    // 使用延迟同步队列
                    MapComponent_PawnOwnership.QueueSyncMessageCell(mapId, cell.x, cell.z, currentPlayer);
                    DebugLog($"[PawnOwnership] AddDesignation (Cell): {newDes.def.defName} at {cell} -> {currentPlayer}");
                }
                else
                {
                    Thing targetThing = newDes.target.Thing;
                    if (targetThing != null)
                    {
                        // 使用延迟同步队列
                        MapComponent_PawnOwnership.QueueSyncMessageThing(mapId, targetThing, currentPlayer);
                        DebugLog($"[PawnOwnership] AddDesignation (Thing): {newDes.def.defName} {targetThing.ThingID} -> {currentPlayer}");
                    }
                }
            }
        }
        
        // ==========================================
        // Designation 移除时清除归属
        // ==========================================
        
        [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.RemoveDesignation))]
        static class Patch_DesignationManager_RemoveDesignation
        {
            static void Prefix(DesignationManager __instance, Designation des)
            {
                if (des == null) return;
                
                var comp = __instance.map?.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null) return;
                
                if (!des.target.HasThing)
                {
                    IntVec3 cell = des.target.Cell;
                    comp.RemoveOwnerCell(cell.x, cell.z);
                }
                else
                {
                    Thing targetThing = des.target.Thing;
                    if (targetThing != null)
                    {
                        comp.RemoveOwner(targetThing.ThingID);
                    }
                }
            }
        }
        
        // ==========================================
        // Zone 归属记录
        // ==========================================
        
        [HarmonyPatch(typeof(ZoneManager), nameof(ZoneManager.RegisterZone))]
        static class Patch_ZoneManager_RegisterZone
        {
            static void Postfix(ZoneManager __instance, Zone newZone)
            {
                if (!MapComponent_PawnOwnership.ShouldProcessOwnership()) return;
                if (newZone == null) return;
                
                var comp = __instance.map?.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null) return;
                
                string currentPlayer = MapComponent_PawnOwnership.GetCurrentPlayer();
                int mapId = __instance.map.uniqueID;
                
                // 使用延迟同步队列
                MapComponent_PawnOwnership.QueueSyncMessageZone(mapId, newZone.ID, currentPlayer);
                DebugLog($"[PawnOwnership] RegisterZone: Zone_{newZone.ID} -> {currentPlayer}");
            }
        }
        
        // ==========================================
        // Zone 删除时清除归属
        // ==========================================
        
        [HarmonyPatch(typeof(ZoneManager), nameof(ZoneManager.DeregisterZone))]
        static class Patch_ZoneManager_DeregisterZone
        {
            static void Prefix(ZoneManager __instance, Zone oldZone)
            {
                if (oldZone == null) return;
                
                var comp = __instance.map?.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null) return;
                
                comp.RemoveOwnerZone(oldZone.ID);
            }
        }
        
        // ==========================================
        // Blueprint/Frame 归属继承
        // ==========================================
        
        [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
        static class Patch_Thing_DeSpawn
        {
            static void Prefix(Thing __instance, DestroyMode mode)
            {
                if (__instance == null) return;
                
                // 只处理 Blueprint 和 Frame
                if (!(__instance is Blueprint) && !(__instance is Frame)) return;
                
                var comp = __instance.Map?.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null) return;
                
                string owner = comp.GetOwner(__instance);
                if (!string.IsNullOrEmpty(owner))
                {
                    MapComponent_PawnOwnership.SavePendingOwnership(__instance.Position, owner);
                    DebugLog($"[PawnOwnership] DeSpawn: 保存 {__instance.GetType().Name}_{__instance.ThingID} 归属 {owner} 到暂存");
                }
            }
        }
        
        [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
        static class Patch_Thing_SpawnSetup
        {
            static void Postfix(Thing __instance, Map map, bool respawningAfterLoad)
            {
                if (__instance == null || map == null) return;
                if (respawningAfterLoad) return;
                
                // 只处理 Blueprint 和 Frame
                if (!(__instance is Blueprint) && !(__instance is Frame)) return;
                
                var comp = map.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null) return;
                
                string owner = MapComponent_PawnOwnership.GetAndClearPendingOwnership(__instance.Position);
                if (!string.IsNullOrEmpty(owner))
                {
                    comp.SetOwner(__instance, owner);
                    DebugLog($"[PawnOwnership] SpawnSetup: 从暂存恢复 {__instance.GetType().Name}_{__instance.ThingID} 归属 {owner}");
                }
            }
        }
        
        // ==========================================
        // 关键 patch：搬运材料时的所有权检查
        // 小人决定"哪个 Blueprint/Frame 要我搬运材料"的核心函数
        // ==========================================
        [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "IsNewValidNearbyNeeder")]
        static class Patch_IsNewValidNearbyNeeder
        {
            static void Postfix(Thing t, IConstructible constructible, Pawn pawn, ref bool __result)
            {
                // 如果已经标记为不需要，跳过
                if (!__result) return;
                if (t == null) return;
                
                // 只检查 Blueprint 和 Frame
                if (!(t is Blueprint) && !(t is Frame)) return;
                
                var comp = pawn.Map?.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null) return;
                
                string blueprintOwner = comp.GetOwner(t);
                string pawnOwner = pawn.GetOwner();
                
                // 如果 Blueprint/Frame 有所有权且不属于当前小人，阻止
                if (!string.IsNullOrEmpty(blueprintOwner) && blueprintOwner != pawnOwner)
                {
                    __result = false;
                    DebugLog($"[PawnOwnership-IsNewValidNearbyNeeder] 阻止 {pawnOwner} 的小人搬运 {t.GetType().Name}_{t.ThingID}，所有权属于 {blueprintOwner}");
                }
            }
        }
        
        // ==========================================
        // Blueprint 放置时记录归属（玩家点击确认）
        // ==========================================
        [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.DesignateSingleCell))]
        static class Patch_Designator_Build_DesignateSingleCell
        {
            static void Postfix(Designator_Build __instance, IntVec3 c)
            {
                if (!MapComponent_PawnOwnership.ShouldProcessOwnership())
                {
                    DebugLog("[PawnOwnership] Designator_Build.DesignateSingleCell: 跳过（不是自己发起的命令）");
                    return;
                }
                
                // 只处理 Blueprint（非 godMode 且有工作量）
                if (DebugSettings.godMode) return;
                if (__instance.PlacingDef.GetStatValueAbstract(StatDefOf.WorkToBuild, __instance.StuffDef) == 0f) return;
                
                // 查找刚放置的 Blueprint
                Blueprint blueprint = c.GetThingList(__instance.Map).FirstOrDefault(t => t is Blueprint_Build) as Blueprint;
                if (blueprint == null)
                {
                    DebugLog("[PawnOwnership] Designator_Build.DesignateSingleCell: 未找到 Blueprint");
                    return;
                }
                
                string currentPlayer = MapComponent_PawnOwnership.GetCurrentPlayer();
                MapComponent_PawnOwnership.QueueSyncMessageThing(__instance.Map.uniqueID, blueprint, currentPlayer);
                DebugLog($"[PawnOwnership] Designator_Build: {blueprint.ThingID} 加入队列 -> {currentPlayer}");
            }
        }
        
        // ==========================================
        // 归属标志绘制
        // ==========================================
        
        [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
        static class Patch_Map_MapUpdate
        {
            static void Postfix(Map __instance)
            {
                var comp = __instance.GetComponent<MapComponent_PawnOwnership>();
                if (comp != null)
                {
                    comp.DrawOwnershipMarkers();
                }
            }
        }
        
        // ==========================================
        // Tick 处理延迟同步
        // ==========================================
        
        [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
        static class Patch_TickManager_TickManagerUpdate
        {
            static void Postfix()
            {
                MapComponent_PawnOwnership.ProcessSyncQueue();
            }
        }
        
        // ==========================================
        // 需求一：物品归属变化逻辑
        // ==========================================
        
        // 方案 A：DoLeavingsFor 归属继承（销毁产出）
        private static string pendingLeavingsOwner = null;
        
        [HarmonyPatch(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor), 
            new Type[] { typeof(Thing), typeof(Map), typeof(DestroyMode), typeof(CellRect), typeof(Predicate<IntVec3>), typeof(List<Thing>) })]
        static class Patch_GenLeaving_DoLeavingsFor
        {
            // Prefix: 保存老物品归属
            static void Prefix(Thing diedThing, Map map, DestroyMode mode, CellRect leavingsRect, Predicate<IntVec3> nearPlaceValidator, List<Thing> listOfLeavingsOut)
            {
                pendingLeavingsOwner = null;
                if (diedThing == null || map == null) return;
                
                var comp = map.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null) return;
                
                pendingLeavingsOwner = comp.GetOwner(diedThing);
                
                if (!string.IsNullOrEmpty(pendingLeavingsOwner))
                {
                    DebugLog($"[PawnOwnership-DoLeavingsFor] 保存 {diedThing.ThingID} 归属 -> {pendingLeavingsOwner}");
                }
            }
            
            // Postfix: 将归属应用到产出的物品
            static void Postfix(Thing diedThing, Map map, DestroyMode mode, CellRect leavingsRect, Predicate<IntVec3> nearPlaceValidator, List<Thing> listOfLeavingsOut)
            {
                if (string.IsNullOrEmpty(pendingLeavingsOwner)) return;
                if (listOfLeavingsOut == null || listOfLeavingsOut.Count == 0) return;
                
                var comp = map?.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null) return;
                
                // 直接从 listOfLeavingsOut 获取产出的物品
                foreach (var thing in listOfLeavingsOut)
                {
                    if (thing == null) continue;
                    comp.SetOwner(thing, pendingLeavingsOwner);
                    DebugLog($"[PawnOwnership-DoLeavingsFor] 继承归属: {thing.ThingID} -> {pendingLeavingsOwner}");
                }
                
                DebugLog($"[PawnOwnership-DoLeavingsFor] 共 {listOfLeavingsOut.Count} 个物品继承归属");
                
                pendingLeavingsOwner = null;
            }
        }
        
        // 方案 B：工作者栈兜底（工作产出）
        private static Stack<Pawn> currentWorkDoer = new Stack<Pawn>();
        
        // 工作开始
        [HarmonyPatch(typeof(Pawn_JobTracker), "StartJob")]
        static class Patch_StartJob
        {
            static void Postfix(Pawn_JobTracker __instance, Job newJob, JobCondition lastJobEndCondition, ThinkNode jobGiver, bool resumeCurJobAfterwards, bool cancelBusyStances, ThinkTreeDef thinkTree, JobTag? tag, bool fromQueue, bool canReturnCurJobToPool, bool? keepCarryingThingOverride, bool continueSleeping, bool addToJobsThisTick, bool preToilReservationsCanFail)
            {
                var pawn = Traverse.Create(__instance).Field<Pawn>("pawn").Value;
                if (pawn == null || newJob == null) return;
                
                currentWorkDoer.Push(pawn);
                // DebugLog($"[PawnOwnership-WorkerStack] StartJob: {pawn.Name} 开始工作 {newJob.def.defName}，栈深度: {currentWorkDoer.Count}");
            }
        }
        
        // 工作结束
        [HarmonyPatch(typeof(Pawn_JobTracker), "EndCurrentJob")]
        static class Patch_EndJob
        {
            static void Postfix(Pawn_JobTracker __instance, JobCondition condition, bool startNewJob, bool canReturnToPool)
            {
                var pawn = Traverse.Create(__instance).Field<Pawn>("pawn").Value;
                if (pawn == null) return;
                
                if (currentWorkDoer.Count > 0 && currentWorkDoer.Peek() == pawn)
                {
                    currentWorkDoer.Pop();
                    // DebugLog($"[PawnOwnership-WorkerStack] EndJob: {pawn.Name} 结束工作，栈深度: {currentWorkDoer.Count}");
                }
            }
        }
        
        // SpawnSetup 工作者兜底
        [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
        static class Patch_SpawnSetup_WorkerFallback
        {
            static void Postfix(Thing __instance, Map map, bool respawningAfterLoad)
            {
                if (__instance == null || map == null) return;
                if (respawningAfterLoad) return;
                
                // 跳过 Blueprint 和 Frame（已有专门处理）
                if (__instance is Blueprint || __instance is Frame) return;
                
                var comp = map.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null) return;
                
                // 已有归属？跳过（DoLeavingsFor 已处理）
                if (!string.IsNullOrEmpty(comp.GetOwner(__instance))) return;
                
                // 工作者兜底
                if (currentWorkDoer.Count > 0)
                {
                    Pawn worker = currentWorkDoer.Peek();
                    string owner = worker.GetOwner();
                    
                    if (!string.IsNullOrEmpty(owner))
                    {
                        comp.SetOwner(__instance, owner);
                        DebugLog($"[PawnOwnership-WorkerFallback] {__instance.ThingID} 继承工作者归属 -> {owner}");
                    }
                }
            }
        }
        
        // ==========================================
        // 需求三：Pawn 进入地图时同步携带物品归属
        // ==========================================
        
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
        static class Patch_Pawn_SpawnSetup_OwnershipSync
        {
            static void Postfix(Pawn __instance, Map map, bool respawningAfterLoad)
            {
                if (__instance == null || map == null) return;
                if (respawningAfterLoad) return;
                
                string pawnOwner = __instance.GetOwner();
                if (string.IsNullOrEmpty(pawnOwner)) return;
                
                var mapComp = map.GetComponent<MapComponent_PawnOwnership>();
                if (mapComp == null) return;
                
                int count = 0;
                
                // 背包物品
                if (__instance.inventory != null)
                {
                    foreach (var thing in __instance.inventory.innerContainer)
                    {
                        mapComp.SetOwner(thing, pawnOwner);
                        count++;
                    }
                }
                
                // 装备
                if (__instance.equipment != null)
                {
                    foreach (var eq in __instance.equipment.AllEquipmentListForReading)
                    {
                        mapComp.SetOwner(eq, pawnOwner);
                        count++;
                    }
                }
                
                // 搬运中的物品
                if (__instance.carryTracker != null && __instance.carryTracker.CarriedThing != null)
                {
                    mapComp.SetOwner(__instance.carryTracker.CarriedThing, pawnOwner);
                    count++;
                }
                
                if (count > 0)
                {
                    DebugLog($"[PawnOwnership-PawnEnterMap] {__instance.Name?.ToString() ?? __instance.ThingID} 进入地图，设置 {count} 个携带物品归属 -> {pawnOwner}");
                }
            }
        }
        
        // ==========================================
        // 搬运存储区归属过滤
        // ==========================================
        
        [HarmonyPatch(typeof(StoreUtility), "TryFindBestBetterStoreCellForWorker")]
        static class Patch_TryFindBestBetterStoreCellForWorker
        {
            static bool Prefix(ISlotGroup slotGroup, Pawn carrier)
            {
                // 单机模式跳过
                if (!MP.enabled || !MP.IsInMultiplayer)
                    return true;
                
                if (carrier == null || carrier.Map == null)
                    return true;
                
                var comp = carrier.Map.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null)
                    return true;
                
                string pawnOwner = carrier.GetOwner();
                if (string.IsNullOrEmpty(pawnOwner))
                    return true;
                
                // 获取存储区对应的 Zone
                // slotGroup.parent 返回 ISlotGroupParent，Zone 实现了这个接口
                if (slotGroup is SlotGroup sg && sg.parent is Zone zone)
                {
                    string zoneOwner = comp.GetOwnerZone(zone.ID);
                    if (!string.IsNullOrEmpty(zoneOwner) && zoneOwner != pawnOwner)
                    {
                        // 存储区不属于当前玩家，跳过
                        DebugLog($"[PawnOwnership] 跳过存储区 Zone_{zone.ID}，归属: {zoneOwner}，小人: {pawnOwner}");
                        return false;
                    }
                }
                
                return true;
            }
        }
    }
}
