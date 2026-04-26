using Verse;
using RimWorld;
using HarmonyLib;
using Multiplayer.API;

namespace PawnOwnership.Patches
{
    // ==========================================
    // 连锁挖矿归属传递
    // ==========================================

    // Patch JobDriver_Mine.DoDamage
    // 在调用 FloodFillDesignations 前设置归属上下文，退出后清除
    [HarmonyPatch(typeof(JobDriver_Mine), "DoDamage")]
    static class Patch_JobDriver_Mine_DoDamage
    {
        static void Prefix(JobDriver_Mine __instance, Thing target, Verse.AI.Toil mine, Pawn actor, IntVec3 mineablePos)
        {
            if (!MP.enabled || !MP.IsInMultiplayer)
                return;
            
            if (actor?.Map == null)
                return;
            
            // 检查是否是 MineVein 开采（会触发 FloodFillDesignations）
            bool isVeinMining = actor.Map.designationManager?.DesignationAt(mineablePos, DesignationDefOf.MineVein) != null;
            if (!isVeinMining)
                return;
            
            var comp = actor.Map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null)
                return;
            
            // 优先获取 target（矿石）的归属，为空则获取 actor（小人）的归属
            string owner = comp.GetOwnerCell(mineablePos.x, mineablePos.z);
            if (string.IsNullOrEmpty(owner))
            {
                owner = actor.GetOwner();
            }
            
            if (!string.IsNullOrEmpty(owner))
            {
                PatchHelpers.SetMiningOwner(owner);
                MapComponent_PawnOwnership.DebugLog($"[PawnOwnership] DoDamage 设置归属: {owner}");
            }
        }
        
        static void Postfix()
        {
            // 清除归属上下文
            PatchHelpers.ClearMiningOwner();
            MapComponent_PawnOwnership.DebugLog($"[PawnOwnership] DoDamage 清除归属");
        }
    }
}