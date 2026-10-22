using System;
using System.Collections.Generic;
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

        // 统计（供启动自检读取，避免"失败被静默吞掉"）
        public static int SucceededCount { get; private set; }
        public static int SkippedInheritedCount { get; private set; }
        public static int FailedCount { get; private set; }

        /// <summary>已注册过的 (方法, 补丁种类) 去重键。</summary>
        private static readonly HashSet<string> PatchedKeys = new HashSet<string>(StringComparer.Ordinal);

        public static void RegisterDynamicPatches(Harmony harmony)
        {
            _harmony = harmony;
            SucceededCount = 0;
            SkippedInheritedCount = 0;
            FailedCount = 0;
            PatchedKeys.Clear();

            var scannerType = typeof(WorkGiver_Scanner);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (Exception ex)
                {
                    Log.Warning("[PawnOwnership] 扫描程序集 " + assembly.FullName + " 时出错: " + ex.Message);
                    continue;
                }

                foreach (var type in types)
                {
                    try
                    {
                        if (!type.IsSubclassOf(scannerType))
                            continue;

                        // 只处理声明在本类的方法；继承来的方法由父类自己处理。
                        // Harmony 对"继承来的 MethodInfo"会抛
                        //   "You can only patch implemented methods/constructors"
                        // 这类尝试以前会产生 600+ 条无效失败日志，掩盖真正的失败。
                        PatchMethodIfExists(type, "JobOnThing",
                            new[] { typeof(Pawn), typeof(Thing), typeof(bool) },
                            typeof(PatchHelpers), "JobOnThing_Prefix");

                        PatchMethodIfExists(type, "JobOnThing",
                            new[] { typeof(Pawn), typeof(Thing), typeof(bool) },
                            typeof(PatchHelpers), "JobOnThing_Postfix", postfix: true);

                        PatchMethodIfExists(type, "HasJobOnThing",
                            new[] { typeof(Pawn), typeof(Thing), typeof(bool) },
                            typeof(PatchHelpers), "HasJobOnThing_Prefix");

                        PatchMethodIfExists(type, "JobOnCell",
                            new[] { typeof(Pawn), typeof(IntVec3), typeof(bool) },
                            typeof(PatchHelpers), "JobOnCell_Prefix");

                        PatchMethodIfExists(type, "HasJobOnCell",
                            new[] { typeof(Pawn), typeof(IntVec3), typeof(bool) },
                            typeof(PatchHelpers), "HasJobOnCell_Prefix");

                        if (MP.enabled)
                        {
                            PatchMethodIfExists(type, "PotentialWorkThingsGlobal",
                                new[] { typeof(Pawn) },
                                typeof(PatchHelpers), "PotentialWorkThingsGlobal_Postfix", postfix: true);

                            PatchMethodIfExists(type, "PotentialWorkCellsGlobal",
                                new[] { typeof(Pawn) },
                                typeof(PatchHelpers), "PotentialWorkCellsGlobal_Postfix", postfix: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        FailedCount++;
                        Log.Warning("[PawnOwnership] 处理类型 " + type.FullName + " 时出错: " + ex.Message);
                    }
                }
            }

            Log.Message("[PawnOwnership] 动态 Patch 完成：成功 " + SucceededCount
                        + "，跳过继承方法 " + SkippedInheritedCount
                        + "，失败 " + FailedCount);
        }

        /// <summary>
        /// 尝试给"该类自己声明"的方法打补丁。
        /// 返回：1 = 成功，0 = 跳过/失败。
        ///
        /// 规则：
        ///   - 继承来的 MethodInfo 直接跳过（Harmony 不接受，且父类会自己处理）；
        ///   - 同一 (方法, 补丁种类) 只注册一次（防御性去重）；
        ///   - 失败不再静默：记 Warning 并计入 FailedCount。
        /// </summary>
        private static int PatchMethodIfExists(Type targetType, string methodName,
            Type[] paramTypes, Type patchClass, string patchMethodName, bool postfix = false)
        {
            var method = targetType.GetMethod(methodName, paramTypes);
            if (method == null) return 0;

            // Harmony 只接受"由该类自己声明"的方法：ReflectedType == DeclaringType
            if (!method.IsDeclaredMember())
            {
                SkippedInheritedCount++;
                return 0;
            }

            string key = method.DeclaringType.FullName + "|" + method.Name + "|" + method.MetadataToken
                         + "|" + (postfix ? "post" : "pre");
            if (!PatchedKeys.Add(key)) return 0;

            var patchMethod = patchClass.GetMethod(patchMethodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (patchMethod == null)
            {
                FailedCount++;
                Log.Warning("[PawnOwnership] 找不到补丁方法: " + patchMethodName);
                return 0;
            }

            try
            {
                var harmonyMethod = new HarmonyMethod(patchMethod);
                if (postfix)
                    _harmony.Patch(method, postfix: harmonyMethod);
                else
                    _harmony.Patch(method, prefix: harmonyMethod);

                SucceededCount++;
                MapComponent_PawnOwnership.DebugLog("[PawnOwnership] Dynamic patch: "
                    + targetType.Name + "." + methodName + " (" + (postfix ? "Postfix" : "Prefix") + ")");
                return 1;
            }
            catch (Exception ex)
            {
                FailedCount++;
                Log.Warning("[PawnOwnership] Patch " + targetType.Name + "." + methodName + " 失败: " + ex.Message);
                return 0;
            }
        }
    }
}
