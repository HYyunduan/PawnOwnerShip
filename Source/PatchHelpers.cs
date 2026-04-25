using System.Collections.Generic;
using Verse;
using Verse.AI;
using RimWorld;

namespace PawnOwnership
{
    /// <summary>
    /// 统一的工作归属检查 Prefix 方法
    /// </summary>
    public static class PatchHelpers
    {
        // 防重复检查（处理子类调用 base 的情况）
        // RimWorld 主循环是单线程，所以用普通静态变量即可
        private static HashSet<(int pawnId, int thingId)> _thingCheckSet = new HashSet<(int, int)>();
        private static HashSet<(int pawnId, int cellX, int cellZ)> _cellCheckSet = new HashSet<(int, int, int)>();
        
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
            string thingOwner = comp.GetOwner(t.thingIDNumber);
            if (!string.IsNullOrEmpty(thingOwner) && thingOwner != pawnOwner)
            {
                MapComponent_PawnOwnership.DebugLog(
                    $"[PawnOwnership] 阻止 {pawnOwner} 的小人对 Thing_{t.thingIDNumber} 工作，归属: {thingOwner}");
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
    }
}
