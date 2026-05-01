using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Verse;
using HarmonyLib;
using RimWorld;

namespace PawnOwnership.Patches
{
    // ==========================================
    // 需求一：物品归属变化逻辑
    // DoLeavingsFor Transpiler 方案
    // ==========================================

    public static class OwnershipInheritanceContext
    {
        public static string PendingLeavingsOwner;
        public static List<Thing> CapturedLeavings = new List<Thing>();
    }

    [HarmonyPatch(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor),
        new System.Type[] { typeof(Thing), typeof(Map), typeof(DestroyMode), typeof(CellRect), typeof(System.Predicate<IntVec3>), typeof(List<Thing>) })]
    static class Patch_GenLeaving_DoLeavingsFor
    {
        // Prefix: 保存 diedThing 的归属
        static void Prefix(Thing diedThing, Map map)
        {
            OwnershipInheritanceContext.PendingLeavingsOwner = null;
            OwnershipInheritanceContext.CapturedLeavings.Clear();

            if (diedThing == null || map == null)
            {
                Log.Message("[PawnOwnership-DoLeavingsFor] Prefix: diedThing 或 map 为 null，跳过");
                return;
            }

            var comp = map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null)
            {
                Log.Message("[PawnOwnership-DoLeavingsFor] Prefix: MapComponent 为 null，跳过");
                return;
            }

            string owner = comp.GetOwner(diedThing);
            OwnershipInheritanceContext.PendingLeavingsOwner = owner;

            Log.Message($"[PawnOwnership-DoLeavingsFor] Prefix: diedThing={diedThing.ThingID} (def={diedThing.def.defName}), owner={owner ?? "null"}, map={map.uniqueID}");
        }

        // Transpiler: 在 listOfLeavingsOut?.AddRange(tmpKilledLeavings) 后插入
        // OwnershipInheritanceContext.CapturedLeavings.AddRange(tmpKilledLeavings)
        //
        // tmpKilledLeavings 是 GenLeaving 的静态字段，用 ldsfld 访问
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var codes = new List<CodeInstruction>(instructions);

            var addRangeMethod = typeof(List<Thing>).GetMethod("AddRange");
            var capturedField = typeof(OwnershipInheritanceContext)
                .GetField(nameof(OwnershipInheritanceContext.CapturedLeavings));
            var tmpLeavingsField = typeof(GenLeaving)
                .GetField("tmpKilledLeavings", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            if (tmpLeavingsField == null)
            {
                Log.Error("[PawnOwnership] Transpiler: 找不到 GenLeaving.tmpKilledLeavings 字段");
                return codes;
            }

            Log.Message("[PawnOwnership] Transpiler: 找到 tmpKilledLeavings 字段，开始注入 IL");

            // 找到 listOfLeavingsOut?.AddRange(tmpKilledLeavings) 的 callvirt AddRange
            // 在其后面插入: CapturedLeavings.AddRange(tmpKilledLeavings)
            bool patched = false;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt
                    && codes[i].operand as MethodInfo == addRangeMethod)
                {
                    var insertCodes = new List<CodeInstruction>
                    {
                        new CodeInstruction(OpCodes.Ldsfld, capturedField),
                        new CodeInstruction(OpCodes.Ldsfld, tmpLeavingsField),
                        new CodeInstruction(OpCodes.Callvirt, addRangeMethod)
                    };

                    codes.InsertRange(i + 1, insertCodes);
                    patched = true;
                    Log.Message($"[PawnOwnership] Transpiler: 在 IL offset {i} 后插入 CapturedLeavings.AddRange(tmpKilledLeavings)");
                    break;
                }
            }

            if (!patched)
            {
                Log.Error("[PawnOwnership] Transpiler: 未找到 callvirt AddRange 指令，注入失败");
            }

            return codes;
        }

        // Postfix: 从 CapturedLeavings 取出物品，应用归属
        static void Postfix(Map map)
        {
            Log.Message($"[PawnOwnership-DoLeavingsFor] Postfix: CapturedLeavings.Count={OwnershipInheritanceContext.CapturedLeavings.Count}, PendingOwner={OwnershipInheritanceContext.PendingLeavingsOwner ?? "null"}");

            if (string.IsNullOrEmpty(OwnershipInheritanceContext.PendingLeavingsOwner))
            {
                Log.Message("[PawnOwnership-DoLeavingsFor] Postfix: 无待继承归属，跳过");
                OwnershipInheritanceContext.CapturedLeavings.Clear();
                return;
            }

            if (OwnershipInheritanceContext.CapturedLeavings.Count == 0)
            {
                Log.Message("[PawnOwnership-DoLeavingsFor] Postfix: CapturedLeavings 为空！Transpiler 可能未生效");
                OwnershipInheritanceContext.PendingLeavingsOwner = null;
                return;
            }

            var comp = map?.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null)
            {
                Log.Message("[PawnOwnership-DoLeavingsFor] Postfix: MapComponent 为 null");
                OwnershipInheritanceContext.CapturedLeavings.Clear();
                OwnershipInheritanceContext.PendingLeavingsOwner = null;
                return;
            }

            string owner = OwnershipInheritanceContext.PendingLeavingsOwner;
            foreach (var thing in OwnershipInheritanceContext.CapturedLeavings)
            {
                if (thing == null) continue;
                comp.SetOwner(thing, owner);
                Log.Message($"[PawnOwnership-DoLeavingsFor] Postfix: {thing.ThingID} -> {owner}");
            }

            Log.Message($"[PawnOwnership-DoLeavingsFor] Postfix: 完成，共 {OwnershipInheritanceContext.CapturedLeavings.Count} 个物品继承归属 -> {owner}");

            OwnershipInheritanceContext.CapturedLeavings.Clear();
            OwnershipInheritanceContext.PendingLeavingsOwner = null;
        }
    }
}
