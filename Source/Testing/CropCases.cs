using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace PawnOwnership.Testing
{
    /// <summary>
    /// 「作物选择是否生效」的自动化测试（单机可跑，不依赖联机）。
    ///
    /// 核心断言链：
    ///   区域设定作物 → WorkGiver_GrowerSow.JobOnCell 派出的任务里 plantDefToSow 必须等于区域设定值。
    /// 这一条覆盖了「改了作物但小人仍种旧作物」这个问题的完整数据链路。
    ///
    /// 另外记录归属过滤对播种的影响：归属者应能派活，他人应被拦截（预期行为）。
    /// </summary>
    public static class CropCases
    {
        private const string RiceDefName = "Plant_Rice";
        private const string PotatoDefName = "Plant_Potato";
        private const string GrowerSowGiver = "GrowerSow";

        private sealed class CaseResult
        {
            public string Id;
            public string Name;
            public string Expected;
            public string Actual;
            public string Detail;
            public bool Pass;
        }

        public static bool RunAll(string outDir)
        {
            var results = new List<CaseResult>();
            Zone_Growing zone = null;
            Pawn pawn = null;
            Map map = null;
            string pawnOriginalOwner = null;
            var comp = (MapComponent_PawnOwnership)null;

            try
            {
                map = Find.CurrentMap;
                if (map == null)
                {
                    results.Add(MakeFail("C00", "环境", "需要一张地图", "Find.CurrentMap 为 null"));
                    WriteReport(outDir, results);
                    return false;
                }

                comp = map.GetComponent<MapComponent_PawnOwnership>();
                pawn = FindColonist(map);
                if (pawn == null)
                {
                    results.Add(MakeFail("C00", "环境", "需要一个殖民者", "没有可用殖民者"));
                    WriteReport(outDir, results);
                    return false;
                }
                pawnOriginalOwner = pawn.GetOwner();

                ThingDef rice = DefDatabase<ThingDef>.GetNamed(RiceDefName, false);
                ThingDef potato = DefDatabase<ThingDef>.GetNamed(PotatoDefName, false);
                if (rice == null || potato == null)
                {
                    results.Add(MakeFail("C00", "环境", "需要 Plant_Rice / Plant_Potato 定义",
                        "rice=" + (rice == null ? "null" : rice.defName) + ", potato=" + (potato == null ? "null" : potato.defName)));
                    WriteReport(outDir, results);
                    return false;
                }

                WorkGiverDef giverDef = DefDatabase<WorkGiverDef>.GetNamed(GrowerSowGiver, false);
                var sowGiver = giverDef == null ? null : giverDef.Worker as WorkGiver_GrowerSow;
                if (sowGiver == null)
                {
                    results.Add(MakeFail("C00", "环境", "需要 WorkGiver GrowerSow", "找不到 GrowerSow 或类型不符"));
                    WriteReport(outDir, results);
                    return false;
                }

                // 建一块测试种植区
                zone = BuildGrowingZone(map, pawn, out string siteDetail, out IntVec3 testCell);
                if (zone == null || !testCell.IsValid)
                {
                    results.Add(MakeFail("C00", "环境", "需要一块可种植的空地", siteDetail));
                    WriteReport(outDir, results);
                    return false;
                }
                results.Add(MakePass("C00", "环境", "建立测试种植区", siteDetail, "cells=" + zone.Cells.Count()));

                // ---- C01 设定作物后读取一致 ----
                zone.SetPlantDefToGrow(rice);
                ThingDef readBack = zone.GetPlantDefToGrow();
                results.Add(MakeResult("C01", "设定作物后可读回",
                    rice.defName, readBack == null ? "null" : readBack.defName,
                    "SetPlantDefToGrow(" + rice.defName + ")"));

                // ---- C02 区域扩张后作物保持 ----
                int beforeCells = zone.Cells.Count();
                TryAddMoreCells(map, zone);
                int afterCells = zone.Cells.Count();
                ThingDef afterExpand = zone.GetPlantDefToGrow();
                results.Add(MakeResult("C02", "区域扩张后作物保持",
                    rice.defName, afterExpand == null ? "null" : afterExpand.defName,
                    "cells " + beforeCells + " -> " + afterCells));

                // ---- C03 派活时任务里带的是设定作物（核心）----
                Job jobRice = MakeSowJob(sowGiver, pawn, testCell);
                string riceActual = jobRice == null
                    ? "null job"
                    : (jobRice.plantDefToSow == null ? "null def" : jobRice.plantDefToSow.defName);
                results.Add(MakeResult("C03", "派活时 plantDefToSow = 区域设定（水稻）",
                    rice.defName, riceActual, "cell=" + testCell));

                // ---- C04 换成土豆后派活随之改变 ----
                zone.SetPlantDefToGrow(potato);
                ThingDef potatoReadBack = zone.GetPlantDefToGrow();
                results.Add(MakeResult("C04", "改为土豆后区域字段随之改变",
                    potato.defName, potatoReadBack == null ? "null" : potatoReadBack.defName,
                    "SetPlantDefToGrow(" + potato.defName + ")"));

                // JobOnCell 依赖扫描阶段的 wantedPlantDef 状态，必须先走一遍扫描入口再派活，
                // 否则拿到的是上一次扫描残留的作物（这是测试方法问题，不是产品问题）。
                Job jobPotato = MakeSowJob(sowGiver, pawn, testCell);
                string potatoActual = jobPotato == null
                    ? "null job"
                    : (jobPotato.plantDefToSow == null ? "null def" : jobPotato.plantDefToSow.defName);
                results.Add(MakeResult("C05", "改为土豆后派活随之变为土豆",
                    potato.defName, potatoActual, "SetPlantDefToGrow(" + potato.defName + ") 后重新扫描"));

                // ---- C06 改回水稻再次生效（双向切换）----
                zone.SetPlantDefToGrow(rice);
                Job jobRice2 = MakeSowJob(sowGiver, pawn, testCell);
                string riceActual2 = jobRice2 == null
                    ? "null job"
                    : (jobRice2.plantDefToSow == null ? "null def" : jobRice2.plantDefToSow.defName);
                results.Add(MakeResult("C06", "改回水稻后再次生效",
                    rice.defName, riceActual2, "第二次切回"));

                // ---- C07 归属者自身不被过滤误伤 ----
                comp.SetOwnerZone(zone.ID, "PO_TestAlpha");
                pawn.SetOwner("PO_TestAlpha");
                Job jobOwned = MakeSowJob(sowGiver, pawn, testCell);
                results.Add(MakeResult("C07", "区域归属=小人归属时仍能派活",
                    "job != null", jobOwned == null ? "null job" : "已派活",
                    "pawnOwner=PO_TestAlpha, zoneOwner=PO_TestAlpha"));

                // ---- C08 他人区域被拦截（预期行为）----
                comp.SetOwnerZone(zone.ID, "PO_TestBeta");
                Job jobForeign = MakeSowJob(sowGiver, pawn, testCell);
                results.Add(MakeResult("C08", "区域归属为他人时被拦截（预期行为）",
                    "null job", jobForeign == null ? "null job" : "已派活",
                    "pawnOwner=PO_TestAlpha, zoneOwner=PO_TestBeta"));

                int passed = results.Count(r => r.Pass);
                WriteReport(outDir, results);
                Log.Message("[PawnOwnership][CropCases] 完成：" + passed + "/" + results.Count + " 通过");
                return passed == results.Count;
            }
            catch (Exception ex)
            {
                results.Add(MakeFail("C99", "异常", "不应抛异常", ex.GetType().Name + ": " + ex.Message));
                WriteReport(outDir, results);
                Log.Error("[PawnOwnership][CropCases] 异常: " + ex);
                return false;
            }
            finally
            {
                try { if (comp != null && zone != null) comp.RemoveOwnerZone(zone.ID); } catch { }
                try { if (pawn != null) pawn.SetOwner(pawnOriginalOwner); } catch { }
                try { if (zone != null) zone.Delete(); } catch { }
            }
        }

        public static Pawn FindColonist(Map map)
        {
            return map.mapPawns.FreeColonists.FirstOrDefault(p =>
                p != null && !p.Downed && !p.Dead && p.workSettings != null && p.jobs != null);
        }

        /// <summary>
        /// 在殖民者附近找一片无屋顶、未迷雾、无建筑的空地，建一个 Zone_Growing。
        /// 返回第一个「能真正派活」的格子（说明地形可种植）。
        /// </summary>
        public static Zone_Growing BuildGrowingZone(Map map, Pawn pawn, out string detail, out IntVec3 testCell)
        {
            detail = "";
            testCell = IntVec3.Invalid;

            var zone = new Zone_Growing(map.zoneManager);
            var candidates = new List<IntVec3>();
            CellRect rect = CellRect.CenteredOn(pawn.Position, 12);

            foreach (IntVec3 c in rect.Cells)
            {
                if (!c.InBounds(map)) continue;
                if (c.Fogged(map)) continue;
                if (map.roofGrid.Roofed(c)) continue;
                if (map.zoneManager.ZoneAt(c) != null) continue;
                if (map.thingGrid.ThingsListAt(c).Count > 0) continue;
                candidates.Add(c);
                if (candidates.Count >= 64) break;
            }

            int added = 0;
            foreach (IntVec3 c in candidates)
            {
                try { zone.AddCell(c); added++; }
                catch { }
                if (added >= 9) break;
            }

            detail = "候选=" + candidates.Count + " 加入=" + added + " 中心=" + pawn.Position;

            if (added == 0)
            {
                try { zone.Delete(); } catch { }
                return null;
            }

            // 找一块能真正派活的格子：地形可种植 + 小人可达
            ThingDef rice = DefDatabase<ThingDef>.GetNamed(RiceDefName, false);
            WorkGiverDef giverDef = DefDatabase<WorkGiverDef>.GetNamed(GrowerSowGiver, false);
            var giver = giverDef == null ? null : giverDef.Worker as WorkGiver_GrowerSow;

            if (giver != null && rice != null)
            {
                zone.SetPlantDefToGrow(rice);
                foreach (IntVec3 c in zone.Cells)
                {
                    try
                    {
                        if (giver.JobOnCell(pawn, c, false) != null)
                        {
                            testCell = c;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (!testCell.IsValid)
            {
                try { zone.Delete(); } catch { }
                return null;
            }

            detail += " 测试格=" + testCell;
            return zone;
        }

        private static void TryAddMoreCells(Map map, Zone_Growing zone)
        {
            try
            {
                IntVec3 baseCell = zone.Cells.First();
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;

                        IntVec3 c = baseCell + new IntVec3(dx, 0, dz);
                        if (!c.InBounds(map)) continue;
                        if (c.Fogged(map) || map.roofGrid.Roofed(c)) continue;
                        if (map.zoneManager.ZoneAt(c) != null) continue;
                        if (map.thingGrid.ThingsListAt(c).Count > 0) continue;

                        try { zone.AddCell(c); return; } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 走完整的"扫描 → 派活"入口再取任务。
        /// WorkGiver_GrowerSow.JobOnCell 依赖扫描阶段写入的 wantedPlantDef，
        /// 直接调用会拿到上一次扫描残留的作物（测试方法问题，不是产品问题）。
        /// </summary>
        private static Job MakeSowJob(WorkGiver_GrowerSow giver, Pawn pawn, IntVec3 cell)
        {
            try
            {
                var cells = giver.PotentialWorkCellsGlobal(pawn);
                if (cells != null) { foreach (IntVec3 _ in cells) { } }
            }
            catch { }
            return giver.JobOnCell(pawn, cell, false);
        }

        private static CaseResult MakeResult(string id, string name, string expected, string actual, string detail)
        {
            bool pass = string.Equals(expected, actual, StringComparison.Ordinal);
            if (expected == "job != null") pass = actual != "null job";
            return new CaseResult
            {
                Id = id, Name = name, Expected = expected, Actual = actual, Detail = detail, Pass = pass
            };
        }

        private static CaseResult MakePass(string id, string name, string expected, string actual, string detail)
            => new CaseResult { Id = id, Name = name, Expected = expected, Actual = actual, Detail = detail, Pass = true };

        private static CaseResult MakeFail(string id, string name, string expected, string actual)
            => new CaseResult { Id = id, Name = name, Expected = expected, Actual = actual, Detail = "", Pass = false };

        private static void WriteReport(string outDir, List<CaseResult> results)
        {
            try
            {
                if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(Path.GetTempPath(), "po-crop");
                Directory.CreateDirectory(outDir);

                int passed = results.Count(r => r.Pass);
                int failed = results.Count - passed;

                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"ok\": ").Append(failed == 0 ? "true" : "false").Append(",\n");
                sb.Append("  \"total\": ").Append(results.Count).Append(",\n");
                sb.Append("  \"passed\": ").Append(passed).Append(",\n");
                sb.Append("  \"failed\": ").Append(failed).Append(",\n");
                sb.Append("  \"cases\": [\n");
                for (int i = 0; i < results.Count; i++)
                {
                    CaseResult r = results[i];
                    sb.Append("    {\"id\":").Append(Q(r.Id))
                      .Append(",\"name\":").Append(Q(r.Name))
                      .Append(",\"pass\":").Append(r.Pass ? "true" : "false")
                      .Append(",\"expected\":").Append(Q(r.Expected))
                      .Append(",\"actual\":").Append(Q(r.Actual))
                      .Append(",\"detail\":").Append(Q(r.Detail)).Append("}");
                    if (i < results.Count - 1) sb.Append(",");
                    sb.Append("\n");
                }
                sb.Append("  ]\n}\n");

                File.WriteAllText(Path.Combine(outDir, "crop-report.json"), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Error("[PawnOwnership][CropCases] 写报告失败: " + ex);
            }
        }

        private static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(ch); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
