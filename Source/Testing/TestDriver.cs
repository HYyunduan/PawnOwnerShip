using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Verse;
using Verse.AI;
using RimWorld;
using PawnOwnership.Patches;

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

        /// <summary>作物选择生效性测试场景名。</summary>
        private const string CropProbeScenario = "cropprobe";

        private bool IsCropProbeScenario()
        {
            return string.Equals(scenario, CropProbeScenario, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>现象观察场景：把种植区归属设为他人，观察本阵营小人是否仍去干活。</summary>
        private const string SowObserveScenario = "sowobserve";

        private bool IsSowObserveScenario()
        {
            return string.Equals(scenario, SowObserveScenario, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>观察场景里的"本阵营"与"他人阵营"名字。</summary>
        private const string SelfOwner = "Host";
        private const string OtherOwner = "OtherPlayer";

        /// <summary>联机观察场景：两端进入同一会话后验证归属过滤。</summary>
        private const string MpObserveScenario = "mpobserve";

        private bool IsMpObserveScenario()
        {
            return string.Equals(scenario, MpObserveScenario, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>主机建房只尝试一次。</summary>
        private bool hostAttempted;

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
                {
                    bool timedOut = Time.realtimeSinceStartup - beginGameAt > MapTimeoutSeconds;

                    // 联机客户端不读本地存档，等待主机把存档推送过来。
                    if (MpTestBoot.IsClient)
                    {
                        if (MpTestBoot.IsSessionActive() && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
                        {
                            notes.Add("客户端已加入会话：" + MpTestBoot.DescribeSession());
                            Enter(Phase.Prepare);
                        }
                        else if (timedOut)
                        {
                            Fail("客户端等待联机超时（" + MapTimeoutSeconds + " 秒）。"
                                 + "连接错误=" + (MpTestBoot.LastError ?? "无"));
                        }
                        break;
                    }

                    if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
                    {
                        // 联机主机：地图就绪后把当前单机局转为联机主机，然后等待会话建立。
                        if (MpTestBoot.IsHost && !hostAttempted)
                        {
                            hostAttempted = true;
                            bool hosted = MpTestBoot.TryHost();
                            notes.Add("建房调用=" + (hosted ? "成功" : "失败")
                                      + (MpTestBoot.LastError == null ? string.Empty : "，错误=" + MpTestBoot.LastError));
                            if (!hosted) { Fail("建房失败: " + MpTestBoot.LastError); }
                            break;
                        }

                        if (MpTestBoot.IsHost && !MpTestBoot.IsSessionActive())
                        {
                            if (timedOut) Fail("建房后等待联机会话超时（" + MapTimeoutSeconds + " 秒）");
                            break;
                        }

                        if (MpTestBoot.IsHost)
                        {
                            notes.Add("主机会话已建立：" + MpTestBoot.DescribeSession());
                        }
                        Enter(Phase.Prepare);
                    }
                    else if (timedOut)
                    {
                        Fail("等待地图超时（" + MapTimeoutSeconds + " 秒），scenario=" + scenario
                             + "。可能卡在对话框或被 mod 不匹配阻断。");
                    }
                    break;
                }

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
                    if (IsCropProbeScenario())
                    {
                        // 作物选择生效性：单位置跑断言，不需要截图。
                        if (!probeStarted)
                        {
                            probeStarted = true;
                            RunCropCases();
                        }
                        return;
                    }
                    if (IsSowObserveScenario())
                    {
                        if (!probeStarted)
                        {
                            probeStarted = true;
                            StartCoroutine(RunSowObserve());
                        }
                        return;
                    }
                    if (IsMpObserveScenario())
                    {
                        if (!probeStarted)
                        {
                            probeStarted = true;
                            RunMpObserve();
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

            // 联机客户端：不读本地存档，直接连接主机（存档由主机推送）。
            if (MpTestBoot.IsClient)
            {
                MpTestBoot.TrySetUsername("MPClient");
                notes.Add("联机客户端：连接 " + MpTestBoot.ClientAddress);
                bool connected = MpTestBoot.TryConnect();
                notes.Add("连接调用=" + (connected ? "成功" : "失败")
                          + (MpTestBoot.LastError == null ? string.Empty : "，错误=" + MpTestBoot.LastError));
                if (!connected) Fail("连接失败: " + MpTestBoot.LastError);
                return;
            }

            if (MpTestBoot.IsHost)
            {
                MpTestBoot.TrySetUsername("MPHost");
                notes.Add("联机主机：读档后转为主机");
            }

            // ownership 场景必须在真实地图上跑用例，因此和 loadsave 一样需要存档。
            // 不能走 quickstart：QuickStarter.CheckQuickStart() 经实测无任何效果（见 docs/DECISIONS.md F3）。
            if (scenario == "loadsave" || scenario == "ownership" || IsMarkerProbeScenario() || IsCropProbeScenario() || IsSowObserveScenario() || IsMpObserveScenario())
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

            // 优先挑「当前视野内」的物品，且必须满足可视条件：
            //   1) 不在迷雾里；2) 头顶没有屋顶（远古遗迹/山洞/室内都会被屋顶藏住，画了也看不见）。
            // 之前就是因为挑中了远古遗迹里的物品，导致像素比对始终为 0。
            CellRect view = Find.CameraDriver.CurrentViewRect;
            var picked = new List<Thing>();
            int skippedRoof = 0, skippedFog = 0;

            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (t == null || t.def == null) continue;
                if (t.def.category != ThingCategory.Item) continue;
                if (!t.Spawned) continue;
                if (!view.Contains(t.Position)) continue;

                if (map.fogGrid.IsFogged(t.Position)) { skippedFog++; continue; }
                if (map.roofGrid.Roofed(t.Position)) { skippedRoof++; continue; }

                picked.Add(t);
                if (picked.Count >= 50) break;
            }

            bool jumped = false;
            if (picked.Count == 0)
            {
                // 视野里没有可搬运物品：退化为全图前 5 个，并把相机移过去。
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
                {
                    if (t == null || t.def == null) continue;
                    if (t.def.category != ThingCategory.Item) continue;

                    picked.Add(t);
                    if (picked.Count >= 5) break;
                }

                if (picked.Count > 0)
                {
                    Find.CameraDriver.JumpToCurrentMapLoc(picked[0].Position);
                    jumped = true;
                }
            }

            foreach (Thing t in picked)
            {
                comp.SetOwner(t, "SelfTest");
                probeAnchors.Add(t.Position);
            }

            ownedCount = picked.Count;
            notes.Add("设置归属的物品数=" + ownedCount
                      + (jumped ? "（视野内没有符合可视条件的物品，已跳转相机）" : "（视野内、无屋顶、未迷雾）")
                      + "；因屋顶跳过 " + skippedRoof + " 个，因迷雾跳过 " + skippedFog + " 个");

            if (ownedCount == 0)
            {
                throw new InvalidOperationException("没有可设置归属的测试物品");
            }

            // 选中第一个：检查面板会显示 mod 已有的「所属：xxx」一行。
            // 这是**不依赖标记渲染**的可见证据，用来确认归属数据真的写进去了。
            Thing first = picked[0];
            Find.Selector.Select(first, false, false);
            notes.Add("已选中 " + first.LabelShort + "（位置 " + first.Position + "），检查面板应显示 所属：SelfTest");

            // 关键：把相机拉近。标记只有 0.3 格，视野 130 格时一格约 14px，
            // 标记只有约 4px，肉眼和像素比对都几乎不可辨；拉近到 20 格视野后约 25~30px。
            var rootPos = new Vector3(first.Position.x + 0.5f, 0f, first.Position.z + 0.5f);
            Find.CameraDriver.SetRootPosAndSize(rootPos, 20f);
            notes.Add("相机已拉近到视野 20 格，中心 " + first.Position);

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
            int drawnAtCapture = MapComponent_PawnOwnership.MarkersDrawnLastFrame;
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

            notes.Add("抓帧时上一帧绘制标记数=" + drawnAtCapture
                      + "，DrawCalls=" + MapComponent_PawnOwnership.DrawCalls);

            // 把三帧落盘，作为可独立复核的证据（在此之前这些数字没有任何对应图片）。
            string pA = Path.Combine(outDir, "probe-A-markers-on.png");
            string pB = Path.Combine(outDir, "probe-B-markers-on.png");
            string pC = Path.Combine(outDir, "probe-C-markers-off.png");
            bool okA = MarkerVisibilityProbe.SavePng(on1, pA);
            bool okB = MarkerVisibilityProbe.SavePng(on2, pB);
            bool okC = MarkerVisibilityProbe.SavePng(off, pC);
            notes.Add("截图尺寸=" + on1.Width + "x" + on1.Height
                      + "；落盘 A=" + okA + " B=" + okB + " C=" + okC);

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

        /// <summary>作物选择生效性测试：调用 CropCases.RunAll，把结果并入驱动报告。</summary>
        private void RunCropCases()
        {
            try
            {
                bool ok = CropCases.RunAll(outDir);
                notes.Add("CropCases.RunAll 返回 " + ok);
                Finish(ok, ok ? null : "详见 crop-report.json");
            }
            catch (Exception ex)
            {
                Fail("CropCases 异常: " + ex);
            }
        }

        /// <summary>
        /// 现象观察：把水稻种植区归属设为「他人」，小人归属设为「本阵营」，
        /// 然后让小人自然取活，看它是否仍会去种别人区域的作物。
        /// 输出：是否取活、任务详情、最终区域内水稻数量、前后截图。
        /// </summary>
        private IEnumerator RunSowObserve()
        {
            Map map = Find.CurrentMap;
            Pawn pawn = CropCases.FindColonist(map);
            if (pawn == null)
            {
                Finish(false, "没有可用殖民者");
                yield break;
            }

            string detail;
            IntVec3 cell;
            Zone_Growing zone = CropCases.BuildGrowingZone(map, pawn, out detail, out cell);
            if (zone == null)
            {
                Finish(false, "无法建立测试种植区: " + detail);
                yield break;
            }

            ThingDef rice = DefDatabase<ThingDef>.GetNamed("Plant_Rice", false);
            if (rice == null)
            {
                Finish(false, "缺少 Plant_Rice");
                yield break;
            }
            zone.SetPlantDefToGrow(rice);

            var comp = map.GetComponent<MapComponent_PawnOwnership>();

            // 对照开关：-po.selftest-observe-self 时区域归属设成小人自己（对照组），
            // 否则设成「他人」（实验组）。用于对比同一流程下小人是否去播种。
            bool sameOwner = GenCommandLine.CommandLineArgPassed("po.selftest-observe-self");
            string zoneOwner = sameOwner ? SelfOwner : OtherOwner;
            comp.SetOwnerZone(zone.ID, zoneOwner);
            pawn.SetOwner(SelfOwner);

            // 确保小人愿意干农活
            try { pawn.workSettings.SetPriority(WorkTypeDefOf.Growing, 1); } catch { }

            Find.Selector.Select(pawn, false, false);
            Find.CameraDriver.SetRootPosAndSize(new Vector3(cell.x + 0.5f, 0f, cell.z + 0.5f), 16f);

            notes.Add("测试区：" + detail + "，作物=" + rice.defName);
            notes.Add("归属设定：区域=" + zoneOwner + "（" + (sameOwner ? "对照组：同归属" : "实验组：他人归属") + "），小人(" + pawn.LabelShort + ")=" + SelfOwner);

            // 关键：写入后必须回读，确认归属真的落库了。
            // 若读回为空，说明"过滤不生效"只是因为没有归属，而不是补丁没绑定。
            string pawnOwnerRead = pawn.GetOwner();
            string zoneOwnerRead = comp.GetOwnerZone(zone.ID);
            bool worldCompOk = Find.World.GetComponent<OwnershipWorldComponent>() != null;
            notes.Add("回读：小人归属=" + (pawnOwnerRead ?? "null")
                      + "，区域归属=" + (zoneOwnerRead ?? "null")
                      + "，世界组件=" + (worldCompOk ? "存在" : "null"));
            if (string.IsNullOrEmpty(pawnOwnerRead) || string.IsNullOrEmpty(zoneOwnerRead))
            {
                notes.Add("警告：归属写入失败，本次观察无效");
                Finish(false, "归属写入后回读为空，过滤不生效属于预期行为");
                yield break;
            }

            for (int i = 0; i < 20; i++) yield return null;
            Capture("01-before.png");

            TimeSpeed prevSpeed = Find.TickManager.CurTimeSpeed;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;

            // ---- 确定性诊断：走正常扫描路径，看该区域是否会被交给小人 ----
            WorkGiverDef giverDef = DefDatabase<WorkGiverDef>.GetNamed("GrowerSow", false);
            var giver = giverDef == null ? null : giverDef.Worker as WorkGiver_GrowerSow;

            int scanCount = 0;
            bool scanContains = false;
            Job scannedJob = null;

            if (giver != null)
            {
                try
                {
                    var cells = giver.PotentialWorkCellsGlobal(pawn);
                    if (cells != null)
                    {
                        foreach (IntVec3 c in cells)
                        {
                            scanCount++;
                            if (c == cell) scanContains = true;
                        }
                    }
                }
                catch (Exception ex) { notes.Add("扫描异常: " + ex.GetType().Name + ": " + ex.Message); }

                try { scannedJob = giver.JobOnCell(pawn, cell, false); }
                catch (Exception ex) { notes.Add("派活异常: " + ex.GetType().Name + ": " + ex.Message); }
            }

            notes.Add("正常扫描格子数=" + scanCount + "，其中包含测试格=" + scanContains);
            notes.Add("JobOnCell 结果=" + (scannedJob == null
                ? "null（被过滤/不可派活）"
                : scannedJob.def.defName + "，作物=" + (scannedJob.plantDefToSow == null ? "null" : scannedJob.plantDefToSow.defName)));
            notes.Add("农活优先级(Growing)=" + (pawn.workSettings == null ? "null" : pawn.workSettings.GetPriority(WorkTypeDefOf.Growing).ToString()));

            // 有任务就强制派给小人在该区域种，观察最终是否真的种下
            if (scannedJob != null)
            {
                try
                {
                    pawn.jobs.TryTakeOrderedJob(scannedJob, JobTag.Misc);
                    notes.Add("已强制派发播种任务给 " + pawn.LabelShort);
                }
                catch (Exception ex) { notes.Add("强制派活异常: " + ex.GetType().Name + ": " + ex.Message); }
            }

            float deadline = Time.realtimeSinceStartup + 90f;
            int frames = 0;
            while (Time.realtimeSinceStartup < deadline)
            {
                frames++;
                if (CountRicePlants(map, zone, rice) > 0) break;
                yield return null;
            }

            int ricePlants = CountRicePlants(map, zone, rice);
            try { Find.TickManager.CurTimeSpeed = prevSpeed == TimeSpeed.Paused ? TimeSpeed.Normal : prevSpeed; } catch { }

            for (int i = 0; i < 30; i++) yield return null;
            Capture("02-after.png");

            notes.Add("观察帧数=" + frames + "，区域内已种下的水稻数量=" + ricePlants);
            notes.Add("结论：区域归属=" + zoneOwner + " 时，扫描" + (scanContains ? "包含" : "不包含") + "该区域格子，"
                      + (scannedJob != null ? "能派活" : "不能派活") + "，实际种下水稻=" + ricePlants);

            bool pass = ricePlants > 0;
            Finish(pass, pass ? null : "强制派活后仍没有种下水稻，需检查可达性/地形/优先级");
        }

        private static bool CellInZone(Zone_Growing zone, IntVec3 c)
        {
            foreach (IntVec3 x in zone.Cells)
            {
                if (x == c) return true;
            }
            return false;
        }

        private static int CountRicePlants(Map map, Zone_Growing zone, ThingDef rice)
        {
            int n = 0;
            foreach (IntVec3 c in zone.Cells)
            {
                foreach (Thing t in map.thingGrid.ThingsListAt(c))
                {
                    if (t is Plant p && p.def == rice) n++;
                }
            }
            return n;
        }

        /// <summary>
        /// 联机观察（S3）：验证归属过滤在真实联机下是否生效。
        ///
        /// 主机：保持会话直到客户端完成（等待 players>=2，再维持一段时间）。
        /// 客户端：以 <see cref="HaulAIUtility.PawnCanAutomaticallyHaul"/> 作为过滤入口做 A/B 对照：
        ///   归属不同 → 期望被拦截（false）；归属相同 → 期望放行（true）。
        /// </summary>
        private void RunMpObserve()
        {
            if (MpTestBoot.IsHost)
            {
                StartCoroutine(RunMpObserveHost());
                return;
            }

            RunMpObserveClient();
        }

        private IEnumerator RunMpObserveHost()
        {
            notes.Add("联机模式=host，会话=" + MpTestBoot.DescribeSession());

            float deadline = Time.realtimeSinceStartup + 180f;
            int joined = -1;
            while (Time.realtimeSinceStartup < deadline)
            {
                int n = MpTestBoot.PlayerCount();
                if (n > joined) joined = n;
                if (n >= 2) break;
                yield return null;
            }

            notes.Add("观察到玩家数=" + joined + "（会话=" + MpTestBoot.DescribeSession() + "）");

            // 主机需要存活到客户端完成断言：再维持 120 秒。
            float keepAliveUntil = Time.realtimeSinceStartup + 120f;
            while (Time.realtimeSinceStartup < keepAliveUntil)
            {
                yield return null;
            }

            notes.Add("客户端断言窗口结束，主机收尾");
            Finish(joined >= 2, joined >= 2 ? null : "未观察到第二个玩家加入");
        }

        private void RunMpObserveClient()
        {
            Map map = Find.CurrentMap;
            Pawn pawn = CropCases.FindColonist(map);
            var comp = map.GetComponent<MapComponent_PawnOwnership>();
            if (pawn == null || comp == null) { Finish(false, "缺少殖民者或归属组件"); return; }

            var items = new List<Thing>();
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (t == null || t.def == null || t.def.category != ThingCategory.Item) continue;
                if (!t.Spawned) continue;
                items.Add(t);
                if (items.Count >= 2) break;
            }

            if (items.Count < 2) { Finish(false, "可搬运物品不足 2 个，无法做 A/B 对照"); return; }

            Thing mine = items[0];
            Thing foreign = items[1];
            string origPawn = pawn.GetOwner();
            string origMine = comp.GetOwner(mine);
            string origForeign = comp.GetOwner(foreign);

            bool sameAllowed = false;
            bool foreignBlocked = false;
            bool candidateFilterOk = false;
            string filterDetail = "";

            try
            {
                pawn.SetOwner(SelfOwner);
                comp.SetOwner(mine, SelfOwner);
                comp.SetOwner(foreign, OtherOwner);

                // 核心谓词：同归属应放行、异归属应拦截（该方法本身不区分单机/联机）
                sameAllowed = InvokeCheckThingOwnership(pawn, mine);
                foreignBlocked = !InvokeCheckThingOwnership(pawn, foreign);

                // MP 门控的候选列表过滤：联机下应剔除他人归属的物品
                var input = new List<Thing> { mine, foreign };
                List<Thing> filtered = PatchHelpers
                    .PotentialWorkThingsGlobal_Postfix(input, pawn)
                    .ToList();

                bool hasMine = filtered.Contains(mine);
                bool hasForeign = filtered.Contains(foreign);
                candidateFilterOk = hasMine && !hasForeign;
                filterDetail = "输入 2 个，过滤后 " + filtered.Count
                               + " 个（含自己的=" + hasMine + "，含他人的=" + hasForeign + "）";
            }
            finally
            {
                try { pawn.SetOwner(origPawn); } catch { }
                try { comp.SetOwner(mine, origMine); } catch { }
                try { comp.SetOwner(foreign, origForeign); } catch { }
            }

            notes.Add("会话=" + MpTestBoot.DescribeSession() + "，玩家数=" + MpTestBoot.PlayerCount());
            notes.Add("测试物品：自己的=" + mine.LabelShort + "(" + mine.ThingID + ")"
                      + "，他人的=" + foreign.LabelShort + "(" + foreign.ThingID + ")");
            notes.Add("M01 同归属放行（CheckThingOwnership=true）=" + sameAllowed);
            notes.Add("M02 异归属拦截（CheckThingOwnership=false）=" + foreignBlocked);
            notes.Add("M03 候选列表过滤（MP 门控）=" + candidateFilterOk + "；" + filterDetail);

            // ---- B01 建造拦截：蓝图归属他人时，非强制工作应被拦截，强制命令应放行 ----
            string blueprintDetail;
            Thing blueprint = TryCreateBlueprint(map, pawn, out blueprintDetail);
            bool buildBlockedUnforced = false;
            bool buildAllowedForced = false;
            bool buildAllowedSameOwner = false;
            if (blueprint != null)
            {
                string origBp = comp.GetOwner(blueprint);
                try
                {
                    pawn.SetOwner(SelfOwner);
                    comp.SetOwner(blueprint, OtherOwner);

                    var scanner = DefDatabase<WorkGiverDef>.GetNamed("ConstructDeliverResourcesToBlueprints", false)?.Worker as WorkGiver_Scanner;
                    Job dummy = null;
                    buildBlockedUnforced = !PatchHelpers.JobOnThing_Prefix(scanner, pawn, blueprint, false, ref dummy);
                    buildAllowedForced = PatchHelpers.JobOnThing_Prefix(scanner, pawn, blueprint, true, ref dummy);

                    comp.SetOwner(blueprint, SelfOwner);
                    Job dummy2 = null;
                    buildAllowedSameOwner = PatchHelpers.JobOnThing_Prefix(scanner, pawn, blueprint, false, ref dummy2);
                }
                finally
                {
                    try { comp.SetOwner(blueprint, origBp); } catch { }
                    try { blueprint.Destroy(DestroyMode.Vanish); } catch { }
                }
            }
            notes.Add("B01 蓝图测试：" + blueprintDetail);
            notes.Add("B01a 异归属 + 非强制 → 拦截（期望 True）=" + buildBlockedUnforced);
            notes.Add("B01b 异归属 + 强制命令 → 放行（期望 True）=" + buildAllowedForced);
            notes.Add("B01c 同归属 + 非强制 → 放行（期望 True）=" + buildAllowedSameOwner);

            // ---- B02 区域拦截：种植区归属他人时，该区域格子应被拦截 ----
            string zoneDetail;
            IntVec3 zoneCell;
            Zone_Growing zone = CropCases.BuildGrowingZone(map, pawn, out zoneDetail, out zoneCell);
            bool zoneBlocked = false;
            bool zoneAllowed = false;
            if (zone != null)
            {
                try
                {
                    pawn.SetOwner(SelfOwner);
                    comp.SetOwnerZone(zone.ID, OtherOwner);
                    zoneBlocked = !InvokeCheckCellOwnership(pawn, zoneCell);

                    comp.SetOwnerZone(zone.ID, SelfOwner);
                    zoneAllowed = InvokeCheckCellOwnership(pawn, zoneCell);
                }
                finally
                {
                    try { comp.RemoveOwnerZone(zone.ID); } catch { }
                    try { zone.Delete(); } catch { }
                }
            }
            notes.Add("B02 种植区测试：" + zoneDetail);
            notes.Add("B02a 区域归属他人 → 该格被拦截（期望 True）=" + zoneBlocked);
            notes.Add("B02b 区域归属自己 → 该格放行（期望 True）=" + zoneAllowed);

            // ---- B03 作物错配检测（真实 bug：选水稻却种土豆）----
            // 假设：MP 下我们的 PotentialWorkCellsGlobal 后置把惰性枚举物化，
            // 导致 WorkGiver_GrowerSow 的 wantedPlantDef 缓存停留在最后一次计算的结果，
            // 于是逐格派活时用错作物。
            string cropDetail = "未执行";
            int cropChecked = 0;
            int cropMismatch = 0;
            int cropNullOrOther = 0;
            int cropNullCrop = 0;
            ThingDef rice = DefDatabase<ThingDef>.GetNamed("Plant_Rice", false);
            ThingDef potato = DefDatabase<ThingDef>.GetNamed("Plant_Potato", false);
            var cropGiver = DefDatabase<WorkGiverDef>.GetNamed("GrowerSow", false)?.Worker as WorkGiver_GrowerSow;

            if (rice != null && potato != null && cropGiver != null)
            {
                Zone_Growing zoneB = null;
                Zone_Growing zoneA = null;
                try
                {
                    // 区域 A：起点=小人位置（水稻）
                    IntVec3 originA = pawn.Position;
                    zoneA = BuildZoneNear(map, originA, rice, out IntVec3 cellA);
                    // 区域 B：偏移一段距离（土豆），确保与 A 不相邻
                    IntVec3 originB = new IntVec3(originA.x + 14, 0, originA.z + 14);
                    zoneB = BuildZoneNear(map, originB, potato, out IntVec3 cellB);

                    if (zoneA != null && zoneB != null)
                    {
                        // 必须给小人设置归属，否则我们的后置会走早退分支，测不到补丁。
                        try { pawn.SetOwner(SelfOwner); } catch { }

                        // 主测量：像 JobGiver_Work 一样**惰性**消费（逐格取、逐格派活）
                        IEnumerable<IntVec3> seq = cropGiver.PotentialWorkCellsGlobal(pawn);
                        bool seqIsList = seq is IList<IntVec3>;

                        int lazyChecked = 0;
                        int lazyMismatch = 0;
                        foreach (IntVec3 c in seq)
                        {
                            Zone_Growing at = map.zoneManager.ZoneAt(c) as Zone_Growing;
                            if (at == null || (at != zoneA && at != zoneB)) continue;

                            ThingDef expected = at.GetPlantDefToGrow();
                            if (expected == null) continue;

                            Job j = cropGiver.JobOnCell(pawn, c, false);
                            if (j == null || j.def != JobDefOf.Sow) continue;

                            lazyChecked++;
                            if (j.plantDefToSow != expected)
                            {
                                lazyMismatch++;
                                if (lazyMismatch <= 3)
                                {
                                    notes.Add("  惰性消费错配：格 " + c + " 区域期望 "
                                              + expected.defName + "，实际 "
                                              + (j.plantDefToSow == null ? "null" : j.plantDefToSow.defName));
                                }
                            }
                        }

                        // 对照组：先物化再派活（等价于修复前的旧实现），用于证明根因
                        List<IntVec3> materialized = cropGiver.PotentialWorkCellsGlobal(pawn).ToList();
                        int eagerChecked = 0;
                        int eagerMismatch = 0;
                        foreach (IntVec3 c in materialized)
                        {
                            Zone_Growing at = map.zoneManager.ZoneAt(c) as Zone_Growing;
                            if (at == null || (at != zoneA && at != zoneB)) continue;

                            ThingDef expected = at.GetPlantDefToGrow();
                            if (expected == null) continue;

                            Job j = cropGiver.JobOnCell(pawn, c, false);
                            if (j == null || j.def != JobDefOf.Sow) continue;

                            eagerChecked++;
                            if (j.plantDefToSow != expected) eagerMismatch++;
                        }

                        cropChecked = lazyChecked;
                        cropMismatch = lazyMismatch;
                        cropDetail = "惰性消费：检查 " + lazyChecked + " 格，错配 " + lazyMismatch
                                     + "；返回类型是 IList=" + seqIsList
                                     + "；对照（先物化）：检查 " + eagerChecked + " 格，错配 " + eagerMismatch;
                    }
                    else
                    {
                        cropDetail = "区域创建失败（A=" + (zoneA != null) + "，B=" + (zoneB != null) + "）";
                    }
                }
                finally
                {
                    try { if (zoneB != null) zoneB.Delete(); } catch { }
                    try { if (zoneA != null) zoneA.Delete(); } catch { }
                }
            }

            notes.Add("B03 作物错配检测：" + cropDetail);
            notes.Add("B03 判定：作物" + (cropMismatch == 0 ? "全部正确（未复现）" : "出现错配（复现真实 bug）"));

            bool pass = sameAllowed && foreignBlocked && candidateFilterOk
                        && (blueprint == null || (buildBlockedUnforced && buildAllowedForced && buildAllowedSameOwner))
                        && (zone == null || (zoneBlocked && zoneAllowed))
                        && cropMismatch == 0;

            notes.Add("判定：" + (pass ? "归属过滤在联机下按预期生效（含建造/种植区路径）" : "归属过滤行为不符合预期"));
            Finish(pass, pass ? null : "归属过滤未按预期生效，详见 M/B 系列");
        }

        /// <summary>用反射调用 GenConstruct.PlaceBlueprintForBuild（参数名映射，未用参数用 Type.Missing）。</summary>
        private static Thing TryCreateBlueprint(Map map, Pawn pawn, out string detail)
        {
            detail = "";
            try
            {
                BuildableDef def = DefDatabase<ThingDef>.GetNamed("SunLamp", false);
                if (def == null) { detail = "找不到 SunLamp 定义"; return null; }

                IntVec3 cell = IntVec3.Invalid;
                foreach (IntVec3 c in CellRect.CenteredOn(pawn.Position, 8).Cells)
                {
                    if (!c.InBounds(map)) continue;
                    if (c.Fogged(map) || map.roofGrid.Roofed(c)) continue;
                    if (map.thingGrid.ThingsListAt(c).Count > 0) continue;
                    if (!GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map).Accepted) continue;
                    cell = c;
                    break;
                }

                if (!cell.IsValid) { detail = "附近没有可放置蓝图空地"; return null; }

                MethodInfo m = typeof(GenConstruct).GetMethod("PlaceBlueprintForBuild",
                    BindingFlags.Public | BindingFlags.Static);
                if (m == null) { detail = "找不到 GenConstruct.PlaceBlueprintForBuild"; return null; }

                ParameterInfo[] ps = m.GetParameters();
                object[] args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    switch (ps[i].Name)
                    {
                        case "sourceDef": args[i] = def; break;
                        case "center": args[i] = cell; break;
                        case "map": args[i] = map; break;
                        case "rotation": args[i] = Rot4.North; break;
                        case "faction": args[i] = Faction.OfPlayer; break;
                        case "stuff": args[i] = null; break;
                        default: args[i] = Type.Missing; break;
                    }
                }

                Thing bp = m.Invoke(null, args) as Thing;
                detail = bp == null ? "创建返回 null" : ("已在 " + cell + " 创建 " + def.defName + " 蓝图");
                return bp;
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                detail = "创建蓝图失败: " + root.GetType().Name + ": " + root.Message;
                return null;
            }
        }

        /// <summary>在指定中心附近建一个种植区并设定作物，返回区域与首个格。</summary>
        private static Zone_Growing BuildZoneNear(Map map, IntVec3 origin, ThingDef crop, out IntVec3 testCell)
        {
            testCell = IntVec3.Invalid;
            try
            {
                var zone = new Zone_Growing(map.zoneManager);
                // 仅 new 不注册到 AllZones，而 WorkGiver_Grower.PotentialWorkCellsGlobal 是遍历 AllZones 的，
                // 不注册会导致扫描返回 0 格（测试会假通过）。
                if (!map.zoneManager.AllZones.Contains(zone))
                {
                    map.zoneManager.RegisterZone(zone);
                }

                int added = 0;
                foreach (IntVec3 c in CellRect.CenteredOn(origin, 5).Cells)
                {
                    if (!c.InBounds(map)) continue;
                    if (c.Fogged(map) || map.roofGrid.Roofed(c)) continue;
                    if (map.zoneManager.ZoneAt(c) != null) continue;
                    if (map.thingGrid.ThingsListAt(c).Count > 0) continue;
                    // 必须是可种植地形，否则扫描不会返回该格，测试会假通过
                    if (map.terrainGrid.TerrainAt(c).fertility <= 0f) continue;

                    try { zone.AddCell(c); added++; } catch { }
                    if (added >= 9) break;
                }

                if (added == 0) { zone.Delete(); return null; }

                zone.SetPlantDefToGrow(crop);
                testCell = zone.Cells.First();
                return zone;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>CheckCellOwnership 为私有静态方法，测试侧用反射调用。</summary>
        private static bool InvokeCheckCellOwnership(Pawn pawn, IntVec3 cell)
        {
            MethodInfo m = typeof(PatchHelpers).GetMethod(
                "CheckCellOwnership", BindingFlags.NonPublic | BindingFlags.Static);
            if (m == null) return true;
            return (bool)m.Invoke(null, new object[] { pawn, cell });
        }

        /// <summary>CheckThingOwnership 为私有静态方法，测试侧用反射调用。</summary>
        private static bool InvokeCheckThingOwnership(Pawn pawn, Thing thing)
        {
            MethodInfo m = typeof(PatchHelpers).GetMethod(
                "CheckThingOwnership", BindingFlags.NonPublic | BindingFlags.Static);
            if (m == null) return true;
            return (bool)m.Invoke(null, new object[] { pawn, thing });
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
            if (ok && !IsOwnershipScenario() && !IsMarkerProbeScenario() && !IsCropProbeScenario() && !IsSowObserveScenario() && !IsMpObserveScenario())
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
