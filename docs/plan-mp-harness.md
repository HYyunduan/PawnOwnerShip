# 方案：双客户端联机自动化测试（待审核）

> 状态：**待维护者审核，未实施**
> 目标分支：`feat/mp-test-harness`（从 `dev` 拉出）
> 撰写日期：2026-10-17

## 0. 背景与目标

### 为什么必须联机测

本模组的归属过滤全部被 MP 门控：

```csharp
// Source/Patches/PatchHelpers.cs:295 / 332
if (!MP.enabled || !MP.IsInMultiplayer) return true;   // 单机恒真放行
```

单机环境下 `CheckThingOwnership` / `CheckCellOwnership` **永远返回 true**，因此
单机测试（含 `cropprobe` / `sowobserve`）**无法验证过滤逻辑**，之前的"过滤对播种不生效"
结论只在"单机"范畴成立，不能代表联机行为。

### 目标

在同一台机器上跑两个 RimWorld 客户端并自动完成联机，使归属过滤真正生效，然后自动断言：

1. 归属过滤在联机下是否真的拦截"他人"的物品/格子/区域；
2. 两端读到的归属数据是否一致（顺带覆盖同步问题）；
3. 为后续"种田/建造"类问题提供可复现的联机回归环境。

### 验收标准

- 两个进程都进入同一联机会话（日志出现连接建立标志）；
- 两端各自产出 `mp-<role>-report.json`；
- 采集器产出汇总 `mp-report.json`（含双端对照与 PASS/FAIL）；
- 现有单机门禁 `run-tests.ps1` 行为不变。

## 1. 总体架构

```
run-mp-tests.ps1（采集器）
   │
   ├─ 进程 A：RimWorldWin64.exe
   │     -logFile <work>\host.log
   │     -savedatafolder <work>\host-data
   │     -po.selftest-mp=host
   │     -po.selftest-save=新移民1
   │     -po.selftest-scenario=mpobserve
   │     -po.selftest-out=<work>\host
   │
   └─ 进程 B：RimWorldWin64.exe
         -logFile <work>\client.log
         -savedatafolder <work>\client-data
         -po.selftest-mp=client:127.0.0.1:30502
         -po.selftest-scenario=mpobserve
         -po.selftest-out=<work>\client
```

启动顺序：先 A，轮询 A 的 `host-ready.txt`（或日志标志），再起 B。

## 2. 已核实的联机模组 API

来源：`F:\SteamLibrary\steamapps\common\RimWorld\Mods\Multiplayer\AssembliesCustom\Multiplayer.dll`
（用 `PatchAudit --types` 探查）

| 用途 | API |
|---|---|
| 主机设置对象 | `Multiplayer.Client.MpSettings.PreferredLocalServerSettings` |
| 主机设置类型 | `Multiplayer.Common.ServerSettings`（`lan` / `direct` / `lanAddress` / `maxPlayers` / `arbiter` …） |
| 从当前单机局转联机 | `Multiplayer.Client.HostWindow.HostFromSpIngame(settings)` |
| 直接连接 | `Multiplayer.Client.ServerBrowser.DirectConnect(addr)` |
| 低层连接窗口 | `new Multiplayer.Client.ConnectingWindow(address, port)` |
| 玩家名 | `Multiplayer.Client.Multiplayer.settings.username`（反射设置） |

命令行参数只有两个且不适用于本方案：`-mpserver`、`-mphostreplay`。

**结论：建房与加入都可以在游戏代码内调用，不需要模拟鼠标点 UI。**

## 3. 产品代码改动（仅测试钩子，默认行为不变）

### 新增 `Source/Testing/MpTestBoot.cs`

职责：解析 `-po.selftest-mp=` 并在正确时机调用联机 API。

- **全部通过反射调用**，不新增对 `Multiplayer.dll` 的程序集引用
  （避免给单机用户引入硬依赖；现有引用只有 `0MultiplayerAPI.dll`）。
- 仅在命令行参数存在时激活；无参数时**完全不执行**。

角色流程：

```
host:
  MainMenu 就绪
    → GameDataSaveLoader.LoadGame(saveName)
    → 等待 Find.CurrentMap != null
    → 设置 Multiplayer.settings.username = "MPHost"
    → HostWindow.HostFromSpIngame(PreferredLocalServerSettings)
    → 等待 Multiplayer.Client != null / session 建立
    → 写 host-ready.txt，启动 TestDriver.RunMpObserve(role=host)

client:
  MainMenu 就绪
    → 设置 Multiplayer.settings.username = "MPClient"
    → ServerBrowser.DirectConnect("127.0.0.1:30502")
    → 等待进入地图（由 host 下发的存档加载完成）
    → 启动 TestDriver.RunMpObserve(role=client)
```

### 修改 `Source/Testing/SelfTest.cs`

- 新增 `-po.selftest-mp` 解析，若存在则调用 `MpTestBoot.Boot()`；
- 不影响现有 `-po.selftest-scenario` 流程。

### 修改 `Source/Testing/TestDriver.cs`

- 新增场景分支 `mpobserve`；
- 不影响现有 `loadsave` / `ownership` / `markerprobe` / `cropprobe` / `sowobserve`。

### 不改动

`Source/Patches/**`、`Source/MapComponent_PawnOwnership.cs`、`Source/Mod_PawnOwnership.cs`
等产品逻辑**一行不改**。

## 4. 测试场景 `mpobserve`

两端共享同一份游戏状态（MP 是同局同步），因此用**游戏内哨兵**做跨进程协调，
不引入进程间通信。

### 协调方式

| 步骤 | 动作 |
|---|---|
| 1 | host 进入地图后，在约定位置建/选一个种植区，设作物=水稻 |
| 2 | host 把该区域归属写为 `MPClient`（`SetOwnerZone`） |
| 3 | host 把一只殖民者归属写为 `MPHost`（`SetOwner`） |
| 4 | host 把 `zone.ID` 写入报告并打日志（哨兵） |
| 5 | client 轮询：直到 `GetOwnerZone(zoneId) == "MPClient"`（说明 host 已就绪），超时 180s |
| 6 | client 端断言（核心）：归属为 `MPHost` 的小人，能否在该区域派活/播种 |
| 7 | 两端各自记录：区域归属回读、小人归属回读、`JobOnCell` 结果、是否实际种下 |

### 断言设计

| 编号 | 断言 | 期望 |
|---|---|---|
| M01 | 两端读到的 `GetOwnerZone(zoneId)` 一致 | 一致（验证同步） |
| M02 | 两端读到的小人归属一致 | 一致 |
| M03 | `MPHost` 的小人不能在 `MPClient` 的区域播种 | 不能 |
| M04 | 反向：`MPClient` 的小人可以在该区域播种 | 能 |

## 5. 采集器 `tools/harness/run-mp-tests.ps1`

- 参数：`-SaveName`、`-Port`、`-HostTimeoutSec`、`-ClientTimeoutSec`、`-KeepAlive`
- 步骤：
  1. 部署一次（复用 `run-tests.ps1` 的 deploy 逻辑或直接拷贝 DLL）
  2. 清理残留 RimWorld 进程（按命令行参数匹配，避免误杀用户自己开的游戏）
  3. 起 host → 等 `host-ready.txt`（超时则失败并收集日志）
  4. 起 client → 等 client 报告
  5. 解析两端 `mp-*.json`，写 `artifacts/mp-report.json`
  6. 强杀两个进程，复制全部产物到 `artifacts/runs/<时间>-mp-observe/`
- 失败时同样复制日志，便于定位。

## 6. 日志 / 存档 / 配置隔离

这是本方案最关键的环境问题：

| 资源 | 冲突风险 | 对策 |
|---|---|---|
| 日志 | **高** | 两端各自 `-logFile`；不使用默认 `Player.log` |
| 存档 / 自动存档 | 高 | 两端各自 `-savedatafolder` |
| 配置（`ConfigFolderPath`） | 中 | **无法用 `-savedatafolder` 隔离**（已核实：两者是独立路径）→ 两端共享；风险是两端同时写 `Prefs.xml` |
| 玩家名 | 中 | 启动后反射设置 `Multiplayer.settings.username` 区分两端 |
| 端口 | 中 | 固定 `30502`；启动前检测占用 |
| 模组列表 | 低 | 同一安装、同一 `ModsConfig.xml`，天然一致（MP 会校验） |

配置共享的处理：
- 接受共享（只读为主）；
- 结束时**强杀进程**而不是让两端并发走正常退出流程，减少 `Prefs.xml` 并发写；
- 若实测发现 `Prefs` 冲突导致启动异常，再考虑启动前备份/恢复 `Config` 目录。

## 7. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| `HostFromSpIngame` 的前置条件不明（是否必须在地图内/是否有窗口前置） | 主机起不来 | 实现前先写一个最小验证：单进程内调用并观察日志；不成立则退回 `HostUtil.HostServer` + `ServerSettings` 手工构造 |
| `DirectConnect` 需要服务器浏览器窗口在前台 | 客户端连不上 | 备选 `new ConnectingWindow(address, port)` 直接入栈 |
| MP 拒绝重复用户名 | 连接失败 | 反射设置不同 username；仍失败则改用两端不同 `Config` 目录（需先解决路径隔离） |
| 两端时序竞争 | 偶发失败 | 哨兵轮询 + 超时；不用固定 sleep 作为主判据 |
| 第二个实例 Steam 初始化异常 | 启动失败 | 你已确认本机双击 exe 双开可用，风险低 |
| 测试钩子影响正式游玩 | 回归风险 | 全部逻辑由命令行参数门控，无参数时零行为变化 |

## 8. 实施步骤与工作量

| 步骤 | 内容 | 预估 |
|---|---|---|
| S1 | 写最小反射验证：单进程内 `HostFromSpIngame` 能否成功建房 | 0.5 天 |
| S2 | `MpTestBoot` 完整实现（host/client 两条路径） | 0.5 天 |
| S3 | `mpobserve` 场景 + 哨兵协调 | 0.5 天 |
| S4 | `run-mp-tests.ps1` 采集器 + 报告 | 0.5 天 |
| S5 | 联调跑到两端稳定出报告 | 0.5~1 天 |
| 合计 | | **2.5~3 天** |

## 9. 回滚方式

- 全部改动在新分支 `feat/mp-test-harness`；
- 所有测试钩子由命令行参数门控，**不加参数时与当前版本行为完全一致**；
- 若方案不可行，直接丢弃分支，`dev` 不受影响；
- 不会触及存档格式与产品逻辑。

## 10. 待确认事项（需要你回复）

1. **本方案是否批准**（涉及新增测试钩子文件与两处测试类改动，产品逻辑不动）；
2. 是否接受**配置目录共享**（两端 Prefs 写同一份），还是要求我同时解决配置隔离；
3. 默认端口用 `30502` 是否可以；
4. 是否需要保留双开进程不自动关闭（`-KeepAlive`）以便你人工观察。
