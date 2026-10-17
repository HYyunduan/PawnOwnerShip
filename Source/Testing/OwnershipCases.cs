using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PawnOwnership.Testing
{
    /// <summary>
    /// 归属自动化用例 O01–O11（L2 运行时）。
    ///
    /// 设计约束：
    ///   - 每个用例独立 try/catch，单例失败不中断整批；
    ///   - 用例自建 Thing（ThingMaker/GenSpawn），不依赖存档里恰好有什么；用完清理；
    ///   - 只调用真实游戏 API 与 mod 的真实入口，不在测试里复制归属判定逻辑；
    ///   - 无法可靠触发的用例标记为失败并写明原因，不伪造通过；
    ///   - 结论分级（已核实 / 推断 / 未验证）写在注释里，每条 API 用法都附一手的元数据依据。
    ///
    /// API 依据（一手证据：Source/Libs/Assembly-CSharp.dll 的元数据经 tools/PatchAudit 与
    /// 自建 IL 读取器核对，见 RULES.md 铁律 2）：
    ///   - O04/O05：DesignationManager.AddDesignation / RemoveDesignation 会触发 mod 补丁；
    ///       AddDesignation 的 Postfix 只把消息压入静态 syncQueue，真正落库在
    ///       TickManager.TickManagerUpdate 的 Postfix 调 MapComponent_PawnOwnership.ProcessSyncQueue()。
    ///       本测试直接调用同一个 ProcessSyncQueue()（真实入口），避免依赖下一帧。
    ///   - O06：ZoneManager.RegisterZone / DeregisterZone；区域归属同样走 syncQueue。
    ///   - O07：GenLeaving.DoLeavingsFor 的 6 参重载是被 patch 的那个；Thing.Destroy 的 IL 显示它经
    ///       4 参重载转发到 6 参重载，且 4 参重载传入的 listOfLeavingsOut 非 null
    ///       （6 参 IL 里只有在 listOfLeavingsOut != null 时才执行 AddRange(tmpKilledLeavings)）。
    ///       Wall 的 XML 中 leaveResourcesWhenKilled=false，所以用 DestroyMode.Deconstruct
    ///       （CanBuildingLeaveResources 对 Deconstruct 走 resourcesFractionWhenDeconstructed != 0）。
    ///   - O08：Blueprint.TryReplaceWithSolidThing 的 IL 显示顺序为
    ///       MakeSolidThing -> WipeExistingThings -> blueprint.Destroy(Vanish) -> GenSpawn.Spawn(frame)，
    ///       正好命中 mod 在 Thing.DeSpawn Prefix（暂存）与 Thing.SpawnSetup Postfix（恢复）的链路。
    ///   - O10/O11：动态补丁把所有 WorkGiver_Scanner 子类的 HasJobOnThing 挂上
    ///       PatchHelpers.HasJobOnThing_Prefix；WorkGiver_Deconstruct.HasJobOnThing(pawn, t, forced)
    ///       是子类声明的 override（参数名逐字为 pawn/t/forced），且它内部对
    ///       WorkGiver_RemoveBuilding.HasJobOnThing 是 call（非 virtual），后者也在声明列表里、同样被 patch。
    ///       这条路径不经过 MP.IsInMultiplayer 门禁（只有 PotentialWorkThingsGlobal/CellsGlobal 有该门禁）。
    ///       用例先在运行时用 Harmony.GetPatchInfo 确认前缀真的挂上了，再断言行为。
    /// </summary>
    public static class OwnershipCases
    {
        private const string ModHarmonyId = "omas.PawnOwnership";

        private const string OwnerA = "PO_TestAlpha";
        private const string OwnerB = "PO_TestBeta";

        private const int TotalCases = 11;

        // =====================================================================
        // 公共入口
        // =====================================================================

        /// <summary>
        /// 跑全部用例，写 &lt;outDir&gt;/ownership-report.json，返回是否全部通过。
        /// </summary>
        public static bool RunAll(string outDir)
        {
            if (string.IsNullOrEmpty(outDir))
            {
                outDir = Path.Combine(Path.GetTempPath(), "po-ownership");
            }

            List<CaseResult> results = new List<CaseResult>();
            Log.Message("[PawnOwnership][OwnershipCases] 开始运行 " + TotalCases + " 个归属用例（outDir=" + outDir + "）");

            bool originalShowMarkers = true;
            try { originalShowMarkers = Mod_PawnOwnership.ShowMarkers; } catch { }

            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                Log.Warning("[PawnOwnership][OwnershipCases] 创建输出目录失败: " + Describe(ex) + "（仍尝试写报告）");
            }

            try
            {
                RunCase(results, "O01", "设置/读取物品归属", "SetOwner 后 GetOwner(Thing)/GetOwner(ThingID) 都返回同一归属人", O01);
                RunCase(results, "O02", "清除归属", "RemoveOwner(ThingID) 后 GetOwner 返回 null", O02);
                RunCase(results, "O03", "两件物品归属隔离", "两件物品各自归属互不影响，清除一件不影响另一件", O03);
                RunCase(results, "O04", "采矿标记写入格子归属", "AddDesignation(格子目标) 后 cellOwnership 写入当前玩家", O04);
                RunCase(results, "O05", "移除标记清除格子归属", "RemoveDesignation 后该格子归属被清除", O05);
                RunCase(results, "O06", "区域归属随创建/删除", "RegisterZone 写入区域归属，Delete 后清除", O06);
                RunCase(results, "O07", "归属物销毁后产出继承归属", "销毁有归属的建筑后，产出的 leavings 继承同一归属", O07);
                RunCase(results, "O08", "蓝图→框架归属传递", "蓝图被替换为 Frame 后，Frame 继承蓝图归属", O08);
                RunCase(results, "O09", "显示开关不影响归属数据", "ShowMarkers 关/开不改变已存归属数据", O09);
                RunCase(results, "O10", "他人归属物不可工作", "无归属时 HasJobOnThing=true；改为他人归属后 HasJobOnThing=false", O10);
                RunCase(results, "O11", "归属者可工作", "小人归属与目标归属一致时 HasJobOnThing=true", O11);
            }
            finally
            {
                // 恢复全局可见状态，避免影响驱动后续流程
                try { Mod_PawnOwnership.ShowMarkers = originalShowMarkers; } catch { }
                try { MapComponent_PawnOwnership.ProcessSyncQueue(); } catch { }
            }

            int passed = 0;
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].Pass) passed++;
            }
            int total = results.Count;
            int failed = total - passed;
            bool ok = total == TotalCases && failed == 0;

            bool wrote = false;
            try
            {
                string json = BuildReport(results, ok, total, passed, failed);
                string path = Path.Combine(outDir, "ownership-report.json");
                File.WriteAllText(path, json, new UTF8Encoding(false));
                wrote = true;
                Log.Message("[PawnOwnership][OwnershipCases] 报告已写入 " + path
                            + "（ok=" + ok + ", total=" + total + ", passed=" + passed + ", failed=" + failed + "）");
            }
            catch (Exception ex)
            {
                Log.Error("[PawnOwnership][OwnershipCases] 写报告失败: " + ex);
            }

            return ok && wrote;
        }

        // =====================================================================
        // 用例批量运行与结果记录
        // =====================================================================

        private sealed class CaseResult
        {
            public string Id;
            public string Name;
            public bool Pass;
            public string Expected;
            public string Actual;
            public string Detail;
        }

        private sealed class CaseCtx
        {
            public readonly List<string> Failures = new List<string>();
            public readonly List<string> Observations = new List<string>();

            public void Obs(string text)
            {
                if (string.IsNullOrEmpty(text) || Observations.Count >= 16) return;
                if (text.Length > 220) text = text.Substring(0, 220) + "...";
                Observations.Add(text);
            }

            public void Expect(string label, object expected, object actual)
            {
                string e = S(expected);
                string a = S(actual);
                Obs(label + "=" + a);
                if (!string.Equals(e, a, StringComparison.Ordinal))
                {
                    Failures.Add(label + " 期望[" + e + "] 实际[" + a + "]");
                }
            }

            public void ExpectBool(string label, bool expected, bool actual)
            {
                Obs(label + "=" + (actual ? "true" : "false"));
                if (expected != actual)
                {
                    Failures.Add(label + " 期望[" + (expected ? "true" : "false") + "] 实际[" + (actual ? "true" : "false") + "]");
                }
            }

            public void Fail(string reason)
            {
                if (!string.IsNullOrEmpty(reason)) Failures.Add(reason);
            }

            private static string S(object o)
            {
                return o == null ? "(null)" : o.ToString();
            }
        }

        private static void RunCase(List<CaseResult> results, string id, string name, string expected, Action<CaseCtx> body)
        {
            CaseCtx ctx = new CaseCtx();
            try
            {
                body(ctx);
            }
            catch (Exception ex)
            {
                ctx.Failures.Add("未捕获异常: " + Describe(ex));
                Log.Error("[PawnOwnership][OwnershipCases] " + id + " 抛异常: " + ex);
            }

            CaseResult r = new CaseResult
            {
                Id = id,
                Name = name,
                Expected = expected,
                Pass = ctx.Failures.Count == 0,
            };
            r.Actual = ctx.Observations.Count == 0 ? "(无观测)" : string.Join("; ", ctx.Observations.ToArray());
            r.Detail = ctx.Failures.Count == 0
                ? "全部断言通过（" + ctx.Observations.Count + " 项观测）"
                : string.Join(" | ", ctx.Failures.ToArray());

            results.Add(r);
            if (r.Pass)
            {
                Log.Message("[PawnOwnership][OwnershipCases] PASS " + id + " " + name);
            }
            else
            {
                Log.Warning("[PawnOwnership][OwnershipCases] FAIL " + id + " " + name + " :: " + r.Detail);
            }
        }

        // =====================================================================
        // O01 设置/读取物品归属
        // =====================================================================

        private static void O01(CaseCtx c)
        {
            Map map;
            MapComponent_PawnOwnership comp;
            if (!TryGetEnv(c, out map, out comp)) return;

            Thing item = null;
            try
            {
                IntVec3 cell;
                item = SpawnTestItem(map, ThingDefOf.Steel, out cell);
                if (item == null)
                {
                    c.Fail("找不到可生成测试物品的空地（Standable 且无任何 Thing）");
                    return;
                }
                c.Obs("item=" + item.ThingID + "@" + cell);

                comp.SetOwner(item, OwnerA);
                c.Expect("GetOwner(Thing)", OwnerA, comp.GetOwner(item));
                c.Expect("GetOwner(ThingID)", OwnerA, comp.GetOwner(item.ThingID));
            }
            finally
            {
                SafeRemoveOwner(comp, item);
                SafeDestroy(item);
            }
        }

        // =====================================================================
        // O02 清除归属
        // =====================================================================

        private static void O02(CaseCtx c)
        {
            Map map;
            MapComponent_PawnOwnership comp;
            if (!TryGetEnv(c, out map, out comp)) return;

            Thing item = null;
            try
            {
                IntVec3 cell;
                item = SpawnTestItem(map, ThingDefOf.Steel, out cell);
                if (item == null)
                {
                    c.Fail("找不到可生成测试物品的空地");
                    return;
                }

                comp.SetOwner(item, OwnerA);
                c.Expect("前置：设置后读取", OwnerA, comp.GetOwner(item));

                comp.RemoveOwner(item.ThingID);
                c.Expect("清除后 GetOwner(Thing)", null, comp.GetOwner(item));
                c.Expect("清除后 GetOwner(ThingID)", null, comp.GetOwner(item.ThingID));
            }
            finally
            {
                SafeRemoveOwner(comp, item);
                SafeDestroy(item);
            }
        }

        // =====================================================================
        // O03 两件物品归属隔离
        // =====================================================================

        private static void O03(CaseCtx c)
        {
            Map map;
            MapComponent_PawnOwnership comp;
            if (!TryGetEnv(c, out map, out comp)) return;

            Thing a = null;
            Thing b = null;
            try
            {
                IntVec3 cellA;
                a = SpawnTestItem(map, ThingDefOf.Steel, out cellA);
                if (a == null)
                {
                    c.Fail("找不到第一件测试物品的空地");
                    return;
                }

                IntVec3 cellB;
                b = SpawnTestItem(map, ThingDefOf.Plasteel, out cellB);
                if (b == null)
                {
                    c.Fail("找不到第二件测试物品的空地");
                    return;
                }

                if (a.ThingID == b.ThingID)
                {
                    c.Fail("测试物品 ThingID 相同，无法验证隔离");
                    return;
                }

                comp.SetOwner(a, OwnerA);
                comp.SetOwner(b, OwnerB);
                c.Expect("物品A归属", OwnerA, comp.GetOwner(a));
                c.Expect("物品B归属", OwnerB, comp.GetOwner(b));

                comp.RemoveOwner(a.ThingID);
                c.Expect("清除A后A", null, comp.GetOwner(a));
                c.Expect("清除A后B不受影响", OwnerB, comp.GetOwner(b));
            }
            finally
            {
                SafeRemoveOwner(comp, a);
                SafeRemoveOwner(comp, b);
                SafeDestroy(a);
                SafeDestroy(b);
            }
        }

        // =====================================================================
        // O04 采矿标记写入格子归属
        // =====================================================================

        private static void O04(CaseCtx c)
        {
            Map map;
            MapComponent_PawnOwnership comp;
            if (!TryGetEnv(c, out map, out comp)) return;

            Designation des = null;
            IntVec3 cell = IntVec3.Invalid;
            try
            {
                if (!ScanForCell(map, ScanRoot(map), 24, delegate(IntVec3 x)
                    {
                        if (GridsUtility.Fogged(x, map)) return false;
                        if (map.designationManager.AllDesignationsAt(x).Count != 0) return false;
                        return string.IsNullOrEmpty(comp.GetOwnerCell(x.x, x.z));
                    }, out cell))
                {
                    c.Fail("找不到没有标记、也没有残留归属的格子");
                    return;
                }

                des = new Designation(new LocalTargetInfo(cell), DesignationDefOf.Mine, null);
                map.designationManager.AddDesignation(des);
                c.ExpectBool("标记已注册", true, map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) != null);

                // AddDesignation 的 mod 补丁只入队；这里调用与 TickManager 补丁相同的真实入口落库。
                MapComponent_PawnOwnership.ProcessSyncQueue();

                string expected = MapComponent_PawnOwnership.GetCurrentPlayer();
                c.Obs("当前玩家=" + expected);
                c.Expect("格子归属", expected, comp.GetOwnerCell(cell.x, cell.z));
            }
            finally
            {
                if (des != null)
                {
                    try { map.designationManager.RemoveDesignation(des); }
                    catch (Exception ex) { Log.Warning("[PawnOwnership][OwnershipCases] O04 清理标记失败: " + Describe(ex)); }
                }
                if (cell.IsValid) SafeRemoveCellOwner(comp, cell);
            }
        }

        // =====================================================================
        // O05 移除标记清除格子归属
        // =====================================================================

        private static void O05(CaseCtx c)
        {
            Map map;
            MapComponent_PawnOwnership comp;
            if (!TryGetEnv(c, out map, out comp)) return;

            Designation des = null;
            IntVec3 cell = IntVec3.Invalid;
            try
            {
                if (!ScanForCell(map, ScanRoot(map), 24, delegate(IntVec3 x)
                    {
                        if (GridsUtility.Fogged(x, map)) return false;
                        if (map.designationManager.AllDesignationsAt(x).Count != 0) return false;
                        return string.IsNullOrEmpty(comp.GetOwnerCell(x.x, x.z));
                    }, out cell))
                {
                    c.Fail("找不到没有标记、也没有残留归属的格子");
                    return;
                }

                des = new Designation(new LocalTargetInfo(cell), DesignationDefOf.Mine, null);
                map.designationManager.AddDesignation(des);
                MapComponent_PawnOwnership.ProcessSyncQueue();
                c.Expect("前置：标记已写入格子归属", MapComponent_PawnOwnership.GetCurrentPlayer(), comp.GetOwnerCell(cell.x, cell.z));

                map.designationManager.RemoveDesignation(des);
                des = null; // 已移除，finally 不再重复移除
                c.Expect("移除标记后格子归属", null, comp.GetOwnerCell(cell.x, cell.z));
            }
            finally
            {
                if (des != null)
                {
                    try { map.designationManager.RemoveDesignation(des); }
                    catch (Exception ex) { Log.Warning("[PawnOwnership][OwnershipCases] O05 清理标记失败: " + Describe(ex)); }
                }
                if (cell.IsValid) SafeRemoveCellOwner(comp, cell);
            }
        }

        // =====================================================================
        // O06 区域归属随创建/删除
        // =====================================================================

        private static void O06(CaseCtx c)
        {
            Map map;
            MapComponent_PawnOwnership comp;
            if (!TryGetEnv(c, out map, out comp)) return;

            Zone_Stockpile zone = null;
            try
            {
                zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                MapComponent_PawnOwnership.ProcessSyncQueue();

                c.Obs("zoneId=" + zone.ID);
                string expected = MapComponent_PawnOwnership.GetCurrentPlayer();
                c.Expect("创建后区域归属", expected, comp.GetOwnerZone(zone.ID));

                zone.Delete();
                c.Expect("删除后区域归属", null, comp.GetOwnerZone(zone.ID));
            }
            finally
            {
                if (zone != null)
                {
                    try
                    {
                        if (map.zoneManager != null && map.zoneManager.AllZones.Contains(zone))
                        {
                            map.zoneManager.DeregisterZone(zone);
                        }
                    }
                    catch (Exception ex) { Log.Warning("[PawnOwnership][OwnershipCases] O06 清理区域失败: " + Describe(ex)); }
                    SafeRemoveZoneOwner(comp, zone);
                }
            }
        }

        // =====================================================================
        // O07 归属物销毁后产出继承归属
        // =====================================================================

        private static void O07(CaseCtx c)
        {
            Map map;
            MapComponent_PawnOwnership comp;
            if (!TryGetEnv(c, out map, out comp)) return;

            Thing wall = null;
            List<Thing> leavings = new List<Thing>();
            try
            {
                IntVec3 cell;
                if (!TryFindPlaceCell(map, out cell))
                {
                    c.Fail("找不到可放置测试墙的空地（CanPlaceBlueprintAt 全部拒绝）");
                    return;
                }

                wall = ThingMaker.MakeThing(WallDef, WoodDef);
                GenSpawn.Spawn(wall, cell, map, WipeMode.Vanish);
                if (wall == null || !wall.Spawned)
                {
                    c.Fail("测试墙生成失败");
                    return;
                }

                comp.SetOwner(wall, OwnerA);
                c.Expect("前置：墙归属", OwnerA, comp.GetOwner(wall));

                HashSet<Thing> before = new HashSet<Thing>(map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver));
                IntVec3 pos = wall.Position;

                // Wall 的 XML: leaveResourcesWhenKilled=false；Deconstruct 走 resourcesFractionWhenDeconstructed。
                wall.Destroy(DestroyMode.Deconstruct);

                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
                {
                    if (t == null || before.Contains(t)) continue;
                    if (t is Blueprint) continue;
                    leavings.Add(t);
                }

                c.Obs("墙位置=" + pos + ", leavings=" + leavings.Count);
                if (leavings.Count == 0)
                {
                    c.Fail("销毁归属物后没有找到任何 leavings，继承链无法验证（可能放置失败或产出被地形/资源比例吃掉）");
                    return;
                }

                for (int i = 0; i < leavings.Count; i++)
                {
                    Thing t = leavings[i];
                    c.Expect("leavings[" + i + ":" + t.def.defName + "]归属", OwnerA, comp.GetOwner(t));
                }
            }
            finally
            {
                SafeRemoveOwner(comp, wall);
                for (int i = 0; i < leavings.Count; i++)
                {
                    SafeRemoveOwner(comp, leavings[i]);
                    SafeDestroy(leavings[i]);
                }
                SafeDestroy(wall);
            }
        }

        // =====================================================================
        // O08 蓝图→框架归属传递
        // =====================================================================

        private static void O08(CaseCtx c)
        {
            Map map;
            MapComponent_PawnOwnership comp;
            if (!TryGetEnv(c, out map, out comp)) return;

            Blueprint_Build blueprint = null;
            Thing solid = null;
            IntVec3 cell = IntVec3.Invalid;
            try
            {
                Pawn pawn = FirstColonist(map);
                if (pawn == null)
                {
                    c.Fail("地图上没有可用殖民者（TryReplaceWithSolidThing 需要 Pawn 参数）");
                    return;
                }

                if (!TryFindPlaceCell(map, out cell))
                {
                    c.Fail("找不到可放置蓝图/框架的空地");
                    return;
                }

                // 清掉该格可能残留的暂存归属，避免误判继承来源
                MapComponent_PawnOwnership.GetAndClearPendingOwnership(cell);

                blueprint = GenConstruct.PlaceBlueprintForBuild(
                    WallDef, cell, map, Rot4.North, Faction.OfPlayer, WoodDef, null, null, false);
                if (blueprint == null)
                {
                    c.Fail("GenConstruct.PlaceBlueprintForBuild 返回 null");
                    return;
                }
                if (!blueprint.Spawned)
                {
                    c.Fail("蓝图未成功生成（Spawned=false）");
                    return;
                }
                c.Obs("blueprint=" + blueprint.ThingID + "@" + blueprint.Position);

                comp.SetOwner(blueprint, OwnerA);
                c.Expect("前置：蓝图归属", OwnerA, comp.GetOwner(blueprint));

                bool styleOverridden = false;
                bool replaced = blueprint.TryReplaceWithSolidThing(pawn, out solid, out styleOverridden);
                c.ExpectBool("TryReplaceWithSolidThing 返回", true, replaced);
                c.ExpectBool("蓝图已 Despawn", false, blueprint.Spawned);
                c.ExpectBool("产出物是 Frame", true, solid is Frame);
                c.Expect("Frame 归属", OwnerA, comp.GetOwner(solid));
            }
            finally
            {
                SafeRemoveOwner(comp, blueprint);
                SafeRemoveOwner(comp, solid);
                SafeDestroy(solid);
                SafeDestroy(blueprint);
                if (cell.IsValid)
                {
                    try { MapComponent_PawnOwnership.GetAndClearPendingOwnership(cell); } catch { }
                }
            }
        }

        // =====================================================================
        // O09 显示开关不影响归属数据
        // =====================================================================

        private static void O09(CaseCtx c)
        {
            Map map;
            MapComponent_PawnOwnership comp;
            if (!TryGetEnv(c, out map, out comp)) return;

            Thing item = null;
            bool originalShow = true;
            try
            {
                originalShow = Mod_PawnOwnership.ShowMarkers;

                IntVec3 cell;
                item = SpawnTestItem(map, ThingDefOf.Steel, out cell);
                if (item == null)
                {
                    c.Fail("找不到可生成测试物品的空地");
                    return;
                }
                comp.SetOwner(item, OwnerA);
                c.Expect("前置：物品归属", OwnerA, comp.GetOwner(item));

                Mod_PawnOwnership.ShowMarkers = false;
                c.ExpectBool("关闭后 ShowMarkers", false, Mod_PawnOwnership.ShowMarkers);
                c.Expect("关闭后 GetOwner(Thing)", OwnerA, comp.GetOwner(item));
                c.Expect("关闭后 GetOwner(ThingID)", OwnerA, comp.GetOwner(item.ThingID));

                Mod_PawnOwnership.ShowMarkers = true;
                c.ExpectBool("打开后 ShowMarkers", true, Mod_PawnOwnership.ShowMarkers);
                c.Expect("打开后 GetOwner(Thing)", OwnerA, comp.GetOwner(item));
            }
            finally
            {
                try { Mod_PawnOwnership.ShowMarkers = originalShow; }
                catch (Exception ex) { Log.Warning("[PawnOwnership][OwnershipCases] O09 恢复显示开关失败: " + Describe(ex)); }
                SafeRemoveOwner(comp, item);
                SafeDestroy(item);
            }
        }

        // =====================================================================
        // O10 他人归属物不可工作（HasJobOnThing=false）
        // =====================================================================

        private static void O10(CaseCtx c)
        {
            // 先让墙"无归属"构造出天然可工作的前置，再把归属改成他人，观察同一入口翻转为 false。
            DeconstructWorkSetup setup = SetupDeconstructWork(c, null, OwnerA);
            if (setup == null) return;

            try
            {
                string patchError = CheckHasJobPrefix(typeof(WorkGiver_Deconstruct));
                if (patchError != null)
                {
                    c.Fail("动态前缀补丁未生效（无法验证归属门禁）: " + patchError);
                    return;
                }
                c.Obs("patch(HasJobOnThing)=true");

                WorkGiver_Deconstruct wg = GetDeconstructWorkGiver();
                if (wg == null)
                {
                    c.Fail("找不到 Deconstruct 的 WorkGiver 实例（DefDatabase 查不到）");
                    return;
                }

                bool natural = wg.HasJobOnThing(setup.Pawn, setup.Wall, false);
                c.ExpectBool("前置：无归属时 HasJobOnThing", true, natural);

                setup.Comp.SetOwner(setup.Wall, OwnerB);
                c.Expect("墙归属(他人)", OwnerB, setup.Comp.GetOwner(setup.Wall));
                c.Expect("小人归属", OwnerA, PawnOwnershipExtension.GetOwner(setup.Pawn));

                bool blocked = wg.HasJobOnThing(setup.Pawn, setup.Wall, false);
                c.ExpectBool("HasJobOnThing(他人归属物)", false, blocked);
            }
            finally
            {
                setup.Cleanup();
            }
        }

        // =====================================================================
        // O11 归属者可工作（HasJobOnThing=true）
        // =====================================================================

        private static void O11(CaseCtx c)
        {
            DeconstructWorkSetup setup = SetupDeconstructWork(c, OwnerA, OwnerA);
            if (setup == null) return;

            try
            {
                string patchError = CheckHasJobPrefix(typeof(WorkGiver_Deconstruct));
                if (patchError != null)
                {
                    c.Fail("动态前缀补丁未生效（无法验证归属门禁）: " + patchError);
                    return;
                }

                WorkGiver_Deconstruct wg = GetDeconstructWorkGiver();
                if (wg == null)
                {
                    c.Fail("找不到 Deconstruct 的 WorkGiver 实例（DefDatabase 查不到）");
                    return;
                }

                c.Expect("墙归属", OwnerA, setup.Comp.GetOwner(setup.Wall));
                c.Expect("小人归属", OwnerA, PawnOwnershipExtension.GetOwner(setup.Pawn));

                bool allowed = wg.HasJobOnThing(setup.Pawn, setup.Wall, false);
                c.ExpectBool("HasJobOnThing(归属者可工作)", true, allowed);
            }
            finally
            {
                setup.Cleanup();
            }
        }

        // =====================================================================
        // O10/O11 共用场景
        // =====================================================================

        private sealed class DeconstructWorkSetup
        {
            public Map Map;
            public MapComponent_PawnOwnership Comp;
            public Pawn Pawn;
            public Thing Wall;
            public Designation Designation;
            public string PawnOriginalOwner;
            public bool PawnOwnerCaptured;

            public void Cleanup()
            {
                // 顺序：先撤标记（会顺带清 thing 归属）→ 清归属词典 → 恢复小人归属 → 销毁实体
                try
                {
                    if (Designation != null && Map != null && Map.designationManager != null)
                    {
                        Map.designationManager.RemoveDesignation(Designation);
                    }
                }
                catch (Exception ex) { Log.Warning("[PawnOwnership][OwnershipCases] 清理 Deconstruct 标记失败: " + Describe(ex)); }
                Designation = null;

                SafeRemoveOwner(Comp, Wall);

                try
                {
                    if (PawnOwnerCaptured && Pawn != null)
                    {
                        PawnOwnershipExtension.SetOwner(Pawn, PawnOriginalOwner);
                    }
                }
                catch (Exception ex) { Log.Warning("[PawnOwnership][OwnershipCases] 恢复小人归属失败: " + Describe(ex)); }

                SafeDestroy(Wall);
                Wall = null;
            }
        }

        /// <summary>
        /// 造一个"墙 + Deconstruct 标记 + 小人归属"的真实场景。
        /// wallOwner 传 null 表示先保持墙无归属；pawnOwner 非空则写入小人归属。
        /// </summary>
        private static DeconstructWorkSetup SetupDeconstructWork(CaseCtx c, string wallOwner, string pawnOwner)
        {
            Map map;
            MapComponent_PawnOwnership comp;
            if (!TryGetEnv(c, out map, out comp)) return null;

            DeconstructWorkSetup setup = new DeconstructWorkSetup();
            setup.Map = map;
            setup.Comp = comp;

            try
            {
                setup.Pawn = FirstColonist(map);
                if (setup.Pawn == null)
                {
                    c.Fail("地图上没有可用的自由殖民者，无法构造 HasJobOnThing 场景");
                    return null;
                }

                IntVec3 cell;
                if (!TryFindPlaceCell(map, out cell))
                {
                    c.Fail("找不到可放置测试墙的空地");
                    return null;
                }

                setup.Wall = ThingMaker.MakeThing(WallDef, WoodDef);
                GenSpawn.Spawn(setup.Wall, cell, map, WipeMode.Vanish);
                if (setup.Wall == null || !setup.Wall.Spawned)
                {
                    c.Fail("测试墙生成失败");
                    setup.Cleanup();
                    return null;
                }
                setup.Wall.SetFaction(Faction.OfPlayer, null);

                // WorkGiver_Deconstruct.HasJobOnThing 的成立条件之一：目标身上有 Deconstruct 标记。
                // 用真实 API 添加，mod 的 AddDesignation 补丁会顺带给墙写当前玩家归属，这里随后覆盖。
                setup.Designation = new Designation(new LocalTargetInfo(setup.Wall), DesignationDefOf.Deconstruct, null);
                map.designationManager.AddDesignation(setup.Designation);
                MapComponent_PawnOwnership.ProcessSyncQueue();

                setup.PawnOriginalOwner = PawnOwnershipExtension.GetOwner(setup.Pawn);
                setup.PawnOwnerCaptured = true;
                if (!string.IsNullOrEmpty(pawnOwner))
                {
                    PawnOwnershipExtension.SetOwner(setup.Pawn, pawnOwner);
                }

                if (string.IsNullOrEmpty(wallOwner))
                {
                    comp.RemoveOwner(setup.Wall.ThingID);
                }
                else
                {
                    comp.SetOwner(setup.Wall, wallOwner);
                }

                c.Obs("wall=" + setup.Wall.ThingID + ", pawn=" + setup.Pawn.LabelShort + ", cell=" + cell);
                return setup;
            }
            catch (Exception ex)
            {
                c.Fail("构造 Deconstruct 工作场景异常: " + Describe(ex));
                setup.Cleanup();
                return null;
            }
        }

        /// <summary>运行时确认 HasJobOnThing 上真的挂着本 mod 的前缀补丁（RULES 铁律 2「补丁真的生效」要运行时证据）。</summary>
        private static string CheckHasJobPrefix(Type workGiverType)
        {
            try
            {
                MethodInfo mi = workGiverType.GetMethod(
                    "HasJobOnThing",
                    new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
                if (mi == null) return "找不到 " + workGiverType.Name + ".HasJobOnThing(Pawn,Thing,bool)";

                HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(mi);
                if (info == null) return "Harmony.GetPatchInfo 返回 null（该方法没有任何补丁）";

                for (int i = 0; i < info.Prefixes.Count; i++)
                {
                    HarmonyLib.Patch p = info.Prefixes[i];
                    if (p != null && p.owner == ModHarmonyId) return null;
                }
                return "前缀列表中没有 owner=" + ModHarmonyId + "（实际 owners=" + string.Join(",", info.Owners) + "）";
            }
            catch (Exception ex)
            {
                return Describe(ex);
            }
        }

        private static WorkGiver_Deconstruct GetDeconstructWorkGiver()
        {
            try
            {
                WorkGiverDef def = DefDatabase<WorkGiverDef>.GetNamedSilentFail("Deconstruct");
                if (def == null)
                {
                    List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (all[i] != null && all[i].giverClass == typeof(WorkGiver_Deconstruct))
                        {
                            def = all[i];
                            break;
                        }
                    }
                }
                if (def == null) return null;
                return def.Worker as WorkGiver_Deconstruct;
            }
            catch (Exception ex)
            {
                Log.Warning("[PawnOwnership][OwnershipCases] 获取 Deconstruct WorkGiver 失败: " + Describe(ex));
                return null;
            }
        }

        // =====================================================================
        // 实体生成 / 清理 / 格子查找
        // =====================================================================

        private static ThingDef _wallDef;
        private static ThingDef _woodDef;

        private static ThingDef WallDef
        {
            get
            {
                if (_wallDef == null) _wallDef = DefNamed("Wall");
                return _wallDef;
            }
        }

        private static ThingDef WoodDef
        {
            get
            {
                if (_woodDef == null) _woodDef = DefNamed("WoodLog");
                return _woodDef;
            }
        }

        private static ThingDef DefNamed(string name)
        {
            ThingDef def = ThingDef.Named(name);
            if (def == null) throw new InvalidOperationException("找不到 ThingDef: " + name);
            return def;
        }

        private static bool TryGetEnv(CaseCtx c, out Map map, out MapComponent_PawnOwnership comp)
        {
            map = null;
            comp = null;
            try
            {
                if (Current.ProgramState != ProgramState.Playing)
                {
                    c.Fail("游戏状态不是 Playing（当前 " + Current.ProgramState + "），运行时用例无法执行");
                    return false;
                }
                map = Find.CurrentMap;
                if (map == null)
                {
                    c.Fail("Find.CurrentMap 为 null");
                    return false;
                }
                comp = map.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null)
                {
                    c.Fail("MapComponent_PawnOwnership 为 null（mod 未加载或组件缺失）");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                c.Fail("获取运行环境异常: " + Describe(ex));
                return false;
            }
        }

        private static Pawn FirstColonist(Map map)
        {
            try
            {
                if (map == null || map.mapPawns == null) return null;
                List<Pawn> colonists = map.mapPawns.FreeColonists;
                if (colonists == null) return null;
                for (int i = 0; i < colonists.Count; i++)
                {
                    Pawn p = colonists[i];
                    if (p != null && p.Spawned && !p.Downed) return p;
                }
                for (int i = 0; i < colonists.Count; i++)
                {
                    Pawn p = colonists[i];
                    if (p != null && p.Spawned) return p;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[PawnOwnership][OwnershipCases] 查找殖民者失败: " + Describe(ex));
            }
            return null;
        }

        private static Thing SpawnTestItem(Map map, ThingDef def, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            if (def == null) return null;

            Predicate<IntVec3> free = delegate(IntVec3 x) { return IsFreeStandableCell(map, x); };
            if (!ScanForCell(map, ScanRoot(map), 24, free, out cell))
            {
                if (!ScanForCell(map, map.Center, Math.Max(map.Size.x, map.Size.z), free, out cell))
                {
                    cell = IntVec3.Invalid;
                    return null;
                }
            }

            Thing thing = ThingMaker.MakeThing(def, null);
            GenSpawn.Spawn(thing, cell, map, WipeMode.Vanish);
            if (thing == null || !thing.Spawned)
            {
                SafeDestroy(thing);
                cell = IntVec3.Invalid;
                return null;
            }
            return thing;
        }

        private static bool IsFreeStandableCell(Map map, IntVec3 c)
        {
            if (GridsUtility.Fogged(c, map)) return false;
            if (!GenGrid.Standable(c, map)) return false;
            return GridsUtility.GetThingList(c, map).Count == 0;
        }

        /// <summary>找一个可以放 Wall 蓝图的格子（用真实 API GenConstruct.CanPlaceBlueprintAt 判定）。</summary>
        private static bool TryFindPlaceCell(Map map, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            try
            {
                Predicate<IntVec3> ok = delegate(IntVec3 x) { return CanPlaceWallAt(map, x); };
                if (ScanForCell(map, ScanRoot(map), 60, ok, out cell)) return true;
                return ScanForCell(map, map.Center, Math.Max(map.Size.x, map.Size.z), ok, out cell);
            }
            catch (Exception ex)
            {
                Log.Warning("[PawnOwnership][OwnershipCases] 查找放置格失败: " + Describe(ex));
                return false;
            }
        }

        private static bool CanPlaceWallAt(Map map, IntVec3 c)
        {
            try
            {
                if (GridsUtility.Fogged(c, map)) return false;
                if (GridsUtility.GetThingList(c, map).Count != 0) return false;
                if (map.zoneManager != null && map.zoneManager.ZoneAt(c) != null) return false;

                // O10/O11 要区分"仅 thing 归属"导致的拦截，格子/区域上不能有残留归属（PatchHelpers 也会查这两层）。
                MapComponent_PawnOwnership comp = map.GetComponent<MapComponent_PawnOwnership>();
                if (comp != null && !string.IsNullOrEmpty(comp.GetOwnerCell(c.x, c.z))) return false;

                if (map.designationManager != null)
                {
                    if (map.designationManager.DesignationAt(c, DesignationDefOf.Mine) != null) return false;
                    if (map.designationManager.DesignationAt(c, DesignationDefOf.Deconstruct) != null) return false;
                }
                AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt(
                    WallDef, c, Rot4.North, map, false, null, null, WoodDef, false, false, false);
                return report.Accepted;
            }
            catch
            {
                return false;
            }
        }

        private static IntVec3 ScanRoot(Map map)
        {
            Pawn p = FirstColonist(map);
            if (p != null && p.Spawned) return p.Position;
            return map.Center;
        }

        /// <summary>从 root 向外环形扫描，返回第一个满足 valid 的格子。</summary>
        private static bool ScanForCell(Map map, IntVec3 root, int maxRadius, Predicate<IntVec3> valid, out IntVec3 found)
        {
            found = IntVec3.Invalid;
            if (map == null || valid == null) return false;
            if (maxRadius < 0) maxRadius = 0;

            for (int r = 0; r <= maxRadius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dz) != r) continue; // 只扫当前环
                        IntVec3 c = new IntVec3(root.x + dx, 0, root.z + dz);
                        if (!GenGrid.InBounds(c, map)) continue;
                        bool ok;
                        try { ok = valid(c); }
                        catch { ok = false; }
                        if (ok)
                        {
                            found = c;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // =====================================================================
        // 清理
        // =====================================================================

        private static void SafeRemoveOwner(MapComponent_PawnOwnership comp, Thing thing)
        {
            if (comp == null || thing == null) return;
            try { comp.RemoveOwner(thing.ThingID); }
            catch (Exception ex) { Log.Warning("[PawnOwnership][OwnershipCases] RemoveOwner 失败: " + Describe(ex)); }
        }

        private static void SafeRemoveCellOwner(MapComponent_PawnOwnership comp, IntVec3 cell)
        {
            if (comp == null || !cell.IsValid) return;
            try { comp.RemoveOwnerCell(cell.x, cell.z); }
            catch (Exception ex) { Log.Warning("[PawnOwnership][OwnershipCases] RemoveOwnerCell 失败: " + Describe(ex)); }
        }

        private static void SafeRemoveZoneOwner(MapComponent_PawnOwnership comp, Zone zone)
        {
            if (comp == null || zone == null) return;
            try { comp.RemoveOwnerZone(zone.ID); }
            catch (Exception ex) { Log.Warning("[PawnOwnership][OwnershipCases] RemoveOwnerZone 失败: " + Describe(ex)); }
        }

        private static void SafeDestroy(Thing thing)
        {
            if (thing == null) return;
            try
            {
                if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
            }
            catch (Exception ex)
            {
                Log.Warning("[PawnOwnership][OwnershipCases] 清理 Thing 失败: " + Describe(ex));
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex == null) return "(null)";
            return ex.GetType().Name + ": " + ex.Message;
        }

        // =====================================================================
        // 报告 JSON（自写转义，不引依赖）
        // =====================================================================

        private static string BuildReport(List<CaseResult> results, bool ok, int total, int passed, int failed)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"ok\": ").Append(ok ? "true" : "false").Append(",\n");
            sb.Append("  \"total\": ").Append(total).Append(",\n");
            sb.Append("  \"passed\": ").Append(passed).Append(",\n");
            sb.Append("  \"failed\": ").Append(failed).Append(",\n");
            sb.Append("  \"cases\": [\n");
            for (int i = 0; i < results.Count; i++)
            {
                CaseResult r = results[i];
                sb.Append("    {");
                sb.Append("\"id\": ").Append(Q(r.Id)).Append(", ");
                sb.Append("\"name\": ").Append(Q(r.Name)).Append(", ");
                sb.Append("\"pass\": ").Append(r.Pass ? "true" : "false").Append(", ");
                sb.Append("\"expected\": ").Append(Q(r.Expected)).Append(", ");
                sb.Append("\"actual\": ").Append(Q(r.Actual)).Append(", ");
                sb.Append("\"detail\": ").Append(Q(r.Detail));
                sb.Append("}");
                if (i < results.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Q(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
