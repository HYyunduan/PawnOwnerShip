using System;
using System.Reflection;
using Verse;

namespace PawnOwnership.Testing
{
    /// <summary>
    /// 联机测试启动钩子（仅测试用，由命令行参数门控）。
    ///
    /// 用法：
    ///   -po.selftest-mp=host                     主机：读档后把当前单机局转为联机主机
    ///   -po.selftest-mp=client:127.0.0.1:30502   客户端：直接连接指定地址
    ///
    /// 设计约束：
    ///   1. **全部通过反射调用** Multiplayer 模组的 API，不新增程序集引用，
    ///      避免给单机用户引入对 Multiplayer.dll 的硬依赖；
    ///   2. 未提供该参数时，本类不产生任何行为；
    ///   3. 所有调用失败都记录到 LastError 并写入日志，不抛出到主流程。
    /// </summary>
    public static class MpTestBoot
    {
        public const string HostMode = "host";
        public const string ClientMode = "client";
        public const string DefaultAddress = "127.0.0.1:30502";

        public static string Mode { get; private set; } = "";
        public static string ClientAddress { get; private set; } = DefaultAddress;
        public static bool HostRequested { get; private set; }

        /// <summary>最近一次反射调用失败的原因；为空表示无错误。</summary>
        public static string LastError { get; private set; }

        private static bool parsed;

        public static bool IsHost => Mode == HostMode;
        public static bool IsClient => Mode == ClientMode;
        public static bool IsActive => IsHost || IsClient;

        /// <summary>在 mod 初始化早期调用，解析命令行参数。</summary>
        public static void ParseIfRequested()
        {
            if (parsed) return;
            parsed = true;

            string raw;
            if (!GenCommandLine.TryGetCommandLineArg("po.selftest-mp", out raw)) return;
            if (string.IsNullOrWhiteSpace(raw)) return;

            raw = raw.Trim();
            int colon = raw.IndexOf(':');
            string role = colon >= 0 ? raw.Substring(0, colon) : raw;
            string rest = colon >= 0 ? raw.Substring(colon + 1) : null;

            if (string.Equals(role, ClientMode, StringComparison.OrdinalIgnoreCase))
            {
                Mode = ClientMode;
                ClientAddress = string.IsNullOrWhiteSpace(rest) ? DefaultAddress : rest.Trim();
            }
            else if (string.Equals(role, HostMode, StringComparison.OrdinalIgnoreCase))
            {
                Mode = HostMode;
            }
            else
            {
                LastError = "无法识别的 -po.selftest-mp 取值: " + raw;
                Log.Error("[PawnOwnership][MpTest] " + LastError);
                return;
            }

            Log.Message("[PawnOwnership][MpTest] 模式=" + Mode
                        + (IsClient ? "，地址=" + ClientAddress : string.Empty));
        }

        // ---------------- 反射访问 Multiplayer 模组 ----------------

        private static Assembly MpAssembly()
        {
            try { return Assembly.Load("Multiplayer"); }
            catch (Exception ex)
            {
                LastError = "加载 Multiplayer 程序集失败: " + ex.Message;
                Log.Error("[PawnOwnership][MpTest] " + LastError);
                return null;
            }
        }

        private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>取 MP 的全局设置对象（Multiplayer.settings）。</summary>
        private static object GetMpSettings(Assembly asm)
        {
            try
            {
                Type t = asm.GetType("Multiplayer.Client.Multiplayer");
                if (t == null) { LastError = "找不到类型 Multiplayer.Client.Multiplayer"; return null; }

                FieldInfo f = t.GetField("settings", AnyStatic);
                if (f != null) return f.GetValue(null);

                PropertyInfo p = t.GetProperty("settings", AnyStatic);
                return p?.GetValue(null);
            }
            catch (Exception ex)
            {
                LastError = "读取 Multiplayer.settings 失败: " + ex.Message;
                return null;
            }
        }

        /// <summary>把本客户端玩家名改成指定值，便于区分主机与客户端。</summary>
        public static bool TrySetUsername(string name)
        {
            try
            {
                Assembly asm = MpAssembly();
                if (asm == null) return false;

                object settings = GetMpSettings(asm);
                if (settings == null) return false;

                FieldInfo f = settings.GetType().GetField("username", AnyInstance);
                if (f == null) { LastError = "MpSettings 没有 username 字段"; return false; }

                f.SetValue(settings, name);
                Log.Message("[PawnOwnership][MpTest] 玩家名已设为 " + name);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "设置玩家名失败: " + ex.Message;
                Log.Error("[PawnOwnership][MpTest] " + LastError);
                return false;
            }
        }

        private static void SetField(object target, string name, object value)
        {
            try
            {
                FieldInfo f = target.GetType().GetField(name, AnyInstance);
                if (f == null) { Log.Message("[PawnOwnership][MpTest] 设置字段跳过（不存在）: " + name); return; }
                f.SetValue(target, value);
            }
            catch (Exception ex)
            {
                Log.Warning("[PawnOwnership][MpTest] 设置字段 " + name + " 失败: " + ex.Message);
            }
        }

        private static void DumpFields(object target, string tag)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[PawnOwnership][MpTest] ServerSettings(").Append(tag).Append("): ");
                foreach (FieldInfo f in target.GetType().GetFields(AnyInstance))
                {
                    object v;
                    try { v = f.GetValue(target); } catch { v = "<读取失败>"; }
                    sb.Append(f.Name).Append('=').Append(v == null ? "null" : v.ToString()).Append("; ");
                }
                Log.Message(sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Warning("[PawnOwnership][MpTest] 打印字段失败: " + ex.Message);
            }
        }

        /// <summary>读取当前生效的 MP 用户名（用于确认设置是否被 Steam 人格名覆盖）。</summary>
        public static string CurrentUsername()
        {
            try
            {
                Assembly asm = MpAssembly();
                if (asm == null) return "<无程序集>";

                object settings = GetMpSettings(asm);
                if (settings == null) return "<无设置对象>";

                FieldInfo f = settings.GetType().GetField("username", AnyInstance);
                object v = f?.GetValue(settings);
                return v == null ? "null" : v.ToString();
            }
            catch (Exception ex)
            {
                return "<读取失败: " + ex.Message + ">";
            }
        }

        /// <summary>主机：把当前单机局转为联机主机。必须在地图内调用。</summary>
        public static bool TryHost()
        {
            LastError = null;
            try
            {
                Assembly asm = MpAssembly();
                if (asm == null) return false;

                // 最后一刻设置用户名：MP 会在启动过程中用 Steam 人格名覆盖 early 设置。
                TrySetUsername("MPHost");
                Log.Message("[PawnOwnership][MpTest] 建房前用户名=" + CurrentUsername());

                object settings = GetMpSettings(asm);
                object serverSettings = null;
                if (settings != null)
                {
                    PropertyInfo pref = settings.GetType().GetProperty("PreferredLocalServerSettings", AnyInstance);
                    if (pref != null) serverSettings = pref.GetValue(settings, null);
                }

                if (serverSettings == null)
                {
                    LastError = "取不到 PreferredLocalServerSettings，无法继续";
                    Log.Error("[PawnOwnership][MpTest] " + LastError);
                    return false;
                }

                // 诊断：打印服务端设置的全部字段，便于定位 PrepareLocalServer 的空引用来源。
                DumpFields(serverSettings, "建房前");

                // 只覆盖必要项：关闭 Steam（同机双开共用同一 Steam 账号会冲突），
                // 保留 direct 模式与默认 directAddress（[::]:30502&0.0.0.0:30502），客户端走 DirectConnect。
                SetField(serverSettings, "gameName", "POTest");
                SetField(serverSettings, "steam", false);
                SetField(serverSettings, "lan", false);
                SetField(serverSettings, "direct", true);
                SetField(serverSettings, "maxPlayers", 8);
                SetField(serverSettings, "autosaveInterval", 0);
                SetField(serverSettings, "arbiter", false);
                SetField(serverSettings, "asyncTime", false);
                SetField(serverSettings, "multifaction", false);

                DumpFields(serverSettings, "建房前（已填充）");

                Type hostWindowType = asm.GetType("Multiplayer.Client.HostWindow");
                if (hostWindowType == null) { LastError = "找不到 Multiplayer.Client.HostWindow"; return false; }

                // 关键：地址字符串必须先解析成端点，否则 HostUtil.PrepareLocalServer 会空引用。
                MethodInfo parseEndpoints = hostWindowType.GetMethod("TryParseEndpoints", AnyStatic);
                if (parseEndpoints == null) { LastError = "找不到 HostWindow.TryParseEndpoints"; return false; }

                object parsed = parseEndpoints.Invoke(null, new[] { serverSettings });
                bool parseOk = parsed is bool b && b;
                Log.Message("[PawnOwnership][MpTest] TryParseEndpoints 结果=" + (parsed == null ? "null" : parsed.ToString()));
                if (!parseOk)
                {
                    LastError = "TryParseEndpoints 失败，地址无法解析";
                    Log.Error("[PawnOwnership][MpTest] " + LastError);
                    return false;
                }

                object window;
                try { window = Activator.CreateInstance(hostWindowType, new object[] { null }); }
                catch (Exception ctorEx)
                {
                    LastError = "构造 HostWindow 失败: " + ctorEx.Message;
                    Log.Error("[PawnOwnership][MpTest] " + LastError);
                    return false;
                }

                // 让窗口使用我们准备好的设置，然后走它自己的 TryHost 流程（包含长事件排队）。
                SetField(window, "serverSettings", serverSettings);

                MethodInfo tryHost = hostWindowType.GetMethod("TryHost", AnyInstance);
                if (tryHost == null) { LastError = "找不到 HostWindow.TryHost"; return false; }

                tryHost.Invoke(window, null);
                HostRequested = true;
                Log.Message("[PawnOwnership][MpTest] 已调用 HostWindow.TryHost");
                return true;
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                LastError = root.GetType().Name + ": " + root.Message;
                Log.Error("[PawnOwnership][MpTest] 建房失败: " + root);
                return false;
            }
        }

        /// <summary>客户端：直接连接主机。</summary>
        public static bool TryConnect()
        {
            LastError = null;
            try
            {
                Assembly asm = MpAssembly();
                if (asm == null) return false;

                // 最后一刻设置用户名，避免被 Steam 人格名覆盖导致与主机重名。
                TrySetUsername("MPClient");
                Log.Message("[PawnOwnership][MpTest] 连接前用户名=" + CurrentUsername());

                Type sb = asm.GetType("Multiplayer.Client.ServerBrowser");
                if (sb == null) { LastError = "找不到 Multiplayer.Client.ServerBrowser"; return false; }

                MethodInfo m = sb.GetMethod("DirectConnect", AnyStatic);
                if (m == null) { LastError = "找不到 ServerBrowser.DirectConnect"; return false; }

                m.Invoke(null, new object[] { ClientAddress });
                Log.Message("[PawnOwnership][MpTest] 已调用 DirectConnect(" + ClientAddress + ")");
                return true;
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                LastError = root.GetType().Name + ": " + root.Message;
                Log.Error("[PawnOwnership][MpTest] 连接失败: " + root);
                return false;
            }
        }

        /// <summary>是否已经进入联机会话（用于等待连接/建房完成）。</summary>
        public static bool IsSessionActive()
        {
            try
            {
                Assembly asm = MpAssembly();
                if (asm == null) return false;

                Type t = asm.GetType("Multiplayer.Client.Multiplayer");
                if (t == null) return false;

                FieldInfo session = t.GetField("session", AnyStatic);
                if (session != null && session.GetValue(null) != null) return true;

                PropertyInfo client = t.GetProperty("Client", AnyStatic);
                if (client != null && client.GetValue(null, null) != null) return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>会话状态的可读描述，写进测试报告用。</summary>
        public static string DescribeSession()
        {
            Assembly asm = MpAssembly();
            if (asm == null) return "Multiplayer 程序集不可用";

            Type t = asm.GetType("Multiplayer.Client.Multiplayer");
            if (t == null) return "找不到 Multiplayer.Client.Multiplayer";

            FieldInfo session = t.GetField("session", AnyStatic);
            object s = session?.GetValue(null);
            if (s == null) return "session=null";

            try
            {
                FieldInfo players = s.GetType().GetField("players", AnyInstance);
                object list = players?.GetValue(s);
                int count = -1;
                if (list is System.Collections.ICollection col) count = col.Count;
                return "session 存在，players=" + (count >= 0 ? count.ToString() : "未知");
            }
            catch (Exception ex)
            {
                return "session 存在（读取玩家数失败: " + ex.Message + "）";
            }
        }
    }
}
