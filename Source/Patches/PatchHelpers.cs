using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;
using RimWorld;
using Multiplayer.API;

namespace PawnOwnership.Patches
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
        
        // 连锁挖矿归属传递
        private static string _currentMiningOwner = null;
        
        public static void SetMiningOwner(string owner)
        {
            _currentMiningOwner = owner;
        }
        
        public static void ClearMiningOwner()
        {
            _currentMiningOwner = null;
        }
        
        public static string GetCurrentMiningOwner()
        {
            return _currentMiningOwner;
        }
        
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
        // 注意：Harmony 按“参数名”绑定原方法参数，必须与 1.6 中的参数名逐字一致。
        // WorkGiver_Scanner.JobOnCell(Pawn pawn, IntVec3 cell, bool forced) 的参数是 cell，
        // 而 HasJobOnCell(Pawn pawn, IntVec3 c, bool forced) 的参数是 c，两者不同，不要统一。
        public static bool JobOnCell_Prefix(WorkGiver_Scanner __instance, Pawn pawn, IntVec3 cell, 
            bool forced, ref Job __result)
        {
            if (forced) return true;
            
            // 防重复检查
            var key = (pawn.thingIDNumber, cell.x, cell.z);
            if (!_cellCheckSet.Add(key))
            {
                return true;
            }
            
            try
            {
                if (!CheckCellOwnership(pawn, cell))
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
        // ⚠ 必须保持**惰性**：原版 `PotentialWorkCellsGlobal` 是 yield 迭代器，
        // 它在"产出每一格/每个物品"的同时设置/清空共享状态（如 WorkGiver_Grower.wantedPlantDef）。
        // 消费方（JobGiver_Work）是"取一个 → 立刻派活 → 再取下一個"的交错模式。
        // 一旦在这里 ToList() 提前求值，状态设置与清空会挤在派活之前完成，
        // 导致后续所有候选共用第一次计算出的值（联机下表现为"选水稻却种土豆"）。
        // 详见 docs/bug-crop-mismatch-investigation.md。
        public static IEnumerable<Thing> PotentialWorkThingsGlobal_Postfix(
            IEnumerable<Thing> __result, 
            Pawn pawn)
        {
            // 单机模式跳过
            if (!MP.enabled || !MP.IsInMultiplayer)
                return __result;
            
            if (__result == null)
                return null;
            
            if (pawn == null || pawn.Map == null)
                return __result;
            
            var comp = pawn.Map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null)
                return __result;
            
            string pawnOwner = pawn.GetOwner();
            if (string.IsNullOrEmpty(pawnOwner))
                return __result;
            
            return FilterThingsLazily(__result, pawn, comp, pawnOwner);
        }
        
        /// <summary>
        /// 惰性过滤物品候选列表：逐个拉取、逐个判断，绝不提前物化。
        /// 递归保护放在迭代器的 finally 里，覆盖整个惰性求值过程。
        /// </summary>
        private static IEnumerable<Thing> FilterThingsLazily(
            IEnumerable<Thing> source,
            Pawn pawn,
            MapComponent_PawnOwnership comp,
            string pawnOwner)
        {
            if (!_pawnThingsFilterSet.Add(pawn.thingIDNumber))
            {
                // 已在过滤中（子类调用 base 造成的重入）：原样放行
                foreach (Thing t in source) yield return t;
                yield break;
            }
            
            try
            {
                foreach (Thing t in source)
                {
                    bool accessible;
                    try { accessible = IsThingAccessible(pawn, t, comp, pawnOwner); }
                    catch { accessible = true; }
                    if (accessible) yield return t;
                }
            }
            finally
            {
                _pawnThingsFilterSet.Remove(pawn.thingIDNumber);
            }
        }
        
        // ==========================================
        // 候选格子过滤 - PotentialWorkCellsGlobal Postfix
        // ==========================================
        // ⚠ 同上的惰性要求：这里的候选正是 WorkGiver_GrowerSow 的播种格子，
        // 提前物化会破坏 wantedPlantDef 的设值/清空时序，导致作物错配。
        public static IEnumerable<IntVec3> PotentialWorkCellsGlobal_Postfix(
            IEnumerable<IntVec3> __result, 
            Pawn pawn)
        {
            // 单机模式跳过
            if (!MP.enabled || !MP.IsInMultiplayer)
                return __result;
            
            if (__result == null)
                return null;
            
            if (pawn == null || pawn.Map == null)
                return __result;
            
            var comp = pawn.Map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null)
                return __result;
            
            string pawnOwner = pawn.GetOwner();
            if (string.IsNullOrEmpty(pawnOwner))
                return __result;
            
            return FilterCellsLazily(__result, pawn, comp, pawnOwner);
        }
        
        /// <summary>惰性过滤格子候选列表，理由同 <see cref="FilterThingsLazily"/>。</summary>
        private static IEnumerable<IntVec3> FilterCellsLazily(
            IEnumerable<IntVec3> source,
            Pawn pawn,
            MapComponent_PawnOwnership comp,
            string pawnOwner)
        {
            if (!_pawnCellsFilterSet.Add(pawn.thingIDNumber))
            {
                foreach (IntVec3 c in source) yield return c;
                yield break;
            }
            
            try
            {
                foreach (IntVec3 c in source)
                {
                    bool accessible;
                    try { accessible = IsCellAccessibleForCandidate(pawn, c, comp, pawnOwner); }
                    catch { accessible = true; }
                    if (accessible) yield return c;
                }
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
