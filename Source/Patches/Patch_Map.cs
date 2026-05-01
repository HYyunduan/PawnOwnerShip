using Verse;
using HarmonyLib;

namespace PawnOwnership.Patches
{
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
}