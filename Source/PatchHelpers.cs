using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;
using RimWorld;
using Multiplayer.API;

namespace PawnOwnership
{
    /// <summary>
    /// 统一的工作归属检查方法
    /// </summary>
    public static class PatchHelpers
    {
        // 防重复检查（处理子类调用 base 的情况）
        // RimWorld 主循环是单线程，所以用普通静态变量即可
        private static HashSet<(int pawnId, int thingId)> _thingCheckSet = new HashSet<(int, int)>();
        private static HashSet<(int pawnId, int cellX, int cellZ)> _cellCheckSet = new HashSet<(int, int, int)>();
        
        // 候选过滤防重复检查
        private static HashSet<int> _pawnThingsFilterSet = new HashSet<int>();
        private static HashSet<int> _pawnCellsFilterSet = new HashSet<int>();
        
        // ==========================================
        // JobOnThing Prefix
        // ==========================================
        public static bool JobOnThing_Prefix(WorkGiver_Scanner __instance, Pawn pawn, Thing t, 
            bool forced, ref Job __result)
        {
            if (forced) return true;
            if (t == null) return true;
            
            // 防重复检查
            var key = (pawn.thingIDNumber, t.thingIDNumber);
            if (!_thingCheckSet.Add(key))
            {
                // 已经在检查中，跳过（子类调用 base 触发的）
                return true;
            }
            
            try
            {
                if (!CheckThingOwnership(pawn, t))
                {
                    __result = null;
                    return false;
                }
                return true;
            }
            finally
            {
                _thingCheckSet.Remove(key);
            }
        }
        
        // ==========================================
        // HasJobOnThing Prefix
        // ==========================================
        public static bool HasJobOnThing_Prefix(WorkGiver_Scanner __instance, Pawn pawn, Thing t, 
            bool forced, ref bool __result)
        {
            if (forced) return true;
            if (t == null) return true;
            
            // 防重复检查
            var key = (pawn.thingIDNumber, t.thingIDNumber);
            if (!_thingCheckSet.Add(key))
            {
                return true;
            }
            
            try
            {
                if (!CheckThingOwnership(pawn, t))
                {
                    __result = false;
                    return false;
                }
                return true;
            }
            finally
            {
                _thingCheckSet.Remove(key);
            }
        }
        
        // ==========================================
        // JobOnCell Prefix
        // ==========================================
        public static bool JobOnCell_Prefix(WorkGiver_Scanner __instance, Pawn pawn, IntVec3 c, 
            bool forced, ref Job __result)
        {
            if (forced) return true;
            
            // 防重复检查
            var key = (pawn.thingIDNumber, c.x, c.z);
            if (!_cellCheckSet.Add(key))
            {
                return true;
            }
            
            try
            {
                if (!CheckCellOwnership(pawn, c))
                {
                    __result = null;
                    return false;
                }
                return true;
            }
            finally
            {
                _cellCheckSet.Remove(key);
            }
        }
        
        // ==========================================
        // HasJobOnCell Prefix
        // ==========================================
        public static bool HasJobOnCell_Prefix(WorkGiver_Scanner __instance, Pawn pawn, IntVec3 c, 
            bool forced, ref bool __result)
        {
            if (forced) return true;
            
            // 防重复检查
            var key = (pawn.thingIDNumber, c.x, c.z);
            if (!_cellCheckSet.Add(key))
            {
                return true;
            }
            
            try
            {
                if (!CheckCellOwnership(pawn, c))
                {
                    __result = false;
                    return false;
                }
                return true;
            }
            finally
            {
                _cellCheckSet.Remove(key);
            }
        }
        
        // ==========================================
        // 统一归属检查 - Thing
        // ==========================================
        private static bool CheckThingOwnership(Pawn pawn, Thing t)
        {
            var comp = pawn.Map?.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return true;
            
            string pawnOwner = pawn.GetOwner();
            if (string.IsNullOrEmpty(pawnOwner)) return true;
            
            // 1. 检查 Thing 归属
            string thingOwner = comp.GetOwner(t);
            if (!string.IsNullOrEmpty(thingOwner) && thingOwner != pawnOwner)
            {
                MapComponent_PawnOwnership.DebugLog(
                    $"[PawnOwnership] 阻止 {pawnOwner} 的小人对 {t.ThingID} 工作，归属: {thingOwner}");
                return false;
            }
            // 2. 检查格子归属
            string cellOwner = comp.GetOwnerCell(t.Position.x, t.Position.z);
            if (!string.IsNullOrEmpty(cellOwner) && cellOwner != pawnOwner)
            {
                 MapComponent_PawnOwnership.DebugLog(
                    $"[PawnOwnership] 阻止 {pawnOwner} 的小人在 Cell_{t.Position.x}_{t.Position.z} 工作，归属: {cellOwner}");
                return false;
            }
            // 3. 检查区域归属
            Zone zone = pawn.Map.zoneManager.ZoneAt(t.Position);
            if (zone != null)
            {
                string zoneOwner = comp.GetOwnerZone(zone.ID);
                if (!string.IsNullOrEmpty(zoneOwner) && zoneOwner != pawnOwner)
                {
                    MapComponent_PawnOwnership.DebugLog(
                        $"[PawnOwnership] 阻止 {pawnOwner} 的小人在 Zone_{zone.ID} 工作，归属: {zoneOwner}");
                    return false;
                }
            }
            return true;
        }
        
        // ==========================================
        // 统一归属检查 - Cell
        // ==========================================
        private static bool CheckCellOwnership(Pawn pawn, IntVec3 c)
        {
            var comp = pawn.Map?.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return true;
            
            string pawnOwner = pawn.GetOwner();
            if (string.IsNullOrEmpty(pawnOwner)) return true;
            
            // 1. 检查 Cell 归属（采矿等）
            string cellOwner = comp.GetOwnerCell(c.x, c.z);
            if (!string.IsNullOrEmpty(cellOwner) && cellOwner != pawnOwner)
            {
                MapComponent_PawnOwnership.DebugLog(
                    $"[PawnOwnership] 阻止 {pawnOwner} 的小人在 Cell_{c.x}_{c.z} 工作，归属: {cellOwner}");
                return false;
            }
            
            // 2. 检查区域归属（种植区等）
            Zone zone = pawn.Map.zoneManager.ZoneAt(c);
            if (zone != null)
            {
                string zoneOwner = comp.GetOwnerZone(zone.ID);
                if (!string.IsNullOrEmpty(zoneOwner) && zoneOwner != pawnOwner)
                {
                    MapComponent_PawnOwnership.DebugLog(
                        $"[PawnOwnership] 阻止 {pawnOwner} 的小人在 Zone_{zone.ID} 工作，归属: {zoneOwner}");
                    return false;
                }
            }
            
            return true;
        }
        
        // ==========================================
        // JobOnThing Postfix - 检查工作目标区域归属
        // ==========================================
        public static void JobOnThing_Postfix(Pawn pawn, ref Job __result)
        {
            if (__result == null) return;
            if (pawn == null || pawn.Map == null) return;
            
            // 检查 targetB 是否有效
            if (__result.targetB == null) return;
            
            IntVec3 destCell;
            if (__result.targetB.HasThing)
            {
                destCell = __result.targetB.Thing.Position;
            }
            else
            {
                destCell = __result.targetB.Cell;
            }
            
            var comp = pawn.Map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;
            
            string pawnOwner = pawn.GetOwner();
            if (string.IsNullOrEmpty(pawnOwner)) return;
            
            // 检查目标区域归属
            Zone destZone = pawn.Map.zoneManager.ZoneAt(destCell);
            if (destZone != null)
            {
                string zoneOwner = comp.GetOwnerZone(destZone.ID);
                if (!string.IsNullOrEmpty(zoneOwner) && zoneOwner != pawnOwner)
                {
                    MapComponent_PawnOwnership.DebugLog(
                        $"[PawnOwnership] 阻止工作: {pawnOwner} 的小人不能在 Zone_{destZone.ID} 工作，归属: {zoneOwner}");
                    __result = null;
                }
            }
        }
        
        // ==========================================
        // 候选物品过滤 - PotentialWorkThingsGlobal Postfix
        // ==========================================
        public static IEnumerable<Thing> PotentialWorkThingsGlobal_Postfix(
            IEnumerable<Thing> __result, 
            Pawn pawn)
        {
            // 单机模式跳过
            if (!MP.enabled || !MP.IsInMultiplayer)
                return __result;
            
            if (pawn == null || pawn.Map == null)
                return __result;
            
            // 防重复检查
            if (!_pawnThingsFilterSet.Add(pawn.thingIDNumber))
                return __result;
            
            try
            {
                var comp = pawn.Map.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null)
                    return __result;
                
                string pawnOwner = pawn.GetOwner();
                if (string.IsNullOrEmpty(pawnOwner))
                    return __result;
                
                var resultList = __result.ToList();
                var filtered = resultList.Where(thing => IsThingAccessible(pawn, thing, comp, pawnOwner)).ToList();
                
                // 调试日志（始终打印）
                MapComponent_PawnOwnership.DebugLog(
                    $"[PawnOwnership] PotentialWorkThingsGlobal: {pawn.Name} 原始 {resultList.Count} 个物品，过滤后 {filtered.Count} 个");
                
                return filtered;
            }
            finally
            {
                _pawnThingsFilterSet.Remove(pawn.thingIDNumber);
            }
        }
        
        // ==========================================
        // 候选格子过滤 - PotentialWorkCellsGlobal Postfix
        // ==========================================
        public static IEnumerable<IntVec3> PotentialWorkCellsGlobal_Postfix(
            IEnumerable<IntVec3> __result, 
            Pawn pawn)
        {
            // 单机模式跳过
            if (!MP.enabled || !MP.IsInMultiplayer)
                return __result;
            
            if (pawn == null || pawn.Map == null)
                return __result;
            
            // 防重复检查
            if (!_pawnCellsFilterSet.Add(pawn.thingIDNumber))
                return __result;
            
            try
            {
                var comp = pawn.Map.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null)
                    return __result;
                
                string pawnOwner = pawn.GetOwner();
                if (string.IsNullOrEmpty(pawnOwner))
                    return __result;
                
                var resultList = __result.ToList();
                var filtered = resultList.Where(cell => IsCellAccessibleForCandidate(pawn, cell, comp, pawnOwner)).ToList();
                
                // 调试日志（始终打印）
                MapComponent_PawnOwnership.DebugLog(
                    $"[PawnOwnership] PotentialWorkCellsGlobal: {pawn.Name} 原始 {resultList.Count} 个格子，过滤后 {filtered.Count} 个");
                
                return filtered;
            }
            finally
            {
                _pawnCellsFilterSet.Remove(pawn.thingIDNumber);
            }
        }
        
        // ==========================================
        // 候选物品可访问性检查
        // ==========================================
        private static bool IsThingAccessible(Pawn pawn, Thing thing, MapComponent_PawnOwnership comp, string pawnOwner)
        {
            if (thing == null) return false;
            
            // 1. 检查物品归属
            string thingOwner = comp.GetOwner(thing);
            if (!string.IsNullOrEmpty(thingOwner) && thingOwner != pawnOwner)
                return false;
            
            // 2. 检查物品所在格子归属
            string cellOwner = comp.GetOwnerCell(thing.Position.x, thing.Position.z);
            if (!string.IsNullOrEmpty(cellOwner) && cellOwner != pawnOwner)
                return false;
            
            // 3. 检查物品所在区域归属
            Zone zone = pawn.Map.zoneManager.ZoneAt(thing.Position);
            if (zone != null)
            {
                string zoneOwner = comp.GetOwnerZone(zone.ID);
                if (!string.IsNullOrEmpty(zoneOwner) && zoneOwner != pawnOwner)
                    return false;
            }
            
            return true;
        }
        
        // ==========================================
        // 候选格子可访问性检查
        // ==========================================
        private static bool IsCellAccessibleForCandidate(Pawn pawn, IntVec3 cell, MapComponent_PawnOwnership comp, string pawnOwner)
        {
            // 1. 检查格子归属
            string cellOwner = comp.GetOwnerCell(cell.x, cell.z);
            if (!string.IsNullOrEmpty(cellOwner) && cellOwner != pawnOwner)
                return false;
            
            // 2. 检查区域归属
            Zone zone = pawn.Map.zoneManager.ZoneAt(cell);
            if (zone != null)
            {
                string zoneOwner = comp.GetOwnerZone(zone.ID);
                if (!string.IsNullOrEmpty(zoneOwner) && zoneOwner != pawnOwner)
                    return false;
            }
            
            return true;
        }
    }
}
