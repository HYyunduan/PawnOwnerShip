using Verse;
using HarmonyLib;

namespace PawnOwnership.Patches
{
    /// <summary>
    /// Thing.GetInspectString 补丁 - 检查文本追加所属信息
    /// </summary>
    [HarmonyPatch(typeof(Thing))]
    [HarmonyPatch("GetInspectString")]
    public static class Patch_Thing_InspectString
    {
        static void Postfix(Thing __instance, ref string __result)
        {
            if (__instance == null || __instance.Map == null) return;

            var comp = __instance.Map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;

            string owner = comp.GetOwner(__instance);
            string line = string.IsNullOrEmpty(owner)
                ? "所属：无"
                : $"所属：{owner}";

            if (string.IsNullOrEmpty(__result))
                __result = line;
            else
                __result = __result + "\n" + line;
        }
    }
}
