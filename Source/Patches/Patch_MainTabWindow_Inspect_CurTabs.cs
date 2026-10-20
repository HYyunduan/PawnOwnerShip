using System.Collections.Generic;
using Verse;
using RimWorld;
using HarmonyLib;

namespace PawnOwnership.Patches
{
    /// <summary>
    /// MainTabWindow_Inspect.CurTabs 补丁
    ///
    /// 原版行为：
    ///   - 单选且 Thing 没有预定义 Tab 时不调用 GetInspectTabs()；
    ///   - **多选时直接返回 null**（除了全部为 IStorageGroupMember 的特殊情况），
    ///     因此多选下不会出现任何页签。
    ///
    /// 本补丁做两件事：
    ///   1. 单选：原版返回 null 时主动调用 GetInspectTabs()，让所属页签能被注入；
    ///   2. 多选：只要选中里存在支持设置归属的对象，就返回「所属」页签，
    ///      从而支持批量查看/修改归属（页签内部读取 Find.Selector.SelectedObjects）。
    /// </summary>
    [HarmonyPatch(typeof(MainTabWindow_Inspect))]
    [HarmonyPatch("CurTabs", MethodType.Getter)]
    public static class Patch_MainTabWindow_Inspect_CurTabs
    {
        static void Postfix(ref IEnumerable<InspectTabBase> __result)
        {
            if (__result != null) return;

            int numSelected = Find.Selector.NumSelected;

            // 多选：批量归属页签
            if (numSelected > 1)
            {
                if (OwnershipBulk.SupportedCount() > 0)
                {
                    __result = new List<InspectTabBase> { ITab_Ownership.GetShared() };
                }
                return;
            }

            if (numSelected != 1) return;

            Thing selThing = Find.Selector.SingleSelectedThing;
            if (selThing == null) return;

            __result = selThing.GetInspectTabs();
        }
    }
}
