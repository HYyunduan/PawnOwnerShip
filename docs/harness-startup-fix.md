# 启动期"假死"的修复：窗口焦点 + 后台运行

> 日期：2026-10-17　分支：`feat/mp-test-harness`

## 现象

无人值守运行自动化测试时，游戏进程启动后会长时间停在加载阶段，
**必须手动点一下游戏窗口**才继续——即所谓"启动期假死"。
表现为日志长时间停在固定行数（例如 1067 行，`EliteRaid` 补丁加载处），
主机迟迟不出现 `Server started.`，最终被超时判为失败。

## 根因

Unity 在窗口**失去焦点**时会降速甚至暂停；RimWorld 由 `Prefs.RunInBackground` 控制该行为。
而测试脚本当时用 `-WindowStyle Hidden` 启动游戏，窗口既不显示也没有焦点，
游戏便进入"几乎不推进"的状态。

不是代码 bug，是运行环境问题。

## 修复（两侧同时做）

### 1. 游戏侧：测试模式下强制后台运行

`Source/Testing/SelfTest.cs` 新增 `EnableBackgroundRunning()`，在 mod 初始化时：

```csharp
Prefs.RunInBackground = true;
UnityEngine.Application.runInBackground = true;
```

仅在带 `-po.selftest-*` 参数时生效，**不影响正常游玩**。

> 注意：参数形式是 `-po.selftest-scenario=xxx`（含值），
> 因此检测必须用 `GenCommandLine.TryGetCommandLineArg("po.selftest-scenario", out _)`；
> `CommandLineArgPassed` 只匹配精确的 `-key`，对 `-key=value` 返回 false。

### 2. 脚本侧：普通窗口启动并主动聚焦

`tools/harness/run-tests.ps1`：

```powershell
$proc = Start-Process -FilePath $GameExe -PassThru -ArgumentList $argLine -WindowStyle Normal
$focused = Focus-GameWindow -Proc $proc -TimeoutSec 30
```

`tools/harness/run-mp-tests.ps1`：主机与客户端启动后各调用一次 `Focus-GameWindow`，
并把结果作为 `host-focus` / `client-focus` 步骤记录进报告。

`Focus-GameWindow` 的实现：
- `WScript.Shell.AppActivate(进程ID)`（简单可靠）
- 兜底 Win32：`ShowWindow(SW_RESTORE)` + `BringWindowToTop` + `SetForegroundWindow`

## 实测效果

| 场景 | 修复前 | 修复后 |
|---|---|---|
| 联机门禁（主机+客户端） | 经常卡住，需 480 秒超时兜底 | **141 秒** 全流程 PASS |
| 单机门禁（ownership 11/11） | 曾多次长时间停在 1067 行 | **55 秒** 全流程 PASS |

日志中可见：

```
[PawnOwnership][SelfTest] 已启用后台运行（Prefs.RunInBackground=true），避免窗口失焦导致启动停滞
```

联机报告步骤：

```
[PASS] host-focus  已聚焦主机窗口
[PASS] client-focus 已聚焦客户端窗口
```

## 影响与边界

- 仅在带测试参数时启用后台运行，正式游玩行为不变；
- 聚焦会短暂抢占前台窗口，这是自动化测试的预期行为；
- 若聚焦失败（例如系统前台锁），`RunInBackground=true` 仍能保证游戏继续推进。
