using Verse;
using RimWorld;
using HarmonyLib;

namespace PawnOwnership.Patches
{
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
                MapComponent_PawnOwnership.DebugLog($"[PawnOwnership] DeSpawn: 保存 {__instance.GetType().Name}_{__instance.ThingID} 归属 {owner} 到暂存");
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
                MapComponent_PawnOwnership.DebugLog($"[PawnOwnership] SpawnSetup: 从暂存恢复 {__instance.GetType().Name}_{__instance.ThingID} 归属 {owner}");
            }
        }
    }
}