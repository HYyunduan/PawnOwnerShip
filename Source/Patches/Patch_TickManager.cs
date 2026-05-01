using Verse;
using HarmonyLib;

namespace PawnOwnership.Patches
{
    // ==========================================
    // Tick 处理延迟同步
    // ==========================================

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    static class Patch_TickManager_TickManagerUpdate
    {
        static void Postfix()
        {
            MapComponent_PawnOwnership.ProcessSyncQueue();
        }
    }
}