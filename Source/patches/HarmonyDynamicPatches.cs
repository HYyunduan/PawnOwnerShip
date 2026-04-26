using System;
using System.Reflection;
using Verse;
using Verse.AI;
using RimWorld;
using HarmonyLib;
using Multiplayer.API;

namespace PawnOwnership.Patches
{
    public static class HarmonyDynamicPatches
    {
        private static Harmony _harmony;
        
        // ==========================================
        // 动态 Patch 注册
        // ==========================================
        
        /// <summary>
        /// 注册动态 Patch
        /// </summary>
        public static void RegisterDynamicPatches(Harmony harmony)
        {
            _harmony = harmony;
            
            var scannerType = typeof(WorkGiver_Scanner);
            int patchCount = 0;
            
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        // 只处理具体的子类（非抽象类）
                        if (!type.IsSubclassOf(scannerType) || type.IsAbstract)
                            continue;
                        
                        patchCount += PatchMethodIfExists(type, "JobOnThing", 
                            new[] { typeof(Pawn), typeof(Thing), typeof(bool) },
                            typeof(PatchHelpers), "JobOnThing_Prefix");
                        
                        // JobOnThing Postfix - 检查搬运目标区域归属
                        patchCount += PatchMethodIfExists(type, "JobOnThing", 
                            new[] { typeof(Pawn), typeof(Thing), typeof(bool) },
                            typeof(PatchHelpers), "JobOnThing_Postfix", postfix: true);
                        
                        patchCount += PatchMethodIfExists(type, "HasJobOnThing", 
                            new[] { typeof(Pawn), typeof(Thing), typeof(bool) },
                            typeof(PatchHelpers), "HasJobOnThing_Prefix");
                        
                        patchCount += PatchMethodIfExists(type, "JobOnCell", 
                            new[] { typeof(Pawn), typeof(IntVec3), typeof(bool) },
                            typeof(PatchHelpers), "JobOnCell_Prefix");
                        
                        patchCount += PatchMethodIfExists(type, "HasJobOnCell", 
                            new[] { typeof(Pawn), typeof(IntVec3), typeof(bool) },
                            typeof(PatchHelpers), "HasJobOnCell_Prefix");
                        
                        // 候选物品过滤 - PotentialWorkThingsGlobal（仅多人模式）
                        if (MP.enabled)
                        {
                            patchCount += PatchMethodIfExists(type, "PotentialWorkThingsGlobal",
                                new[] { typeof(Pawn) },
                                typeof(PatchHelpers), "PotentialWorkThingsGlobal_Postfix", postfix: true);
                        }
                        
                        // 候选格子过滤 - PotentialWorkCellsGlobal（仅多人模式）
                        if (MP.enabled)
                        {
                            patchCount += PatchMethodIfExists(type, "PotentialWorkCellsGlobal",
                                new[] { typeof(Pawn) },
                                typeof(PatchHelpers), "PotentialWorkCellsGlobal_Postfix", postfix: true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[PawnOwnership] 扫描程序集 {assembly.FullName} 时出错: {ex.Message}");
                }
            }
            
            Log.Message($"[PawnOwnership] 动态 Patch 完成，共注册 {patchCount} 个补丁");
        }
        
        /// <summary>
        /// 如果方法存在，则打 Patch
        /// 返回：1 = 成功 patch，0 = 未 patch
        /// </summary>
        private static int PatchMethodIfExists(Type targetType, string methodName, 
            Type[] paramTypes, Type patchClass, string patchMethodName, bool postfix = false)
        {
            try
            {
                var method = targetType.GetMethod(methodName, paramTypes);
                if (method == null) return 0;
                
                var patchMethod = patchClass.GetMethod(patchMethodName, 
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                
                if (patchMethod == null)
                {
                    Log.Warning($"[PawnOwnership] 找不到 {(postfix ? "Postfix" : "Prefix")} 方法: {patchMethodName}");
                    return 0;
                }
                
                var harmonyMethod = new HarmonyMethod(patchMethod);
                
                if (postfix)
                {
                    _harmony.Patch(method, postfix: harmonyMethod);
                }
                else
                {
                    _harmony.Patch(method, prefix: harmonyMethod);
                }
                
                MapComponent_PawnOwnership.DebugLog($"[PawnOwnership] Dynamic patch: {targetType.Name}.{methodName} ({(postfix ? "Postfix" : "Prefix")})");
                return 1;
            }
            catch (Exception ex)
            {
                MapComponent_PawnOwnership.DebugLog($"[PawnOwnership] Patch {targetType.Name}.{methodName} 失败: {ex.Message}");
                return 0;
            }
        }
    }
}