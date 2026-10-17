using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PawnOwnership.Testing
{
    /// <summary>
    /// 启动自检。仅在带 <c>-po.selftest-report=&lt;path&gt;</c> 参数启动时运行。
    ///
    /// 它做的是「运行时真相」校验：Harmony 到底把多少个补丁挂到了多少个方法上。
    /// 这与 tools/PatchAudit 的静态推断互补——静态审计证明「名字能对上」，
    /// 这里证明「游戏里真的挂上了」，能抓到 Prepatcher 改写程序集、其他 mod 抢占、
    /// 以及加载顺序导致的差异。
    ///
    /// 设计约束（见 docs/DECISIONS.md 与确认过的 S3a 设计）：
    ///   - 不 patch 任何东西，不引新程序集；
    ///   - 不碰存档字段，不参与 Multiplayer 同步；
    ///   - 写完报告立即返回，进程由脚本强杀，不调用 Root.Shutdown()（避开退出时序问题）。
    /// </summary>
    public static class SelfTest
    {
        private const string ReportArg = "po.selftest-report";

        /// <summary>注册的补丁方法数低于此值视为异常（当前静态补丁约 20 个方法）。</summary>
        private const int MinPatchedMethods = 20;

        public static void RunIfRequested(Harmony harmony)
        {
            MpTestBoot.ParseIfRequested();
            WritePatchReportIfRequested(harmony);
            StartScenarioIfRequested();
        }

        private static void WritePatchReportIfRequested(Harmony harmony)
        {
            string path;
            if (!GenCommandLine.TryGetCommandLineArg(ReportArg, out path)) return;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                string json = BuildReport(harmony, out bool healthy);
                File.WriteAllText(path, json, new UTF8Encoding(false));

                Log.Message("[PawnOwnership][SelfTest] 报告已写入 " + path
                            + "（healthy=" + healthy + "）");
            }
            catch (Exception ex)
            {
                Log.Error("[PawnOwnership][SelfTest] 写报告失败: " + ex);
            }
        }

        /// <summary>
        /// 带 -po.selftest-scenario=&lt;quickstart|loadsave&gt; 启动时，排队启动场景驱动。
        /// 用 ExecuteWhenFinished 延后到入口场景加载完，避免在 Unity 未就绪时创建 GameObject。
        /// </summary>
        private static void StartScenarioIfRequested()
        {
            string scenario;
            if (!GenCommandLine.TryGetCommandLineArg("po.selftest-scenario", out scenario)) return;
            if (string.IsNullOrEmpty(scenario)) return;

            string save, outDir;
            GenCommandLine.TryGetCommandLineArg("po.selftest-save", out save);
            GenCommandLine.TryGetCommandLineArg("po.selftest-out", out outDir);

            LongEventHandler.ExecuteWhenFinished(delegate
            {
                TestDriver.Start(scenario, save, outDir);
            });
        }

        private static string BuildReport(Harmony harmony, out bool healthy)
        {
            var methods = new List<string>();
            int prefixes = 0, postfixes = 0, transpilers = 0;

            foreach (MethodBase m in harmony.GetPatchedMethods())
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(m);
                if (info == null) continue;

                int p = info.Prefixes.Count(x => x.owner == harmony.Id);
                int q = info.Postfixes.Count(x => x.owner == harmony.Id);
                int t = info.Transpilers.Count(x => x.owner == harmony.Id);
                if (p + q + t == 0) continue; // 被别的 mod patch 的方法，不计入

                prefixes += p;
                postfixes += q;
                transpilers += t;

                string typeName = m.DeclaringType != null ? m.DeclaringType.FullName : "(global)";
                methods.Add(typeName + "." + m.Name);
            }

            methods.Sort(StringComparer.Ordinal);
            healthy = methods.Count >= MinPatchedMethods;

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"stage\": \"L1\",\n");
            sb.Append("  \"healthy\": ").Append(healthy ? "true" : "false").Append(",\n");
            sb.Append("  \"minPatchedMethods\": ").Append(MinPatchedMethods).Append(",\n");
            sb.Append("  \"gameVersion\": ").Append(Quote(SafeVersion())).Append(",\n");
            sb.Append("  \"harmonyId\": ").Append(Quote(harmony.Id)).Append(",\n");
            sb.Append("  \"patchedMethods\": ").Append(methods.Count).Append(",\n");
            sb.Append("  \"prefixes\": ").Append(prefixes).Append(",\n");
            sb.Append("  \"postfixes\": ").Append(postfixes).Append(",\n");
            sb.Append("  \"transpilers\": ").Append(transpilers).Append(",\n");
            sb.Append("  \"methods\": [");
            for (int i = 0; i < methods.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Quote(methods[i]));
            }
            sb.Append("]\n");
            sb.Append("}\n");

            return sb.ToString();
        }

        private static string SafeVersion()
        {
            try { return VersionControl.CurrentVersionString; }
            catch { return "unknown"; }
        }

        private static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
