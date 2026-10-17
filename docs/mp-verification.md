# 双客户端联机自动化测试（L3）验证结论

> 日期：2026-10-17　分支：`feat/mp-test-harness`
> 方案：[plan-mp-harness.md](plan-mp-harness.md)

## 结论

**联机门禁已跑通并通过。** 归属过滤在真实双客户端联机环境下按预期生效：

| 编号 | 断言 | 结果 |
|---|---|---|
| M01 | 同归属 → `CheckThingOwnership` 放行 | ✅ True |
| M02 | 异归属 → `CheckThingOwnership` 拦截 | ✅ True |
| M03 | MP 门控的候选列表过滤（`PotentialWorkThingsGlobal_Postfix`）：输入 2 个物品，过滤后只剩自己的 | ✅ True |

两端均进入同一会话（`session 存在，players=2`）。

## 怎么跑

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\harness\run-mp-tests.ps1 `
    -SaveName '新移民1' -Port 30502 -HostTimeoutSec 240 -ClientTimeoutSec 300
```

脚本会自动：

1. 备份 MP 设置文件
2. 写入主机用户名 `MPHost` → 启动主机 → 等日志 `Server started.`
3. 写入客户端用户名 `MPClient` → 启动客户端 → 等待 `scenario-report.json`
4. 汇总到 `artifacts/mp-report.json`，产物复制到 `artifacts/runs/<时间>-mp-observe/`
5. 强杀两个进程并恢复 MP 设置

## 实现要点（踩过的坑）

| 问题 | 结论 |
|---|---|
| **日志冲突** | 两端各自 `-logFile`，不使用默认 `Player.log` |
| **配置目录无法隔离** | `GenFilePaths.ConfigFolderPath` 与 `SaveDataFolderPath` 是独立路径，`-savedatafolder` 只隔离存档，两端共享 Config |
| **用户名重复** | MP 的活动设置文件是 `Config/Mod_2606448745_Multiplayer.xml`（workshop packageId），**不是** `Mod_Multiplayer_Multiplayer.xml`；且用户名在**游戏启动时**读取并缓存，运行中修改无效 → 必须启动前串行改写文件 |
| **建房入口** | `HostUtil.HostServer` 会因缺少端点解析而空引用；正确路径：设置 `ServerSettings` → `HostWindow.TryParseEndpoints` → `new HostWindow(null)` → 设置 `serverSettings` 字段 → 实例方法 `TryHost()` |
| **连接入口** | `ServerBrowser.DirectConnect("127.0.0.1:30502")` 可用 |
| **Steam** | 两端设置 `steam=false / direct=true`，走 UDP 30502 直连（`Get-NetTCPConnection` 查不到，需用 `Get-NetUDPEndpoint`） |
| **测试断言入口** | `CheckThingOwnership`（反射，私有静态）与 `PotentialWorkThingsGlobal_Postfix`（公开）可直接调用，不依赖原版的可达性/仓储条件 |

## 单机 vs 联机的门控差异（重要）

| 路径 | 单机 | 联机 |
|---|---|---|
| `JobOnThing` / `HasJobOnThing` / `JobOnCell` / `HasJobOnCell` 前缀 | **生效**（内部 `CheckThingOwnership` 无 MP 门控） | 生效 |
| `PotentialWorkThingsGlobal` / `PotentialWorkCellsGlobal` 候选过滤 | **不生效**（`!MP.IsInMultiplayer` 直接放行） | 生效 |
| `HaulAIUtility` / `GenClosest` / `StoreUtility` 等 | 不生效 | 生效 |

因此：验证"候选列表过滤"必须用联机门禁；验证"谓词本身"单机即可。

## 产物

- `artifacts/mp-report.json`
- `artifacts/runs/20261017-210410-mp-observe/`（host.log / client.log / 两端报告）
