// PatchAudit —— Harmony 补丁绑定审计
//
// 用途：在不启动游戏的前提下，校验每个 Prefix/Postfix 的参数是否真的能在目标方法上绑定。
// Harmony 按“参数名”绑定原方法参数；名字对不上会抛 HarmonyException，而动态补丁注册处的
// try/catch 会把异常吞掉，导致补丁静默失效（见 RULES.md 踩坑日志 K-005）。
//
// 用法：
//   PatchAudit <libsDir> <modDll>            校验绑定，失败返回退出码 1
//   PatchAudit --dump <assembly> <TypeName>  打印某个类型的方法/字段/属性（调试 API 用）

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace PatchAudit
{
    internal sealed class MethodInfo2
    {
        public string Name;
        public List<string> Params = new List<string>();
        public bool IsAbstract;
        public bool IsVirtual;
        public bool IsStatic;
        public override string ToString()
        {
            string flags = (IsStatic ? "static " : "") + (IsVirtual ? "virtual " : "") + (IsAbstract ? "abstract " : "");
            return flags + Name + "(" + string.Join(", ", Params) + ")";
        }
    }

    internal sealed class TypeInfo2
    {
        public string FullName;
        public string SimpleName;
        public Dictionary<string, List<MethodInfo2>> Methods =
            new Dictionary<string, List<MethodInfo2>>(StringComparer.Ordinal);
        public HashSet<string> Fields = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> Properties = new HashSet<string>(StringComparer.Ordinal);
    }

    internal sealed class AssemblyDump
    {
        public Dictionary<string, TypeInfo2> ByFull = new Dictionary<string, TypeInfo2>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TypeInfo2>> _bySimple =
            new Dictionary<string, List<TypeInfo2>>(StringComparer.Ordinal);

        public static AssemblyDump Load(string path)
        {
            var dump = new AssemblyDump();
            using (var fs = File.OpenRead(path))
            using (var pe = new PEReader(fs))
            {
                var reader = pe.GetMetadataReader();

                foreach (var h in reader.TypeDefinitions)
                {
                    var td = reader.GetTypeDefinition(h);
                    var ns = reader.GetString(td.Namespace);
                    var name = reader.GetString(td.Name);
                    if (name == "<Module>") continue;

                    var ti = new TypeInfo2
                    {
                        FullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name,
                        SimpleName = name,
                    };

                    foreach (var fh in td.GetFields())
                        ti.Fields.Add(reader.GetString(reader.GetFieldDefinition(fh).Name));
                    foreach (var ph in td.GetProperties())
                        ti.Properties.Add(reader.GetString(reader.GetPropertyDefinition(ph).Name));

                    foreach (var mh in td.GetMethods())
                    {
                        var md = reader.GetMethodDefinition(mh);
                        var mi = new MethodInfo2
                        {
                            Name = reader.GetString(md.Name),
                            IsAbstract = (md.Attributes & MethodAttributes.Abstract) != 0,
                            IsVirtual = (md.Attributes & MethodAttributes.Virtual) != 0,
                            IsStatic = (md.Attributes & MethodAttributes.Static) != 0,
                        };
                        foreach (var ph in md.GetParameters())
                        {
                            var pd = reader.GetParameter(ph);
                            if (pd.SequenceNumber == 0) continue; // 0 是返回值
                            mi.Params.Add(reader.GetString(pd.Name));
                        }

                        List<MethodInfo2> list;
                        if (!ti.Methods.TryGetValue(mi.Name, out list))
                        {
                            list = new List<MethodInfo2>();
                            ti.Methods[mi.Name] = list;
                        }
                        list.Add(mi);
                    }

                    dump.ByFull[ti.FullName] = ti;
                    List<TypeInfo2> sl;
                    if (!dump._bySimple.TryGetValue(name, out sl))
                    {
                        sl = new List<TypeInfo2>();
                        dump._bySimple[name] = sl;
                    }
                    sl.Add(ti);
                }
            }
            return dump;
        }

        public TypeInfo2 Find(string typeName)
        {
            TypeInfo2 t;
            if (ByFull.TryGetValue(typeName, out t)) return t;
            List<TypeInfo2> list;
            if (_bySimple.TryGetValue(typeName, out list) && list.Count == 1) return list[0];
            return null;
        }

        public void Dump(TypeInfo2 ti)
        {
            Console.WriteLine("type " + ti.FullName);
            Console.WriteLine("  fields    : " + string.Join(", ", ti.Fields));
            Console.WriteLine("  properties: " + string.Join(", ", ti.Properties));
            foreach (var kv in ti.Methods)
                foreach (var m in kv.Value)
                    Console.WriteLine("  method    : " + m);
        }
    }

    internal sealed class Binding
    {
        public string TargetType;
        public string TargetMember;
        public bool IsProperty;
        public string PatchType;
        public string PatchMethod;
        public string Note = "";
    }

    internal static class Program
    {
        private static readonly HashSet<string> Special = new HashSet<string>(StringComparer.Ordinal)
        {
            "__instance", "__result", "__resultRef", "__state", "__runOriginal",
            "__originalMethod", "__args", "__exception",
        };

        private static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "--find")
            {
                var d = AssemblyDump.Load(args[1]);
                string member = args[2];
                int found = 0;
                foreach (var ti in d.ByFull.Values)
                {
                    bool hasMethod = ti.Methods.ContainsKey(member);
                    bool hasField = ti.Fields.Contains(member);
                    bool hasProp = ti.Properties.Contains(member);
                    if (!hasMethod && !hasField && !hasProp) continue;
                    found++;
                    Console.WriteLine("type " + ti.FullName);
                    if (hasMethod)
                        foreach (var m in ti.Methods[member]) Console.WriteLine("   method    : " + m);
                    if (hasField) Console.WriteLine("   field     : " + member);
                    if (hasProp) Console.WriteLine("   property  : " + member);
                }
                if (found == 0) Console.WriteLine("未找到声明 '" + member + "' 的类型");
                return 0;
            }

            if (args.Length >= 3 && args[0] == "--types")
            {
                var d = AssemblyDump.Load(args[1]);
                string pattern = args[2];
                int n = 0;
                foreach (var ti in d.ByFull.Values)
                {
                    if (ti.FullName == null) continue;
                    if (ti.FullName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    n++;
                    Console.WriteLine("type " + ti.FullName);
                    foreach (var kv in ti.Methods)
                        foreach (var m in kv.Value) Console.WriteLine("   method    : " + m);
                }
                Console.WriteLine("== 匹配类型数: " + n + " ==");
                return 0;
            }

            if (args.Length >= 3 && args[0] == "--dump")
            {
                var d = AssemblyDump.Load(args[1]);
                var ti = d.Find(args[2]);
                if (ti == null) { Console.WriteLine("type not found: " + args[2]); return 2; }
                d.Dump(ti);
                return 0;
            }

            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: PatchAudit <libsDir> <modDll>");
                Console.Error.WriteLine("       PatchAudit --dump <assembly> <TypeName>");
                return 2;
            }

            var game = AssemblyDump.Load(Path.Combine(args[0], "Assembly-CSharp.dll"));
            var mod = AssemblyDump.Load(args[1]);

            Console.WriteLine("Assembly-CSharp types : " + game.ByFull.Count);
            Console.WriteLine("PawnOwnership types   : " + mod.ByFull.Count);
            Console.WriteLine();

            var bindings = new List<Binding>
            {
                new Binding { TargetType="DesignationManager", TargetMember="AddDesignation", PatchType="Patch_DesignationManager_AddDesignation", PatchMethod="Postfix" },
                new Binding { TargetType="DesignationManager", TargetMember="RemoveDesignation", PatchType="Patch_DesignationManager_RemoveDesignation", PatchMethod="Prefix" },
                new Binding { TargetType="Thing", TargetMember="DeSpawn", PatchType="Patch_Thing_DeSpawn", PatchMethod="Prefix" },
                new Binding { TargetType="Thing", TargetMember="SpawnSetup", PatchType="Patch_Thing_SpawnSetup", PatchMethod="Postfix" },
                new Binding { TargetType="GenLeaving", TargetMember="DoLeavingsFor", PatchType="Patch_GenLeaving_DoLeavingsFor", PatchMethod="Prefix" },
                new Binding { TargetType="GenLeaving", TargetMember="DoLeavingsFor", PatchType="Patch_GenLeaving_DoLeavingsFor", PatchMethod="Postfix" },
                new Binding { TargetType="GenSpawn", TargetMember="Spawn", PatchType="Patch_GenSpawn_Spawn", PatchMethod="Postfix" },
                new Binding { TargetType="Designator_Build", TargetMember="DesignateSingleCell", PatchType="Patch_Designator_Build_DesignateSingleCell", PatchMethod="Postfix" },
                new Binding { TargetType="JobDriver_Mine", TargetMember="DoDamage", PatchType="Patch_JobDriver_Mine_DoDamage", PatchMethod="Prefix" },
                new Binding { TargetType="JobDriver_Mine", TargetMember="DoDamage", PatchType="Patch_JobDriver_Mine_DoDamage", PatchMethod="Postfix" },
                new Binding { TargetType="ZoneManager", TargetMember="RegisterZone", PatchType="Patch_ZoneManager_RegisterZone", PatchMethod="Postfix" },
                new Binding { TargetType="ZoneManager", TargetMember="DeregisterZone", PatchType="Patch_ZoneManager_DeregisterZone", PatchMethod="Prefix" },
                new Binding { TargetType="TickManager", TargetMember="TickManagerUpdate", PatchType="Patch_TickManager_TickManagerUpdate", PatchMethod="Postfix" },
                new Binding { TargetType="Pawn", TargetMember="SpawnSetup", PatchType="Patch_Pawn_SpawnSetup_OwnershipSync", PatchMethod="Postfix" },
                new Binding { TargetType="StoreUtility", TargetMember="TryFindBestBetterStoreCellForWorker", PatchType="Patch_TryFindBestBetterStoreCellForWorker", PatchMethod="Prefix" },
                new Binding { TargetType="WorkGiver_ConstructDeliverResources", TargetMember="IsNewValidNearbyNeeder", PatchType="Patch_IsNewValidNearbyNeeder", PatchMethod="Postfix" },
                new Binding { TargetType="HaulAIUtility", TargetMember="PawnCanAutomaticallyHaul", PatchType="Patch_HaulAIUtility", PatchMethod="Prefix" },
                new Binding { TargetType="GenClosest", TargetMember="ClosestThingReachable", PatchType="Patch_GenClosest", PatchMethod="Prefix" },
                new Binding { TargetType="FoodUtility", TargetMember="SpawnedFoodSearchInnerScan", PatchType="Patch_FoodUtility_SpawnedFoodSearchInnerScan", PatchMethod="Prefix" },
                new Binding { TargetType="JobDriver", TargetMember="DriverTickInterval", PatchType="Patch_JobDriver_DriverTickInterval", PatchMethod="Prefix" },
                new Binding { TargetType="JobDriver", TargetMember="DriverTickInterval", PatchType="Patch_JobDriver_DriverTickInterval", PatchMethod="Postfix" },
                new Binding { TargetType="Thing", TargetMember="GetInspectString", PatchType="Patch_Thing_InspectString", PatchMethod="Postfix" },
                new Binding { TargetType="Thing", TargetMember="GetInspectTabs", PatchType="Patch_Thing_GetInspectTabs", PatchMethod="Postfix" },
                new Binding { TargetType="MainTabWindow_Inspect", TargetMember="CurTabs", IsProperty=true, PatchType="Patch_MainTabWindow_Inspect_CurTabs", PatchMethod="Postfix" },

                // 动态补丁：同一个 helper 套到所有 WorkGiver_Scanner 子类上
                new Binding { TargetType="WorkGiver_Scanner", TargetMember="JobOnThing", PatchType="PatchHelpers", PatchMethod="JobOnThing_Prefix", Note="dynamic" },
                new Binding { TargetType="WorkGiver_Scanner", TargetMember="JobOnThing", PatchType="PatchHelpers", PatchMethod="JobOnThing_Postfix", Note="dynamic" },
                new Binding { TargetType="WorkGiver_Scanner", TargetMember="HasJobOnThing", PatchType="PatchHelpers", PatchMethod="HasJobOnThing_Prefix", Note="dynamic" },
                new Binding { TargetType="WorkGiver_Scanner", TargetMember="JobOnCell", PatchType="PatchHelpers", PatchMethod="JobOnCell_Prefix", Note="dynamic" },
                new Binding { TargetType="WorkGiver_Scanner", TargetMember="HasJobOnCell", PatchType="PatchHelpers", PatchMethod="HasJobOnCell_Prefix", Note="dynamic" },
                new Binding { TargetType="WorkGiver_Scanner", TargetMember="PotentialWorkThingsGlobal", PatchType="PatchHelpers", PatchMethod="PotentialWorkThingsGlobal_Postfix", Note="dynamic" },
                new Binding { TargetType="WorkGiver_Scanner", TargetMember="PotentialWorkCellsGlobal", PatchType="PatchHelpers", PatchMethod="PotentialWorkCellsGlobal_Postfix", Note="dynamic" },
            };

            int ok = 0, fail = 0;

            foreach (var b in bindings)
            {
                string label = b.PatchType + "." + b.PatchMethod + " -> " + b.TargetType + "." + b.TargetMember;

                var tt = game.Find(b.TargetType);
                if (tt == null) { Console.WriteLine("FAIL   " + label + "  (目标类型不存在)"); fail++; continue; }

                var pt = mod.Find(b.PatchType);
                if (pt == null) { Console.WriteLine("FAIL   " + label + "  (补丁类型不存在)"); fail++; continue; }

                if (b.IsProperty)
                {
                    if (!tt.Properties.Contains(b.TargetMember))
                    {
                        Console.WriteLine("FAIL   " + label + "  (目标属性不存在)");
                        fail++;
                        continue;
                    }
                    ok++;
                    Console.WriteLine("OK     " + label);
                    continue;
                }

                List<MethodInfo2> overlords;
                if (!tt.Methods.TryGetValue(b.TargetMember, out overlords) || overlords.Count == 0)
                {
                    Console.WriteLine("FAIL   " + label + "  (目标方法不存在)");
                    fail++;
                    continue;
                }

                List<MethodInfo2> plist;
                if (!pt.Methods.TryGetValue(b.PatchMethod, out plist) || plist.Count == 0)
                {
                    Console.WriteLine("FAIL   " + label + "  (补丁方法不存在)");
                    fail++;
                    continue;
                }
                var patch = plist[0];

                // 只看需要绑定到原方法参数上的那些（排除 __xxx / ___field）
                var need = patch.Params
                    .Where(p => !Special.Contains(p) && !p.StartsWith("___", StringComparison.Ordinal))
                    .ToList();

                // 按 Harmony 的规则：找参数名全覆盖的那个重载
                var match = overlords.FirstOrDefault(m => need.All(n => m.Params.Contains(n)));

                if (match == null)
                {
                    fail++;
                    Console.WriteLine("FAIL   " + label + (b.Note == "" ? "" : "  [" + b.Note + "]"));
                    Console.WriteLine("       补丁参数: " + string.Join(", ", patch.Params));
                    Console.WriteLine("       需要绑定: " + (need.Count == 0 ? "(无)" : string.Join(", ", need)));
                    foreach (var m in overlords)
                        Console.WriteLine("       目标重载: " + m);
                }
                else
                {
                    ok++;
                    Console.WriteLine("OK     " + label + (b.Note == "" ? "" : "  [" + b.Note + "]"));
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== RESULT ok=" + ok + " fail=" + fail + " ===");
            return fail == 0 ? 0 : 1;
        }
    }
}
