using Verse;
using HarmonyLib;
using Multiplayer.API;

namespace PawnOwnership.Patches
{
    // ==========================================
    // Zone 归属记录
    // ==========================================

    [HarmonyPatch(typeof(ZoneManager), nameof(ZoneManager.RegisterZone))]
    static class Patch_ZoneManager_RegisterZone
    {
        static void Postfix(ZoneManager __instance, Zone newZone)
        {
            if (!MapComponent_PawnOwnership.ShouldProcessOwnership()) return;
            if (newZone == null) return;
            
            var comp = __instance.map?.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;
            
            string currentPlayer = MapComponent_PawnOwnership.GetCurrentPlayer();
            int mapId = __instance.map.uniqueID;
            
            // 使用延迟同步队列
            MapComponent_PawnOwnership.QueueSyncMessageZone(mapId, newZone.ID, currentPlayer);
            MapComponent_PawnOwnership.DebugLog($"[PawnOwnership] RegisterZone: Zone_{newZone.ID} -> {currentPlayer}");
        }
    }

    // ==========================================
    // Zone 删除时清除归属
    // ==========================================

    [HarmonyPatch(typeof(ZoneManager), nameof(ZoneManager.DeregisterZone))]
    static class Patch_ZoneManager_DeregisterZone
    {
        static void Prefix(ZoneManager __instance, Zone oldZone)
        {
            if (oldZone == null) return;
            
            var comp = __instance.map?.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;
            
            comp.RemoveOwnerZone(oldZone.ID);
        }
    }
}