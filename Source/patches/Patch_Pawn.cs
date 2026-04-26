using Verse;
using HarmonyLib;

namespace PawnOwnership.Patches
{
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
                MapComponent_PawnOwnership.DebugLog($"[PawnOwnership-PawnEnterMap] {__instance.Name?.ToString() ?? __instance.ThingID} 进入地图，设置 {count} 个携带物品归属 -> {pawnOwner}");
            }
        }
    }
}