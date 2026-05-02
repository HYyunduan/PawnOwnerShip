using Verse;
using RimWorld;
using HarmonyLib;

namespace PawnOwnership.Patches
{
    /// <summary>
    /// MainTabWindow_Inspect.CurTabs 补丁
    /// 原版对没有预定义 Tab 的 Thing（如物品）不调用 GetInspectTabs()
    /// 此补丁在原版返回 null 时主动调用，让 Patch_Thing_GetInspectTabs 有机会注入 Tab
    /// </summary>
    [HarmonyPatch(typeof(MainTabWindow_Inspect))]
    [HarmonyPatch("CurTabs", MethodType.Getter)]
    public static class Patch_MainTabWindow_Inspect_CurTabs
    {
        static void Postfix(MainTabWindow_Inspect __instance, ref System.Collections.Generic.IEnumerable<InspectTabBase> __result)
        {
            if (__result != null) return;

            Thing selThing = Find.Selector.SingleSelectedThing;
            if (selThing == null) return;

            __result = selThing.GetInspectTabs();
        }
    }
}
