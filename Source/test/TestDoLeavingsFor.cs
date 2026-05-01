using Verse;
using Verse.AI;
using HarmonyLib;
using RimWorld;

namespace PawnOwnership.Test
{
    /// <summary>
    /// 临时测试：追踪哪些操作走了 DoLeavingsFor
    /// 测试完删除此文件
    /// </summary>
    [HarmonyPatch(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor),
        new System.Type[] { typeof(Thing), typeof(Map), typeof(DestroyMode), typeof(CellRect), typeof(System.Predicate<IntVec3>), typeof(System.Collections.Generic.List<Thing>) })]
    static class Test_DoLeavingsFor_Logger
    {
        static void Prefix(Thing diedThing, Map map, DestroyMode mode)
        {
            if (diedThing == null) return;
            
            string thingInfo = $"{diedThing.ThingID} (def={diedThing.def.defName}, cat={diedThing.def.category})";
            string stackTrace = new System.Diagnostics.StackTrace(2, true).ToString(); // 跳过 Prefix 和 Harmony 调用
            
            // 只取前 5 帧，避免日志太长
            string[] frames = stackTrace.Split('\n');
            string shortTrace = string.Join("\n", System.Linq.Enumerable.Take(frames, 5));
            
            Log.Message($"[TEST-DoLeavingsFor] 被调用！\n  diedThing: {thingInfo}\n  DestroyMode: {mode}\n  调用栈(前5帧):\n{shortTrace}");
        }
    }
}
