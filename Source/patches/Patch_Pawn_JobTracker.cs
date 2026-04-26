using System.Collections.Generic;
using Verse;
using Verse.AI;
using HarmonyLib;
using RimWorld;

namespace PawnOwnership.Patches
{
    // ==========================================
    // 需求一：物品归属变化逻辑
    // 方案 B：工作者栈兜底（工作产出）
    // ==========================================

    public static class WorkerStack
    {
        private static Stack<Pawn> _currentWorkDoer = new Stack<Pawn>();
        
        public static Stack<Pawn> CurrentWorkDoer => _currentWorkDoer;
    }

    // 工作开始
    [HarmonyPatch(typeof(Pawn_JobTracker), "StartJob")]
    static class Patch_StartJob
    {
        static void Postfix(Pawn_JobTracker __instance, Job newJob, JobCondition lastJobEndCondition, ThinkNode jobGiver, bool resumeCurJobAfterwards, bool cancelBusyStances, ThinkTreeDef thinkTree, JobTag? tag, bool fromQueue, bool canReturnCurJobToPool, bool? keepCarryingThingOverride, bool continueSleeping, bool addToJobsThisTick, bool preToilReservationsCanFail)
        {
            var pawn = Traverse.Create(__instance).Field<Pawn>("pawn").Value;
            if (pawn == null || newJob == null) return;
            
            WorkerStack.CurrentWorkDoer.Push(pawn);
            // MapComponent_PawnOwnership.DebugLog($"[PawnOwnership-WorkerStack] StartJob: {pawn.Name} 开始工作 {newJob.def.defName}，栈深度: {WorkerStack.CurrentWorkDoer.Count}");
        }
    }

    // 工作结束
    [HarmonyPatch(typeof(Pawn_JobTracker), "EndCurrentJob")]
    static class Patch_EndJob
    {
        static void Postfix(Pawn_JobTracker __instance, JobCondition condition, bool startNewJob, bool canReturnToPool)
        {
            var pawn = Traverse.Create(__instance).Field<Pawn>("pawn").Value;
            if (pawn == null) return;
            
            if (WorkerStack.CurrentWorkDoer.Count > 0 && WorkerStack.CurrentWorkDoer.Peek() == pawn)
            {
                WorkerStack.CurrentWorkDoer.Pop();
                // MapComponent_PawnOwnership.DebugLog($"[PawnOwnership-WorkerStack] EndJob: {pawn.Name} 结束工作，栈深度: {WorkerStack.CurrentWorkDoer.Count}");
            }
        }
    }

    // SpawnSetup 工作者兜底
    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
    static class Patch_SpawnSetup_WorkerFallback
    {
        static void Postfix(Thing __instance, Map map, bool respawningAfterLoad)
        {
            if (__instance == null || map == null) return;
            if (respawningAfterLoad) return;
            
            // 跳过 Blueprint 和 Frame（已有专门处理）
            if (__instance is Blueprint || __instance is Frame) return;
            
            var comp = map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;
            
            // 已有归属？跳过（DoLeavingsFor 已处理）
            if (!string.IsNullOrEmpty(comp.GetOwner(__instance))) return;
            
            // 工作者兜底
            if (WorkerStack.CurrentWorkDoer.Count > 0)
            {
                Pawn worker = WorkerStack.CurrentWorkDoer.Peek();
                string owner = worker.GetOwner();
                
                if (!string.IsNullOrEmpty(owner))
                {
                    comp.SetOwner(__instance, owner);
                    MapComponent_PawnOwnership.DebugLog($"[PawnOwnership-WorkerFallback] {__instance.ThingID} 继承工作者归属 -> {owner}");
                }
            }
        }
    }
}