using System.Collections.Generic;
using Verse;
using HarmonyLib;
using RimWorld;

namespace PawnOwnership.Patches
{
    // ==========================================
    // 需求一：物品归属变化逻辑
    // 方案 A：DoLeavingsFor 归属继承（销毁产出）
    // ==========================================

    public static class OwnershipInheritanceContext
    {
        private static string _pendingLeavingsOwner = null;
        
        public static string PendingLeavingsOwner
        {
            get => _pendingLeavingsOwner;
            set => _pendingLeavingsOwner = value;
        }
    }

    [HarmonyPatch(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor), 
        new System.Type[] { typeof(Thing), typeof(Map), typeof(DestroyMode), typeof(CellRect), typeof(System.Predicate<IntVec3>), typeof(List<Thing>) })]
    static class Patch_GenLeaving_DoLeavingsFor
    {
        // Prefix: 保存老物品归属
        static void Prefix(Thing diedThing, Map map, DestroyMode mode, CellRect leavingsRect, System.Predicate<IntVec3> nearPlaceValidator, List<Thing> listOfLeavingsOut)
        {
            OwnershipInheritanceContext.PendingLeavingsOwner = null;
            if (diedThing == null || map == null) return;
            
            var comp = map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;
            
            OwnershipInheritanceContext.PendingLeavingsOwner = comp.GetOwner(diedThing);
            
            if (!string.IsNullOrEmpty(OwnershipInheritanceContext.PendingLeavingsOwner))
            {
                MapComponent_PawnOwnership.DebugLog($"[PawnOwnership-DoLeavingsFor] 保存 {diedThing.ThingID} 归属 -> {OwnershipInheritanceContext.PendingLeavingsOwner}");
            }
        }
        
        // Postfix: 将归属应用到产出的物品
        static void Postfix(Thing diedThing, Map map, DestroyMode mode, CellRect leavingsRect, System.Predicate<IntVec3> nearPlaceValidator, List<Thing> listOfLeavingsOut)
        {
            if (string.IsNullOrEmpty(OwnershipInheritanceContext.PendingLeavingsOwner)) return;
            if (listOfLeavingsOut == null || listOfLeavingsOut.Count == 0) return;
            
            var comp = map?.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;
            
            // 直接从 listOfLeavingsOut 获取产出的物品
            foreach (var thing in listOfLeavingsOut)
            {
                if (thing == null) continue;
                comp.SetOwner(thing, OwnershipInheritanceContext.PendingLeavingsOwner);
                MapComponent_PawnOwnership.DebugLog($"[PawnOwnership-DoLeavingsFor] 继承归属: {thing.ThingID} -> {OwnershipInheritanceContext.PendingLeavingsOwner}");
            }
            
            MapComponent_PawnOwnership.DebugLog($"[PawnOwnership-DoLeavingsFor] 共 {listOfLeavingsOut.Count} 个物品继承归属");
            
            OwnershipInheritanceContext.PendingLeavingsOwner = null;
        }
    }
}