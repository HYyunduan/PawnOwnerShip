using Verse;
using HarmonyLib;

namespace PawnOwnership.Patches
{
    // ==========================================
    // GenSpawn.Spawn 归属：通过 DriverTickInterval 上下文确定工作者
    // ==========================================

    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn),
        new System.Type[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) })]
    static class Patch_GenSpawn_Spawn
    {
        static void Postfix(Thing newThing, IntVec3 loc, Map map, bool respawningAfterLoad)
        {
            if (newThing == null || map == null) return;
            if (respawningAfterLoad) return;

            var comp = map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;

            // 已有归属？跳过
            if (!string.IsNullOrEmpty(comp.GetOwner(newThing)))
            {
                MapComponent_PawnOwnership.DebugLog(
                    $"[PawnOwnership-GenSpawn] {newThing.ThingID} 已有归属，跳过");
                return;
            }

            // 白名单过滤
            if (!MapComponent_PawnOwnership.ShouldTrackOwnership(newThing))
            {
                MapComponent_PawnOwnership.DebugLog(
                    $"[PawnOwnership-GenSpawn] {newThing.ThingID} 不在白名单，跳过 (cat={newThing.def.category})");
                return;
            }

            // 检查是否有 pawn 正在工作（DriverTickInterval 上下文）
            Pawn worker = OwnershipContext.CurrentWorker;
            if (worker == null)
            {
                MapComponent_PawnOwnership.DebugLog(
                    $"[PawnOwnership-GenSpawn] {newThing.ThingID} 无 worker 上下文，跳过");
                return;
            }

            string owner = worker.GetOwner();
            if (!string.IsNullOrEmpty(owner))
            {
                comp.SetOwner(newThing, owner);
                Log.Message(
                    $"[PawnOwnership-GenSpawn] {newThing.ThingID} 归属 -> {owner} (worker={worker.Name}, job={worker.CurJob?.def.defName ?? "null"})");
            }
            else
            {
                MapComponent_PawnOwnership.DebugLog(
                    $"[PawnOwnership-GenSpawn] {newThing.ThingID} worker {worker.Name} 无归属，跳过");
            }
        }
    }
}
