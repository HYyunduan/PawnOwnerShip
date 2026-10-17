using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using Verse;
using RimWorld;

namespace PawnOwnership.Testing
{
    /// <summary>
    /// L2 场景驱动：进游戏 → 造归属 → 截图（开 / 关 / 恢复）→ 写报告。
    ///
    /// 设计约束（见 docs/DECISIONS.md 与已确认的 S3b 设计）：
    ///   - 不新增 Harmony patch：用隐藏 GameObject 的 Update() 拿每帧回调；
    ///   - 不新增程序集引用：截图走反射调用 UnityEngine.ScreenCapture；
    ///   - 不写存档、不调 ModSettings.WriteSettings：开关只在内存里改，结束时恢复。
    ///
    /// 两条开局路径：
    ///   scenario=quickstart → Verse.QuickStarter.CheckQuickStart()（进程内临时开开发者模式）
    ///   scenario=loadsave   → GameDataSaveLoader.LoadGame(saveName)
    /// </summary>
    public class TestDriver : MonoBehaviour
    {
        private enum Phase
        {
            WaitEntry,
            StartGame,
            WaitMap,
            Prepare,
            CaptureOn,
            TurnOff,
            CaptureOff,
            TurnOn,
            CaptureBack,
            Settle,
            Finished,
        }

        private string scenario;
        private string saveName;
        private string outDir;

        private Phase phase = Phase.WaitEntry;
        private int phaseFrame;
        private int frame;
        private bool finished;

        private bool originalShowMarkers;
        private bool originalDevMode;
        private int ownedCount;
        private string currentMapInfo;

        // 绘制计数采样：用「实际绘制了多少标记」代替看像素。
        private int markersOnDrawn;
        private int markersOffDrawn;
        private int markersBackDrawn;

        private readonly List<string> notes = new List<string>();

        private int idleFrames;

        private const int EntrySettleFrames = 180;
        private const int MapTimeoutFrames = 3600; // 约 60 秒
        private const int ShotSettleFrames = 40;   // 截图为异步落盘，间隔要给足
        private const int FinishSettleFrames = 150; // 最后一张截图后等落盘再写报告
        private const int ToggleSettleFrames = 3;

        public static void Start(string scenario, string saveName, string outDir)
        {
            try
            {
                if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(Path.GetTempPath(), "po-scenario");
                Directory.CreateDirectory(outDir);

                var go = new GameObject("PawnOwnershipTestDriver");
                UnityEngine.Object.DontDestroyOnLoad(go);
                var driver = go.AddComponent<TestDriver>();
                driver.scenario = scenario;
                driver.saveName = saveName;
                driver.outDir = outDir;
                driver.originalShowMarkers = Mod_PawnOwnership.ShowMarkers;

                Log.Message("[PawnOwnership][TestDriver] 已启动：scenario=" + scenario
                            + ", save=" + (saveName ?? "(none)") + ", out=" + outDir);
            }
            catch (Exception ex)
            {
                Log.Error("[PawnOwnership][TestDriver] 启动失败: " + ex);
            }
        }

        private void Update()
        {
            if (finished) return;
            frame++;

            try
            {
                Step();
            }
            catch (Exception ex)
            {
                Fail("驱动异常: " + ex);
            }
        }

        private void Step()
        {
            switch (phase)
            {
                case Phase.WaitEntry:
                    // 等主菜单进入空闲状态。注意 ShouldWaitForEvent 只作为观察项记录：
                    // 实测它可能在长时间内保持 true，若用它做门槛会永远卡住。
                    if (Current.ProgramState != ProgramState.Entry || !GenScene.InEntryScene)
                    {
                        idleFrames = 0;
                        if (frame % 60 == 0)
                        {
                            Log.Message("[PawnOwnership][TestDriver] 等待入口: state=" + Current.ProgramState
                                        + " inEntry=" + GenScene.InEntryScene);
                        }
                        return;
                    }

                    idleFrames++;
                    if (idleFrames % 60 == 0)
                    {
                        Log.Message("[PawnOwnership][TestDriver] 入口空闲 " + idleFrames
                                    + " 帧, shouldWait=" + LongEventHandler.ShouldWaitForEvent);
                    }
                    if (idleFrames < EntrySettleFrames) return;

                    notes.Add("主菜单空闲 " + idleFrames + " 帧后开始开局");
                    BeginGame();
                    Enter(Phase.StartGame);
                    break;

                case Phase.StartGame:
                    if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
                    {
                        Enter(Phase.Prepare);
                    }
                    else if (frame - phaseFrame > MapTimeoutFrames)
                    {
                        Fail("等待地图超时（" + MapTimeoutFrames + " 帧），scenario=" + scenario
                             + "。可能卡在对话框或被 mod 不匹配阻断。");
                    }
                    break;

                case Phase.Prepare:
                    if (frame - phaseFrame < 30) return;
                    PrepareScene();
                    Enter(Phase.CaptureOn);
                    break;

                case Phase.CaptureOn:
                    if (frame - phaseFrame < ShotSettleFrames) return;
                    markersOnDrawn = MapComponent_PawnOwnership.MarkersDrawnLastFrame;
                    Capture("01-markers-on.png");
                    Enter(Phase.TurnOff);
                    break;

                case Phase.TurnOff:
                    if (frame - phaseFrame < ToggleSettleFrames) return;
                    SetMarkers(false);
                    notes.Add("markers 已关闭");
                    Enter(Phase.CaptureOff);
                    break;

                case Phase.CaptureOff:
                    if (frame - phaseFrame < ShotSettleFrames) return;
                    markersOffDrawn = MapComponent_PawnOwnership.MarkersDrawnLastFrame;
                    Capture("02-markers-off.png");
                    Enter(Phase.TurnOn);
                    break;

                case Phase.TurnOn:
                    if (frame - phaseFrame < ToggleSettleFrames) return;
                    SetMarkers(true);
                    notes.Add("markers 已恢复");
                    Enter(Phase.CaptureBack);
                    break;

                case Phase.CaptureBack:
                    if (frame - phaseFrame < ShotSettleFrames) return;
                    markersBackDrawn = MapComponent_PawnOwnership.MarkersDrawnLastFrame;
                    Capture("03-markers-back.png");
                    Enter(Phase.Settle);
                    break;

                case Phase.Settle:
                    // 给最后一张截图留出落盘时间，否则进程被脚本杀掉时文件还没写完。
                    if (frame - phaseFrame < FinishSettleFrames) return;
                    Finish(true, null);
                    break;
            }
        }

        private void Enter(Phase next)
        {
            phase = next;
            phaseFrame = frame;
        }

        private void BeginGame()
        {
            if (scenario == "loadsave")
            {
                notes.Add("加载存档: " + saveName);
                Log.Message("[PawnOwnership][TestDriver] 加载存档: " + saveName);
                GameDataSaveLoader.LoadGame(saveName);
                return;
            }

            originalDevMode = Prefs.DevMode;
            Prefs.DevMode = true;
            notes.Add("已临时开启 Prefs.DevMode");

            if (scenario == "quicktest")
            {
                // 调用前后各写一个磁盘标记：若进程在此崩溃，标记文件可作证据。
                WriteMarker("before-quicktest.txt",
                    "state=" + Current.ProgramState + "; map=" + (Find.CurrentMap != null));

                Log.Message("[PawnOwnership][TestDriver] 调用 Root_Play.SetupForQuickTestPlay()");
                try
                {
                    Root_Play.SetupForQuickTestPlay();
                    notes.Add("SetupForQuickTestPlay 正常返回");
                    WriteMarker("after-quicktest.txt", "returned normally");
                }
                catch (Exception ex)
                {
                    string detail = ex.GetType().Name + ": " + ex.Message;
                    notes.Add("SetupForQuickTestPlay 异常: " + detail);
                    Log.Error("[PawnOwnership][TestDriver] SetupForQuickTestPlay 异常: " + ex);
                    WriteMarker("quicktest-exception.txt", detail);
                }
            }
            else
            {
                Log.Message("[PawnOwnership][TestDriver] 调用 QuickStarter.CheckQuickStart()");
                QuickStarter.CheckQuickStart();
                notes.Add("已调用 CheckQuickStart()");
            }
        }

        private void WriteMarker(string fileName, string content)
        {
            try
            {
                File.WriteAllText(Path.Combine(outDir, fileName), content ?? "", new UTF8Encoding(false));
            }
            catch { }
        }

        private void PrepareScene()
        {
            Map map = Find.CurrentMap;
            currentMapInfo = map != null
                ? (map.info != null ? map.info.parent?.LabelCap.ToString() : "map " + map.uniqueID)
                : "(null)";

            var comp = map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null)
            {
                notes.Add("MapComponent_PawnOwnership 为 null");
                return;
            }

            int n = 0;
            IntVec3 focus = IntVec3.Invalid;

            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (t == null || t.def == null) continue;
                if (t.def.category != ThingCategory.Item) continue;

                comp.SetOwner(t, "SelfTest");
                if (n == 0) focus = t.Position;
                n++;
                if (n >= 5) break;
            }

            ownedCount = n;
            notes.Add("已为 " + n + " 个物品设置归属 SelfTest");

            if (focus.IsValid)
            {
                Find.CameraDriver.JumpToCurrentMapLoc(focus);
                notes.Add("摄像机已跳到 " + focus);
            }
            else
            {
                notes.Add("没找到可设置归属的物品");
            }
        }

        private void SetMarkers(bool on)
        {
            Mod_PawnOwnership.ShowMarkers = on;
        }

        private void Capture(string fileName)
        {
            string path = Path.Combine(outDir, fileName);
            try
            {
                Type t = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        t = asm.GetType("UnityEngine.ScreenCapture", false);
                        if (t != null) break;
                    }
                }

                MethodInfo m = t?.GetMethod("CaptureScreenshot", new[] { typeof(string) });
                if (m == null)
                {
                    notes.Add("截图失败：找不到 ScreenCapture.CaptureScreenshot");
                    Log.Warning("[PawnOwnership][TestDriver] 找不到 ScreenCapture.CaptureScreenshot");
                    return;
                }

                m.Invoke(null, new object[] { path });
                notes.Add("截图: " + fileName);
                Log.Message("[PawnOwnership][TestDriver] 截图 " + path);
            }
            catch (Exception ex)
            {
                notes.Add("截图异常: " + ex.Message);
                Log.Error("[PawnOwnership][TestDriver] 截图失败: " + ex);
            }
        }

        private void Fail(string reason)
        {
            notes.Add("失败: " + reason);
            Log.Error("[PawnOwnership][TestDriver] " + reason);
            Finish(false, reason);
        }

        private void Finish(bool ok, string error)
        {
            if (finished) return;
            finished = true;
            phase = Phase.Finished;

            // 恢复现场
            try { Mod_PawnOwnership.ShowMarkers = originalShowMarkers; } catch { }
            try { Prefs.DevMode = originalDevMode; } catch { }

            // 记录截图是否真的落盘（Unity 的截图是异步写文件，调用成功 ≠ 文件存在）
            foreach (string f in new[] { "01-markers-on.png", "02-markers-off.png", "03-markers-back.png" })
            {
                try { notes.Add("文件 " + f + " 存在=" + File.Exists(Path.Combine(outDir, f))); }
                catch { }
            }

            try
            {
                string json = BuildReport(ok, error);
                string path = Path.Combine(outDir, "scenario-report.json");
                File.WriteAllText(path, json, new UTF8Encoding(false));
                Log.Message("[PawnOwnership][TestDriver] 报告已写入 " + path + "（ok=" + ok + "）");
            }
            catch (Exception ex)
            {
                Log.Error("[PawnOwnership][TestDriver] 写报告失败: " + ex);
            }
        }

        private string BuildReport(bool ok, string error)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"scenario\": ").Append(Q(scenario)).Append(",\n");
            sb.Append("  \"save\": ").Append(Q(saveName)).Append(",\n");
            sb.Append("  \"ok\": ").Append(ok ? "true" : "false").Append(",\n");
            sb.Append("  \"error\": ").Append(Q(error)).Append(",\n");
            sb.Append("  \"map\": ").Append(Q(currentMapInfo)).Append(",\n");
            sb.Append("  \"ownedThings\": ").Append(ownedCount).Append(",\n");
            sb.Append("  \"markersOriginal\": ").Append(originalShowMarkers ? "true" : "false").Append(",\n");
            sb.Append("  \"drawCalls\": ").Append(MapComponent_PawnOwnership.DrawCalls).Append(",\n");
            sb.Append("  \"markersDrawnOn\": ").Append(markersOnDrawn).Append(",\n");
            sb.Append("  \"markersDrawnOff\": ").Append(markersOffDrawn).Append(",\n");
            sb.Append("  \"markersDrawnBack\": ").Append(markersBackDrawn).Append(",\n");
            sb.Append("  \"framesUsed\": ").Append(frame).Append(",\n");
            sb.Append("  \"outDir\": ").Append(Q(outDir)).Append(",\n");
            sb.Append("  \"notes\": [");
            for (int i = 0; i < notes.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Q(notes[i]));
            }
            sb.Append("]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Q(string s)
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
