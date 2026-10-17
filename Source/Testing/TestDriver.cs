using System;
using System.Collections;
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
    /// L2 场景驱动：
    ///   - 标记场景（quickstart / quicktest / loadsave）：进游戏 → 造归属 → 截图（开 / 关 / 恢复）→ 写报告；
    ///   - 归属场景（scenario=ownership）：进游戏 → 跑 <see cref="RunOwnershipCases"/> → 写报告，不截图。
    ///
    /// 设计约束（见 docs/DECISIONS.md 与已确认的 S3b 设计）：
    ///   - 不新增 Harmony patch：用隐藏 GameObject 的 Update() 拿每帧回调；
    ///   - 不新增程序集引用：截图走反射调用 UnityEngine.ScreenCapture；
    ///   - 不写存档、不调 ModSettings.WriteSettings：开关只在内存里改，结束时恢复。
    ///
    /// 开局路径：
    ///   scenario=quickstart / quicktest / ownership → Verse.QuickStarter.CheckQuickStart()
    ///                                                 （quicktest 走 Root_Play.SetupForQuickTestPlay()）
    ///   scenario=loadsave                           → GameDataSaveLoader.LoadGame(saveName)
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
        private float beginGameAt;

        private const int EntrySettleFrames = 180;
        // 存档加载期间帧率极低，按帧数计时会严重低估真实耗时（曾因此在 3600 帧时误判超时）。
        private const float MapTimeoutSeconds = 300f;
        private const int ShotSettleFrames = 40;   // 截图为异步落盘，间隔要给足
        private const int FinishSettleFrames = 150; // 最后一张截图后等落盘再写报告
        private const int ToggleSettleFrames = 3;

        /// <summary>归属用例场景名。该场景只跑用例，不截图、不校验标记绘制。</summary>
        private const string OwnershipScenario = "ownership";

        /// <summary>归属用例报告文件名（由 OwnershipCases.RunAll 写入 outDir）。</summary>
        private const string OwnershipReportFile = "ownership-report.json";

        /// <summary>
        /// 是否为 ownership 场景。标记专用断言（绘制数 / 截图）只对非 ownership 场景成立。
        /// </summary>
        private bool IsOwnershipScenario()
        {
            return string.Equals(scenario, OwnershipScenario, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>标记可见性探针场景名：用游戏内抓帧做 A/B 数值判定，不依赖人眼看图。</summary>
        private const string MarkerProbeScenario = "markerprobe";

        private bool IsMarkerProbeScenario()
        {
            return string.Equals(scenario, MarkerProbeScenario, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>探针用的锚点：已设归属物品的格子坐标（用于投影到屏幕坐标）。</summary>
        private readonly List<IntVec3> probeAnchors = new List<IntVec3>();

        /// <summary>探针协程只允许启动一次。</summary>
        private bool probeStarted;

        private static TestDriver instance;
        private static string pendingScenario;
        private static string pendingSave;
        private static string pendingOutDir;
        private static bool sceneHookInstalled;

        public static void Start(string scenario, string saveName, string outDir)
        {
            try
            {
                if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(Path.GetTempPath(), "po-scenario");
                Directory.CreateDirectory(outDir);

                pendingScenario = scenario;
                pendingSave = saveName;
                pendingOutDir = outDir;

                // 启动阶段创建的对象可能与场景加载竞争而丢失（实测表现为 Update 从不被调用）。
                // 因此除了立即创建，还在每次场景加载后检查实例是否还在，丢了就重建。
                UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
                sceneHookInstalled = true;

                SpawnInstance();
            }
            catch (Exception ex)
            {
                Log.Error("[PawnOwnership][TestDriver] 启动失败: " + ex);
            }
        }

        private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            if (instance != null) return;
            if (string.IsNullOrEmpty(pendingScenario)) return;
            Log.Warning("[PawnOwnership][TestDriver] 场景加载后实例丢失，重建驱动（场景=" + scene.name + "）");
            SpawnInstance();
        }

        private static void SpawnInstance()
        {
            var go = new GameObject("PawnOwnershipTestDriver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var driver = go.AddComponent<TestDriver>();
            driver.scenario = pendingScenario;
            driver.saveName = pendingSave;
            driver.outDir = pendingOutDir;
            driver.originalShowMarkers = Mod_PawnOwnership.ShowMarkers;
            driver.originalDevMode = Prefs.DevMode;
            instance = driver;

            Log.Message("[PawnOwnership][TestDriver] 已启动：scenario=" + pendingScenario
                        + ", save=" + (pendingSave ?? "(none)") + ", out=" + pendingOutDir);
        }

        private void Update()
        {
            if (finished) return;
            frame++;

            // 心跳：用于区分「Update 没被调用」与「状态机在等条件」。
            if (frame % 120 == 0)
            {
                Log.Message("[PawnOwnership][TestDriver] heartbeat phase=" + phase
                            + " state=" + Current.ProgramState
                            + " inEntry=" + GenScene.InEntryScene
                            + " map=" + (Find.CurrentMap != null)
                            + " frames=" + frame);
            }

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
                    else if (Time.realtimeSinceStartup - beginGameAt > MapTimeoutSeconds)
                    {
                        Fail("等待地图超时（" + MapTimeoutSeconds + " 秒），scenario=" + scenario
                             + "。可能卡在对话框或被 mod 不匹配阻断。");
                    }
                    break;

                case Phase.Prepare:
                    if (frame - phaseFrame < 30) return;
                    if (IsOwnershipScenario())
                    {
                        // 归属用例自己建实体、自己清理，不共用标记场景的 PrepareScene / 截图流程。
                        RunOwnershipCases();
                        return;
                    }
                    if (IsMarkerProbeScenario())
                    {
                        // 标记可见性探针：造归属 → 游戏内抓帧 A/B/C → 数值判定。
                        // 必须只启动一次：Prepare 阶段每帧都会进来，重复启动会让多个协程
                        // 互相穿插地开关标记/抓帧，比较结果变成垃圾。
                        if (!probeStarted)
                        {
                            probeStarted = true;
                            PrepareScene();
                            StartCoroutine(RunMarkerProbe());
                        }
                        return;
                    }
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
            beginGameAt = Time.realtimeSinceStartup;

            // ownership 场景必须在真实地图上跑用例，因此和 loadsave 一样需要存档。
            // 不能走 quickstart：QuickStarter.CheckQuickStart() 经实测无任何效果（见 docs/DECISIONS.md F3）。
            if (scenario == "loadsave" || scenario == "ownership" || IsMarkerProbeScenario())
            {
                if (string.IsNullOrEmpty(saveName))
                {
                    Fail("scenario=" + scenario + " 需要通过 -SaveName 指定一个存档");
                    return;
                }

                notes.Add("加载存档: " + saveName);
                Log.Message("[PawnOwnership][TestDriver] 加载存档: " + saveName);
                GameDataSaveLoader.LoadGame(saveName);
                return;
            }

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
            currentMapInfo = DescribeCurrentMap();

            var comp = map.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null)
            {
                throw new InvalidOperationException("MapComponent_PawnOwnership 为 null");
            }

            int n = 0;
            IntVec3 focus = IntVec3.Invalid;

            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (t == null || t.def == null) continue;
                if (t.def.category != ThingCategory.Item) continue;

                comp.SetOwner(t, "SelfTest");
                probeAnchors.Add(t.Position);
                if (n == 0) focus = t.Position;
                n++;
                if (n >= 5) break;
            }

            ownedCount = n;
            notes.Add("已为 " + n + " 个物品设置归属 SelfTest");

            if (n == 0)
            {
                throw new InvalidOperationException("没有可设置归属的测试物品");
            }

            if (focus.IsValid)
            {
                Find.CameraDriver.JumpToCurrentMapLoc(focus);
                notes.Add("摄像机已跳到 " + focus);
            }
            else
            {
                notes.Add("没找到可设置归属的物品");
            }

            SetMarkers(true);
        }

        private string DescribeCurrentMap()
        {
            Map map = Find.CurrentMap;
            return map != null
                ? (map.info != null ? map.info.parent?.LabelCap.ToString() : "map " + map.uniqueID)
                : "(null)";
        }

        /// <summary>
        /// ownership 场景入口：调用 <c>OwnershipCases.RunAll(outDir)</c>，然后把结果落进
        /// scenario-report.json（Finish 负责）。三条要求：
        ///   1. 用例抛异常必须在这里收口并走 Fail → Finish，否则驱动会卡在 Prepare 相位；
        ///   2. RunAll 返回 true 但报告缺失时判失败，避免"报告写不出来却门禁全绿"的假通过；
        ///   3. 生产构建（net472）走直接调用，契约在编译期受检查；stub 夹具
        ///      （tools/harness/tests/driver/DriverRegression.csproj，只链接 TestDriver.cs
        ///      并 stub 引擎 API）没有 OwnershipCases 类型，那里退回反射调用，
        ///      两条路径调用的是同一个 RunAll(string)。
        /// </summary>
        private void RunOwnershipCases()
        {
            currentMapInfo = DescribeCurrentMap();
            notes.Add("ownership 场景：调用 OwnershipCases.RunAll");
            Log.Message("[PawnOwnership][TestDriver] ownership 场景开始，outDir=" + outDir);

            try
            {
                bool ok;
#if NETFRAMEWORK
                // 生产路径：直接调用。规格要求；同时让类型/方法签名在编译期即受检查。
                ok = OwnershipCases.RunAll(outDir);
#else
                // 仅 stub 夹具（net8.0 等无游戏类型的目标）走这里，见上方第 3 条。
                MethodInfo runAll = FindOwnershipCasesRunAll();
                if (runAll == null)
                {
                    Fail("找不到 PawnOwnership.Testing.OwnershipCases.RunAll(string)");
                    return;
                }

                object result = runAll.Invoke(null, new object[] { outDir });
                ok = result is bool && (bool)result;
#endif
                notes.Add("OwnershipCases.RunAll 返回 " + ok);

                if (ok)
                {
                    string reportPath = Path.Combine(outDir, OwnershipReportFile);
                    if (!File.Exists(reportPath))
                    {
                        Fail("ownership 用例返回通过，但未生成 " + OwnershipReportFile);
                        return;
                    }
                    notes.Add("已生成 " + OwnershipReportFile);
                }

                Finish(ok, ok ? null : "详见 " + OwnershipReportFile);
            }
            catch (Exception ex)
            {
                Fail("ownership 用例执行异常: " + DescribeException(ex));
            }
        }

#if !NETFRAMEWORK
        /// <summary>
        /// stub 夹具用的反射解析：返回 null 表示契约缺失。见 RunOwnershipCases 的说明。
        /// </summary>
        private static MethodInfo FindOwnershipCasesRunAll()
        {
            const string typeName = "PawnOwnership.Testing.OwnershipCases";

            Type type = Type.GetType(typeName, false);
            if (type == null)
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        type = asm.GetType(typeName, false);
                    }
                    catch
                    {
                        type = null;
                    }
                    if (type != null) break;
                }
            }
            if (type == null) return null;

            return type.GetMethod(
                "RunAll",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
        }
#endif

        /// <summary>
        /// 异常信息尽量带内层异常：反射调用的异常会被包成 TargetInvocationException。
        /// </summary>
        private static string DescribeException(Exception ex)
        {
            var tie = ex as TargetInvocationException;
            if (tie != null && tie.InnerException != null) ex = tie.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
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

        /// <summary>
        /// 标记可见性探针：游戏内抓帧做 A/B 判定。
        /// 逻辑：抓"开"两帧 → 噪声基线；关标记抓一帧 → 信号；
        /// 对每个已知锚点（已设归属物品的行星坐标投影到屏幕）比较窗口内像素差，
        /// 信号显著高于噪声才判定"标记可见"。
        /// </summary>
        private IEnumerator RunMarkerProbe()
        {
            notes.Add("markerprobe: 开始抓帧");

            // 让摄像机/地图动画先稳定下来，否则两帧之间的"天然差异"会盖过信号。
            for (int i = 0; i < 20; i++) yield return null;

            SetMarkers(true);
            yield return new WaitForEndOfFrame();
            MarkerVisibilityProbe.Frame on1 = MarkerVisibilityProbe.Capture();
            yield return new WaitForEndOfFrame();
            MarkerVisibilityProbe.Frame on2 = MarkerVisibilityProbe.Capture();

            if (!on1.Valid || !on2.Valid)
            {
                Finish(false, "抓帧失败（CaptureScreenshotAsTexture 不可用）");
                yield break;
            }

            SetMarkers(false);
            yield return null;
            yield return new WaitForEndOfFrame();
            MarkerVisibilityProbe.Frame off = MarkerVisibilityProbe.Capture();
            SetMarkers(true);

            notes.Add("抓帧时上一帧绘制标记数=" + MapComponent_PawnOwnership.MarkersDrawnLastFrame
                      + "，DrawCalls=" + MapComponent_PawnOwnership.DrawCalls);

            if (!off.Valid)
            {
                Finish(false, "关闭标记后抓帧失败");
                yield break;
            }

            int noise = MarkerVisibilityProbe.FullFrameDiff(on1, on2);
            int signal = MarkerVisibilityProbe.FullFrameDiff(on1, off);

            Camera cam = Current.Camera;
            int best = 0;
            int bestIdx = -1;
            bool bestFlipped = false;
            int onScreen = 0;
            var anchorDiffs = new List<int>();

            for (int i = 0; i < probeAnchors.Count; i++)
            {
                IntVec3 cell = probeAnchors[i];
                // 锚点必须投影到「标记实际被画出的世界坐标」：物品水平位置 + 标记所用的层高。
                // 若投影到地面 (x,0,z)，透视相机下会与高空标记错开，窗口就量不到东西。
                Vector3 world = new Vector3(
                    cell.x + 0.5f,
                    AltitudeLayer.MetaOverlays.AltitudeFor() + 0.1f,
                    cell.z + 0.5f);
                Vector3 sp = cam.WorldToScreenPoint(world);
                bool visible = sp.z > 0f && sp.x >= 0f && sp.x < on1.Width && sp.y >= 0f && sp.y < on1.Height;
                if (!visible)
                {
                    notes.Add("锚点 " + i + " " + cell + " 不在屏幕内");
                    continue;
                }

                onScreen++;
                bool flipped;
                int d = MarkerVisibilityProbe.AnchorDiff(on1, off, (int)sp.x, (int)sp.y, out flipped);
                anchorDiffs.Add(d);
                notes.Add("锚点 " + i + " " + cell + " 屏幕=(" + (int)sp.x + "," + (int)sp.y
                          + ") 窗口差异=" + d + (flipped ? " (y 翻转后更优)" : string.Empty));
                if (d > best)
                {
                    best = d;
                    bestIdx = i;
                    bestFlipped = flipped;
                }
            }

            int windowPixels = (MarkerVisibilityProbe.WindowRadius * 2 + 1) * (MarkerVisibilityProbe.WindowRadius * 2 + 1);
            int totalPixels = on1.Width * on1.Height;
            double noisePerWindow = totalPixels > 0 ? (double)noise * windowPixels / totalPixels : 0.0;
            double threshold = Math.Max(10.0, noisePerWindow * 2.0);

            // 判定改为「有多少个锚点真的测到了标记」，而不是只看单个最大值：
            // 单个 17 像素的差异可能是巧合，多个锚点同时成立才算可靠。
            int detectedAnchors = 0;
            foreach (int d in anchorDiffs)
            {
                if (d > threshold) detectedAnchors++;
            }

            bool detected = detectedAnchors >= Math.Max(1, Math.Min(3, onScreen)) && signal > noise * 3;

            notes.Add("噪声基线整帧差异=" + noise + "（折合每窗口约 " + noisePerWindow.ToString("F1") + " 像素）");
            notes.Add("信号整帧差异=" + signal);
            notes.Add("屏幕内锚点=" + onScreen + "/" + probeAnchors.Count);
            notes.Add("达到阈值的锚点=" + detectedAnchors + "（阈值 " + threshold.ToString("F1") + "）");
            notes.Add("最佳锚点=" + bestIdx + " 窗口差异=" + best + (bestFlipped ? " (y 翻转)" : string.Empty));
            notes.Add("判定 detected=" + detected);

            Finish(detected, detected
                ? null
                : "未检测到标记像素（达到阈值的锚点 " + detectedAnchors + "，最佳窗口差异 "
                  + best + "，阈值 " + threshold.ToString("F1") + "）");
        }

        private void Finish(bool ok, string error)
        {
            if (finished) return;

            // 收尾时解除场景钩子并清空待启动参数，避免 sceneLoaded 把驱动又重建一遍。
            instance = null;
            pendingScenario = null;
            if (sceneHookInstalled)
            {
                UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
                sceneHookInstalled = false;
            }

            // 标记场景专用断言：绘制数 + 三张截图。ownership 场景不出图，绝不套用这些判定，
            // 否则永远不会通过（ownedCount/markersOnDrawn 均为 0、png 不存在）。
            if (ok && !IsOwnershipScenario() && !IsMarkerProbeScenario())
            {
                var failures = new List<string>();
                if (ownedCount <= 0) failures.Add("ownedCount=" + ownedCount + " (预期>0)");
                if (markersOnDrawn <= 0) failures.Add("markersOnDrawn=" + markersOnDrawn + " (预期>0)");
                if (markersOffDrawn != 0) failures.Add("markersOffDrawn=" + markersOffDrawn + " (预期=0)");
                if (markersBackDrawn <= 0) failures.Add("markersBackDrawn=" + markersBackDrawn + " (预期>0)");
                foreach (string f in new[] { "01-markers-on.png", "02-markers-off.png", "03-markers-back.png" })
                {
                    string p = Path.Combine(outDir, f);
                    if (!File.Exists(p)) failures.Add(f + " 不存在");
                    else if (new FileInfo(p).Length <= 0) failures.Add(f + " 为空");
                }
                if (failures.Count > 0)
                {
                    ok = false;
                    error = string.Join("; ", failures.ToArray());
                    notes.Add(error);
                }
            }
            finished = true;
            phase = Phase.Finished;

            // 恢复现场
            try { Mod_PawnOwnership.ShowMarkers = originalShowMarkers; } catch { }
            try { Prefs.DevMode = originalDevMode; } catch { }

            // 记录截图是否真的落盘（Unity 的截图是异步写文件，调用成功 ≠ 文件存在）。
            // ownership 场景不截图，这段只对标记场景有意义。
            if (!IsOwnershipScenario())
            {
                foreach (string f in new[] { "01-markers-on.png", "02-markers-off.png", "03-markers-back.png" })
                {
                    try { notes.Add("文件 " + f + " 存在=" + File.Exists(Path.Combine(outDir, f))); }
                    catch { }
                }
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
            sb.Append("  \"markerScenario\": ").Append(IsOwnershipScenario() ? "false" : "true").Append(",\n");
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
