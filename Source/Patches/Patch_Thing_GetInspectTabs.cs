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
        private static ITab_Ownership _cachedTab;

        static void Postfix(Thing __instance, ref IEnumerable<InspectTabBase> __result)
        {
            if (__instance == null) return;

            bool shouldInject = false;

            // 殖民者/奴隶
            if (__instance is Pawn pawn && (pawn.IsColonist || pawn.IsSlave))
                shouldInject = true;
            // 物品
            else if (__instance.def.category == ThingCategory.Item)
                shouldInject = true;
            // 动物（非殖民者/奴隶的 Pawn）
            else if (__instance is Pawn animal && !animal.IsColonist && !animal.IsSlave)
                shouldInject = true;

            if (!shouldInject) return;

            if (_cachedTab == null)
                _cachedTab = new ITab_Ownership();

            var tabs = __result?.ToList() ?? new List<InspectTabBase>();
            if (!tabs.Any(t => t is ITab_Ownership))
                tabs.Add(_cachedTab);
            __result = tabs;
        }
    }
}
