using Verse;
using RimWorld;
using HarmonyLib;
using Multiplayer.API;

namespace PawnOwnership.Patches
{
    // ==========================================
    // 搬运存储区归属过滤
    // ==========================================

    [HarmonyPatch(typeof(StoreUtility), "TryFindBestBetterStoreCellForWorker")]
    static class Patch_TryFindBestBetterStoreCellForWorker
    {
        static bool Prefix(ISlotGroup slotGroup, Pawn carrier)
        {
            // 单机模式跳过
            if (!MP.enabled || !MP.IsInMultiplayer)
                return true;
            
            if (carrier == null || carrier.Map == null)
                return true;
            
            var comp = carrier.Map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null)
                return true;
            
            string pawnOwner = carrier.GetOwner();
            if (string.IsNullOrEmpty(pawnOwner))
                return true;
            
            // 获取存储区对应的 Zone
            // slotGroup.parent 返回 ISlotGroupParent，Zone 实现了这个接口
            if (slotGroup is SlotGroup sg && sg.parent is Zone zone)
            {
                string zoneOwner = comp.GetOwnerZone(zone.ID);
                if (!string.IsNullOrEmpty(zoneOwner) && zoneOwner != pawnOwner)
                {
                    // 存储区不属于当前玩家，跳过
                    return false;
                }
            }
            
            return true;
        }
    }
}