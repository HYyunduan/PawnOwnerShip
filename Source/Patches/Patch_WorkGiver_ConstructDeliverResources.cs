using Verse;
using Verse.AI;
using HarmonyLib;
using RimWorld;

namespace PawnOwnership.Patches
{
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
                MapComponent_PawnOwnership.DebugLog($"[PawnOwnership-IsNewValidNearbyNeeder] 阻止 {pawnOwner} 的小人搬运 {t.GetType().Name}_{t.ThingID}，所有权属于 {blueprintOwner}");
            }
        }
    }
}