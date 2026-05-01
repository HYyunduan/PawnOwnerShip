using Verse;
using Verse.AI;
using HarmonyLib;

namespace PawnOwnership.Patches
{
    // ==========================================
    // DriverTickInterval 上下文：追踪当前正在工作的 Pawn
    // ==========================================

    [HarmonyPatch(typeof(JobDriver), "DriverTickInterval")]
    static class Patch_JobDriver_DriverTickInterval
    {
        static void Prefix(JobDriver __instance)
        {
            OwnershipContext.CurrentWorker = __instance.pawn;
            MapComponent_PawnOwnership.DebugLog(
                $"[PawnOwnership-DriverTick] Set worker: {__instance.pawn.Name} ({__instance.pawn.GetOwner() ?? "no owner"})");
        }

        static void Postfix(JobDriver __instance)
        {
            MapComponent_PawnOwnership.DebugLog(
                $"[PawnOwnership-DriverTick] Clear worker: {__instance.pawn.Name}");
            OwnershipContext.CurrentWorker = null;
        }
    }
}
