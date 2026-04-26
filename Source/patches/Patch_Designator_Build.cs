using Verse;
using RimWorld;
using HarmonyLib;

namespace PawnOwnership.Patches
{
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
                MapComponent_PawnOwnership.DebugLog("[PawnOwnership] Designator_Build.DesignateSingleCell: 跳过（不是自己发起的命令）");
                return;
            }
            
            // 只处理 Blueprint（非 godMode 且有工作量）
            if (DebugSettings.godMode) return;
            if (__instance.PlacingDef.GetStatValueAbstract(StatDefOf.WorkToBuild, __instance.StuffDef) == 0f) return;
            
            // 查找刚放置的 Blueprint
            Blueprint blueprint = c.GetThingList(__instance.Map).FirstOrDefault(t => t is Blueprint_Build) as Blueprint;
            if (blueprint == null)
            {
                MapComponent_PawnOwnership.DebugLog("[PawnOwnership] Designator_Build.DesignateSingleCell: 未找到 Blueprint");
                return;
            }
            
            string currentPlayer = MapComponent_PawnOwnership.GetCurrentPlayer();
            MapComponent_PawnOwnership.QueueSyncMessageThing(__instance.Map.uniqueID, blueprint, currentPlayer);
            MapComponent_PawnOwnership.DebugLog($"[PawnOwnership] Designator_Build: {blueprint.ThingID} 加入队列 -> {currentPlayer}");
        }
    }
}