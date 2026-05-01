using System;
using Verse;
using Verse.AI;
using HarmonyLib;
using RimWorld;
using Multiplayer.API;

namespace PawnOwnership.Patches
{
    /// <summary>
    /// 物品搜索归属拦截 - 通用逻辑
    /// 包裹 validator，在原始验证前加归属检查
    /// </summary>
    public static class ValidatorOwnershipFilter
    {
        /// <summary>
        /// 包裹 validator，加入归属检查
        /// </summary>
        public static void WrapValidator(ref Predicate<Thing> validator, TraverseParms traverseParams)
        {
            if (!MP.enabled || !MP.IsInMultiplayer)
                return;

            Pawn pawn = traverseParams.pawn;
            if (pawn == null || pawn.Map == null)
                return;

            var comp = pawn.Map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null)
                return;

            string pawnOwner = pawn.GetOwner();
            if (string.IsNullOrEmpty(pawnOwner))
                return;

            Predicate<Thing> originalValidator = validator;
            validator = (Thing t) =>
            {
                string thingOwner = comp.GetOwner(t);
                if (!string.IsNullOrEmpty(thingOwner) && thingOwner != pawnOwner)
                    return false;

                return originalValidator == null || originalValidator(t);
            };
        }
    }

    /// <summary>
    /// GenClosest.ClosestThingReachable 补丁
    /// 覆盖动物搜食物、搬运途中拾取等场景
    /// </summary>
    [HarmonyPatch(typeof(GenClosest))]
    [HarmonyPatch("ClosestThingReachable")]
    public static class Patch_GenClosest
    {
        static void Prefix(ref Predicate<Thing> validator, TraverseParms traverseParams)
        {
            ValidatorOwnershipFilter.WrapValidator(ref validator, traverseParams);
        }
    }

    /// <summary>
    /// FoodUtility.SpawnedFoodSearchInnerScan 补丁
    /// 人类殖民者搜食物走这个方法，不走 ClosestThingReachable
    /// </summary>
    [HarmonyPatch(typeof(FoodUtility))]
    [HarmonyPatch("SpawnedFoodSearchInnerScan")]
    public static class Patch_FoodUtility_SpawnedFoodSearchInnerScan
    {
        static void Prefix(ref Predicate<Thing> validator, TraverseParms traverseParams)
        {
            ValidatorOwnershipFilter.WrapValidator(ref validator, traverseParams);
        }
    }
}
