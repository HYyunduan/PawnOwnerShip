using Verse;
using Verse.AI;
using RimWorld;
using HarmonyLib;
using Multiplayer.API;

namespace PawnOwnership.Patches
{
    /// <summary>
    /// HaulAIUtility 补丁 - 搬运归属拦截
    /// 作为搬运系统的第二道防线（第一道是 PotentialWorkThingsGlobal 候选过滤）
    /// </summary>
    [HarmonyPatch(typeof(HaulAIUtility))]
    [HarmonyPatch("PawnCanAutomaticallyHaul")]
    [HarmonyPatch(new[] { typeof(Pawn), typeof(Thing), typeof(bool) })]
    public static class Patch_HaulAIUtility
    {
        /// <summary>
        /// Prefix: 在原函数执行前检查 Pawn 和 Thing 的归属
        /// 两方都有归属且不同 → 拦截（return false）
        /// 其余情况 → 放行（return true，原函数继续）
        /// </summary>
        public static bool Prefix(Pawn p, Thing t, bool forced, ref bool __result)
        {
            // 强制模式放行
            if (forced) return true;
            if (t == null || p == null || p.Map == null) return true;

            // 单机模式跳过
            if (!MP.enabled || !MP.IsInMultiplayer)
                return true;

            var comp = p.Map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return true;

            string pawnOwner = p.GetOwner();
            string thingOwner = comp.GetOwner(t);

            // 两方都有归属且不同 → 拦截
            if (!string.IsNullOrEmpty(pawnOwner)
                && !string.IsNullOrEmpty(thingOwner)
                && pawnOwner != thingOwner)
            {
                MapComponent_PawnOwnership.DebugLog(
                    $"[PawnOwnership] HaulAIUtility 拦截: {pawnOwner} 的小人不能搬运 {t.ThingID}，归属: {thingOwner}");
                __result = false;
                return false;
            }

            // 其余情况放行（原函数继续执行）
            return true;
        }
    }
}
