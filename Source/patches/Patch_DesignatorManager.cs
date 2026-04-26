using Verse;
using Verse.AI;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Multiplayer.API;

namespace PawnOwnership.Patches
{
    // ==========================================
    // Designation 归属记录
    // ==========================================
    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.AddDesignation))]
    static class Patch_DesignationManager_AddDesignation
    {
        static void Postfix(DesignationManager __instance, Designation newDes)
        {
            if (newDes == null) return;
            
            var comp = __instance.map?.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;
            
            int mapId = __instance.map.uniqueID;
            
            // 先读取临时归属（连锁挖矿时需要）
            string miningOwner = PatchHelpers.GetCurrentMiningOwner();
            
            // 如果有临时归属，直接使用（绕过 ShouldProcessOwnership 检查）
            if (!string.IsNullOrEmpty(miningOwner))
            {
                // 检查是否是 Cell target（如采矿）
                if (!newDes.target.HasThing)
                {
                    IntVec3 cell = newDes.target.Cell;
                    MapComponent_PawnOwnership.QueueSyncMessageCell(mapId, cell.x, cell.z, miningOwner);
                    Log.Message($"[PawnOwnership] AddDesignation (Cell, mining): {newDes.def.defName} at {cell} -> {miningOwner}");
                }
                else
                {
                    Thing targetThing = newDes.target.Thing;
                    if (targetThing != null)
                    {
                        MapComponent_PawnOwnership.QueueSyncMessageThing(mapId, targetThing, miningOwner);
                        Log.Message($"[PawnOwnership] AddDesignation (Thing, mining): {newDes.def.defName} {targetThing.ThingID} -> {miningOwner}");
                    }
                }
                return; // 处理完毕，直接返回
            }
            
            // 没有临时归属，走正常流程
            bool shouldProcess = MapComponent_PawnOwnership.ShouldProcessOwnership();
            
            if (!shouldProcess) return;
            
            string owner = MapComponent_PawnOwnership.GetCurrentPlayer();
            
            // 检查是否是 Cell target（如采矿）
            if (!newDes.target.HasThing)
            {
                IntVec3 cell = newDes.target.Cell;
                // 使用延迟同步队列
                MapComponent_PawnOwnership.QueueSyncMessageCell(mapId, cell.x, cell.z, owner);
                MapComponent_PawnOwnership.DebugLog($"[PawnOwnership] AddDesignation (Cell): {newDes.def.defName} at {cell} -> {owner}");
            }
            else
            {
                Thing targetThing = newDes.target.Thing;
                if (targetThing != null)
                {
                    // 使用延迟同步队列
                    MapComponent_PawnOwnership.QueueSyncMessageThing(mapId, targetThing, owner);
                    MapComponent_PawnOwnership.DebugLog($"[PawnOwnership] AddDesignation (Thing): {newDes.def.defName} {targetThing.ThingID} -> {owner}");
                }
            }
        }
    }

    // ==========================================
    // Designation 移除时清除归属
    // ==========================================

    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.RemoveDesignation))]
    static class Patch_DesignationManager_RemoveDesignation
    {
        static void Prefix(DesignationManager __instance, Designation des)
        {
            if (des == null) return;
            
            var comp = __instance.map?.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;
            
            if (!des.target.HasThing)
            {
                IntVec3 cell = des.target.Cell;
                comp.RemoveOwnerCell(cell.x, cell.z);
            }
            else
            {
                Thing targetThing = des.target.Thing;
                if (targetThing != null)
                {
                    comp.RemoveOwner(targetThing.ThingID);
                }
            }
        }
    }
}