using System.Collections.Generic;
using System.Linq;
using Verse;
using HarmonyLib;

namespace PawnOwnership.Patches
{
    /// <summary>
    /// Thing.GetInspectTabs 补丁 - 注入所属 Tab
    /// 替代原 XML Patch，统一处理殖民者、物品、动物
    /// </summary>
    [HarmonyPatch(typeof(Thing))]
    [HarmonyPatch("GetInspectTabs")]
    public static class Patch_Thing_GetInspectTabs
    {
        static void Postfix(Thing __instance, ref IEnumerable<InspectTabBase> __result)
        {
            if (__instance == null) return;

            // 是否支持设置归属（Pawn 或物品）——统一由 OwnershipBulk 判定，
            // 保证与多选页签的可见性规则一致。
            if (!OwnershipBulk.IsSupported(__instance)) return;

            var tabs = __result?.ToList() ?? new List<InspectTabBase>();
            if (!tabs.Any(t => t is ITab_Ownership))
                tabs.Add(ITab_Ownership.GetShared());
            __result = tabs;
        }
    }
}
