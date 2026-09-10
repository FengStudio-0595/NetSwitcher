// NetSwitcher - Windows 网络配置快速切换工具
// 原生 WinForms 版：只用系统自带控件，无第三方依赖，单文件 exe。
//
// 编译（不需要任何 SDK / NuGet / Visual Studio）:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
//     /nologo /target:winexe /platform:anycpu /codepage:65001 ^
//     /out:NetSwitcher.exe /win32manifest:app.manifest ^
//     /r:System.Windows.Forms.dll /r:System.Drawing.dll NetSwitcher.cs
//
// 全局字体:
//   只在 MainForm 构造函数里设一次 this.Font = 微软雅黑，
//   所有子控件自动继承 —— 不给任何子控件单独设 Font，
//   这样连 ComboBox 的弹出列表也必然是同一个字体。
//   验证: NetSwitcher.exe --uidump
//
// 关于 IPv6 协议开关:
//   netsh 没有开关协议绑定的能力，必须走 PowerShell 的
//   Get-NetAdapter | Disable/Enable-NetAdapterBinding -ComponentID ms_tcpip6。
//   用「接口索引」取网卡对象再管道过去，全程不出现中文网卡名，避开引号转义。
//   （-InputObject 参数形式实测不能用：类型不匹配）
//
// 关于逻辑:
//   全部通过 netsh 操作, 用「接口索引」而不是接口名传参, 避开中文名/引号转义问题。
//   留空的字段一律「不碰」——只改你填了的东西。
//   每次动配置之前先存快照, 所以「还原快照」永远能退回原样。
//   操作完立刻回读网卡真实状态做校验, 不靠 netsh 的退出码自说自话。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace NetSwitcher
{
    // ================================================================ 数据模型

    internal class AdapterInfo
    {
        public int Index;
        public string Name;
        public string Description;
        public OperationalStatus Status;
        public bool V4Dhcp;
        public List<string> V4 = new List<string>();
        public List<string> V4Gw = new List<string>();
        public List<string> V6 = new List<string>();
        public List<string> V6Gw = new List<string>();
        public List<string> Dns4 = new List<string>();
        public List<string> Dns6 = new List<string>();

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case OperationalStatus.Up: return L.T("st.up");
                    case OperationalStatus.Down: return L.T("st.down");
                    case OperationalStatus.NotPresent: return L.T("st.notpresent");
                    case OperationalStatus.Dormant: return L.T("st.dormant");
                    case OperationalStatus.LowerLayerDown: return L.T("st.lowerdown");
                    case OperationalStatus.Testing: return L.T("st.testing");
                    default: return Status.ToString();
                }
            }
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1}  ({2})", Index, Name, StatusText);
        }
    }

    internal class NetshResult
    {
        public int ExitCode;
        public string Output = "";
        public string Command = "";
        public bool Ok { get { return ExitCode == 0; } }
    }

    /// <summary>常用公共 DNS 预设。空字符串表示该厂商没有这一族地址。</summary>
    internal class DnsPreset
    {
        public string ZhName, EnName;
        public string V4a, V4b;
        public string V6a, V6b;

        public DnsPreset(string zh, string en, string v4a, string v4b, string v6a, string v6b)
        {
            ZhName = zh; EnName = en; V4a = v4a; V4b = v4b; V6a = v6a; V6b = v6b;
        }

        /// <summary>按当前语言取显示名。</summary>
        public string Name { get { return L.IsEn ? EnName : ZhName; } }

        public override string ToString()
        {
            return V6a.Length == 0 ? Name + L.T("p.nov6") : Name;
        }
    }

    // ================================================================ 常用 DNS 表

    internal static class Dns
    {
        // 数据来源: github.com/lalifeier/awesome-public-dns （权威公开清单）
        // 114DNS 官方没有公开 IPv6 解析地址，所以它只有 IPv4。
        public static readonly DnsPreset[] All = new DnsPreset[]
        {
            new DnsPreset("阿里 DNS",  "AliDNS",         "223.5.5.5",      "223.6.6.6",       "2400:3200::1",         "2400:3200:baba::1"),
            new DnsPreset("腾讯 DNS",  "Tencent DNS",    "119.29.29.29",   "182.254.116.116", "2402:4e00::",          ""),
            new DnsPreset("114 DNS",   "114 DNS",        "114.114.114.114","114.114.115.115", "",                     ""),
            new DnsPreset("百度 DNS",  "Baidu DNS",      "180.76.76.76",   "",                "2400:da00::6666",      ""),
            new DnsPreset("360 DNS",   "360 DNS",        "101.226.4.6",    "218.30.118.6",    "",                     ""),
            new DnsPreset("火山引擎",  "Volcano Engine", "180.184.1.1",    "180.184.2.2",     "",                     ""),
            new DnsPreset("Google",    "Google",         "8.8.8.8",        "8.8.4.4",         "2001:4860:4860::8888", "2001:4860:4860::8844"),
            new DnsPreset("Cloudflare","Cloudflare",     "1.1.1.1",        "1.0.0.1",         "2606:4700:4700::1111", "2606:4700:4700::1001"),
            new DnsPreset("Quad9",     "Quad9",          "9.9.9.9",        "149.112.112.112", "2620:fe::fe",          "2620:fe::fe:9"),
        };
    }

    // ================================================================ 网卡读取

    internal static class Adapters
    {
        public static List<AdapterInfo> List()
        {
            var result = new List<AdapterInfo>();
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return result; }

            foreach (NetworkInterface ni in all)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var info = new AdapterInfo();
                info.Name = ni.Name;
                info.Description = ni.Description;
                info.Status = ni.OperationalStatus;
                info.Index = -1;
                info.V4Dhcp = false;

                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); }
                catch { continue; }

                try
                {
                    IPv4InterfaceProperties p4 = props.GetIPv4Properties();
                    if (p4 != null) { info.Index = p4.Index; info.V4Dhcp = p4.IsDhcpEnabled; }
                }
                catch { }
                if (info.Index < 0)
                {
                    try
                    {
                        IPv6InterfaceProperties p6 = props.GetIPv6Properties();
                        if (p6 != null) info.Index = p6.Index;
                    }
                    catch { }
                }
                if (info.Index < 0) continue;

                foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                {
                    try
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            info.V4.Add(ua.Address + "/" + ua.PrefixLength);
                        else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6)
                            info.V6.Add(ua.Address + "/" + ua.PrefixLength);
                    }
                    catch { }
                }
                foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                {
                    try
                    {
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork) info.V4Gw.Add(g.Address.ToString());
                        else if (g.Address.AddressFamily == AddressFamily.InterNetworkV6) info.V6Gw.Add(g.Address.ToString());
                    }
                    catch { }
                }
                foreach (IPAddress d in props.DnsAddresses)
                {
                    try
                    {
                        if (d.AddressFamily == AddressFamily.InterNetwork) info.Dns4.Add(d.ToString());
                        else if (d.AddressFamily == AddressFamily.InterNetworkV6) info.Dns6.Add(d.ToString());
                    }
                    catch { }
                }

                result.Add(info);
            }
            result.Sort(delegate (AdapterInfo a, AdapterInfo b) { return a.Index.CompareTo(b.Index); });
            return result;
        }

        public static AdapterInfo ByIndex(int index)
        {
            foreach (AdapterInfo a in List()) if (a.Index == index) return a;
            return null;
        }
    }

    // ================================================================ 进程调用

    internal static class Shell
    {
        private static Encoding _enc;
        private static Encoding Enc
        {
            get
            {
                if (_enc == null)
                {
                    try { _enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
                    catch { _enc = Encoding.Default; }
                }
                return _enc;
            }
        }

        private static string Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Enc;
            psi.StandardErrorEncoding = Enc;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                bool ok = p.HasExited && p.ExitCode == 0;
                return (ok ? "" : "!") + (o + e).Trim();
            }
        }

        public static NetshResult Netsh(string args)
        {
            var r = new NetshResult();
            r.Command = "netsh " + args;
            try
            {
                string outp = Run("netsh", args);
                if (outp.StartsWith("!")) { r.ExitCode = 1; r.Output = outp.Substring(1); }
                else { r.ExitCode = 0; r.Output = outp; }
            }
            catch (Exception ex)
            {
                r.ExitCode = -1;
                r.Output = ex.Message;
            }
            return r;
        }

        /// <summary>执行一段 PowerShell。命令里不能出现双引号（外层用双引号包着传）。</summary>
        public static bool Ps(string command, out string output)
        {
            output = "";
            try
            {
                string outp = Run("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command + "\"");
                if (outp.StartsWith("!")) { output = outp.Substring(1); return false; }
                output = outp;
                return true;
            }
            catch (Exception ex) { output = ex.Message; return false; }
        }

        public static bool Ping(string host, int timeoutMs)
        {
            try
            {
                using (Ping p = new Ping())
                {
                    PingReply rep = p.Send(host, timeoutMs);
                    return rep != null && rep.Status == IPStatus.Success;
                }
            }
            catch { return false; }
        }
    }

    // ================================================================ 地址换算

    internal static class IpUtil
    {
        public static int MaskToPrefix(string mask)
        {
            IPAddress a;
            if (!IPAddress.TryParse(mask, out a)) return -1;
            if (a.AddressFamily != AddressFamily.InterNetwork) return -1;
            byte[] b = a.GetAddressBytes();
            uint m = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            uint inv = ~m;
            if (((inv + 1) & inv) != 0) return -1;
            int p = 0;
            for (int i = 31; i >= 0; i--) { if (((m >> i) & 1) == 1) p++; else break; }
            for (int i = 31 - p; i >= 0; i--) if (((m >> i) & 1) == 1) return -1;
            return p;
        }

        public static string PrefixToMask(int prefix)
        {
            if (prefix < 0 || prefix > 32) return null;
            uint m = prefix == 0 ? 0u : (0xFFFFFFFFu << (32 - prefix));
            return string.Format("{0}.{1}.{2}.{3}", (m >> 24) & 0xFF, (m >> 16) & 0xFF, (m >> 8) & 0xFF, m & 0xFF);
        }

        public static string NormalizeMask(string input, out int prefix)
        {
            prefix = -1;
            if (input == null) return null;
            string s = input.Trim();
            if (s.Length == 0) return null;
            if (s.StartsWith("/")) s = s.Substring(1);
            int p;
            if (int.TryParse(s, out p))
            {
                if (p < 0 || p > 32) return null;
                prefix = p;
                return PrefixToMask(p);
            }
            int q = MaskToPrefix(s);
            if (q < 0) return null;
            prefix = q;
            return s;
        }

        public static bool SameSubnet(IPAddress a, IPAddress b, int prefix)
        {
            if (prefix < 0 || prefix > 32) return false;
            byte[] x = a.GetAddressBytes();
            byte[] y = b.GetAddressBytes();
            if (x.Length != 4 || y.Length != 4) return false;
            uint m = prefix == 0 ? 0u : (0xFFFFFFFFu << (32 - prefix));
            uint ux = ((uint)x[0] << 24) | ((uint)x[1] << 16) | ((uint)x[2] << 8) | x[3];
            uint uy = ((uint)y[0] << 24) | ((uint)y[1] << 16) | ((uint)y[2] << 8) | y[3];
            return (ux & m) == (uy & m);
        }

        public static bool IsV4(string s)
        {
            IPAddress a;
            return IPAddress.TryParse(s, out a) && a.AddressFamily == AddressFamily.InterNetwork;
        }

        public static bool IsV6(string s)
        {
            IPAddress a;
            return IPAddress.TryParse(s, out a) && a.AddressFamily == AddressFamily.InterNetworkV6;
        }
    }

    // ================================================================ 快照

    internal class Snapshot
    {
        public string Adapter = "";
        public int Index = -1;
        public bool V4Dhcp;
        public List<string> V4 = new List<string>();
        public List<string> V4Gw = new List<string>();
        public List<string> V6 = new List<string>();
        public List<string> V6Gw = new List<string>();
        public List<string> Dns4 = new List<string>();
        public List<string> Dns6 = new List<string>();

        public static Snapshot Capture(AdapterInfo a)
        {
            var s = new Snapshot();
            s.Adapter = a.Name;
            s.Index = a.Index;
            s.V4Dhcp = a.V4Dhcp;
            s.V4.AddRange(a.V4);
            s.V4Gw.AddRange(a.V4Gw);
            foreach (string v in a.V6) if (!v.ToLowerInvariant().StartsWith("fe80")) s.V6.Add(v);
            s.Dns4.AddRange(a.Dns4);
            s.Dns6.AddRange(a.Dns6);
            return s;
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# NetSwitcher snapshot v2");
            sb.AppendLine("adapter=" + Adapter);
            sb.AppendLine("index=" + Index.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("v4dhcp=" + (V4Dhcp ? "1" : "0"));
            sb.AppendLine("v4=" + string.Join(",", V4.ToArray()));
            sb.AppendLine("v4gw=" + string.Join(",", V4Gw.ToArray()));
            sb.AppendLine("v6=" + string.Join(",", V6.ToArray()));
            sb.AppendLine("v6gw=" + string.Join(",", V6Gw.ToArray()));
            sb.AppendLine("dns4=" + string.Join(",", Dns4.ToArray()));
            sb.AppendLine("dns6=" + string.Join(",", Dns6.ToArray()));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static Snapshot Load(string path)
        {
            if (!File.Exists(path)) return null;
            var s = new Snapshot();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i);
                string v = line.Substring(i + 1);
                switch (k)
                {
                    case "adapter": s.Adapter = v; break;
                    case "index": int.TryParse(v, out s.Index); break;
                    case "v4dhcp": s.V4Dhcp = (v == "1"); break;
                    case "v4": s.V4 = Split(v); break;
                    case "v4gw": s.V4Gw = Split(v); break;
                    case "v6": s.V6 = Split(v); break;
                    case "v6gw": s.V6Gw = Split(v); break;
                    case "dns": s.Dns4 = Split(v); break;      // v1 旧格式，只有 IPv4 DNS
                    case "dns4": s.Dns4 = Split(v); break;
                    case "dns6": s.Dns6 = Split(v); break;
                }
            }
            return s;
        }

        private static List<string> Split(string v)
        {
            var l = new List<string>();
            if (v == null) return l;
            foreach (string p in v.Split(',')) if (p.Trim().Length > 0) l.Add(p.Trim());
            return l;
        }
    }

    // ================================================================ 界面文字

    /// <summary>
    /// 全部界面文字集中在这里，中英各一份。
    ///
    /// 编译时用 /define:EN 切成英文版 —— 逻辑代码只有一份，两边不会走岔：
    ///   中文版: csc ... /out:NetSwitcher-chs.exe NetSwitcher.cs
    ///   英文版: csc ... /define:EN /out:NetSwitcher-en.exe NetSwitcher.cs
    ///
    /// 用查表而不是命名常量，是为了让这张表一行一条、对照着看好改。
    /// 找不到的 key 会原样返回，配合 --uidump 的自检能立刻发现拼错。
    /// </summary>
    internal static class L
    {
        private static readonly Dictionary<string, string[]> M = new Dictionary<string, string[]>();
        private static bool _en;

        private static void A(string k, string zh, string en) { M[k] = new string[] { zh, en }; }

        /// <summary>取当前语言的文字。</summary>
        public static string T(string k)
        {
            string[] v;
            if (M.TryGetValue(k, out v)) return v[_en ? 1 : 0];
            return "<" + k + ">";      // 缺 key 时显眼一点，别静默出错
        }

        /// <summary>取文字并套参数。</summary>
        public static string F(string k, params object[] a)
        {
            return string.Format(T(k), a);
        }

        public static bool IsEn { get { return _en; } }

        /// <summary>key 总数，给自检用。</summary>
        public static int Count { get { return M.Count; } }

        static L()
        {
#if EN
            _en = true;
#else
            _en = false;
#endif

            // ---------------- 窗体 / 菜单
            A("apptitle",     "网络配置快速切换",              "Network Switcher");
            A("menu.about",   "关于",                          "About");
            A("menu.lang",    "语言",                          "Language");

            // ---------------- 顶部
            A("adapter",      "适配器",                        "Adapter");
            A("refresh",      "刷新",                          "Refresh");

            // ---------------- 状态框
            A("status",       "当前状态",                      "Current Status");
            A("status.no",    "没有找到可用的网络适配器。",      "No usable network adapters found.");
            A("log.noadapter","未发现适配器。",                 "No adapters found.");
            A("word.index",   "索引",                          "Index");
            A("word.none",    "(无)",                          "(none)");
            A("word.static",  "静态",                          "Static");
            A("word.dhcp",    "DHCP 自动",                     "DHCP");
            A("word.gw",      "网关",                          "Gateway");
            A("word.noglobal","(无全局地址)",                   "(no global address)");

            A("st.up",        "已连接",                        "Connected");
            A("st.down",      "断开",                          "Disconnected");
            A("st.notpresent","不存在",                        "Not present");
            A("st.dormant",   "休眠",                          "Dormant");
            A("st.lowerdown", "底层断开",                      "Lower layer down");
            A("st.testing",   "测试中",                        "Testing");

            // ---------------- 分组标题
            A("g.ipv4",       "IPv4",                          "IPv4");
            A("g.ipv6",       "IPv6",                          "IPv6");
            A("g.mode",       "应用方式",                      "Apply Mode");

            // ---------------- 字段标签
            A("f.address",    "地址",                          "IP Address");
            A("f.mask",       "子网掩码",                      "Subnet Mask");
            A("f.gateway",    "网关",                          "Gateway");
            A("f.prefix",     "前缀",                          "Prefix");
            A("f.preset",     "预设",                          "Preset");
            A("f.dns1",       "DNS 1",                         "DNS 1");
            A("f.dns2",       "DNS 2",                         "DNS 2");

            A("hint.v4",      "掩码可填 255.255.255.0 或 /24    ·    留空 = 不修改该项",
                              "Mask: 255.255.255.0 or /24    ·    Blank = leave unchanged");
            A("hint.v6",      "地址留空 = 完全不动 IPv6",
                              "Leave the address blank = do not touch IPv6");

            // ---------------- IPv6 开关
            A("v6.busy",      "IPv6 状态…",                    "IPv6 status...");
            A("v6.unknown",   "IPv6 状态未知",                 "IPv6 status unknown");
            A("v6.on",        "开启 IPv6",                     "Enable IPv6");
            A("v6.off",       "关闭 IPv6",                     "Disable IPv6");
            A("v6.needwin8",  "需要 Windows 8 以上（NetAdapter 模块），且本程序要以管理员身份运行。",
                              "Requires Windows 8 or later (NetAdapter module) and this program must run as administrator.");
            A("v6.confirm",   "{0} [{1}] 的 IPv6 协议？\r\n\r\n等价于在网卡属性里{2}「Internet 协议版本 6 (TCP/IPv6)」。\r\n只影响这一块网卡，不需要重启，再点一次就能切回来。",
                              "{0} IPv6 on [{1}]?\r\n\r\nSame as {2} \"Internet Protocol Version 6 (TCP/IPv6)\" in the adapter properties.\r\nAffects only this adapter. No reboot needed, and clicking again switches it back.");
            A("v6.onword",    "开启",                          "enabling");
            A("v6.offword",   "关闭",                          "disabling");
            A("v6.check",     "勾选",                          "checking");
            A("v6.uncheck",   "取消勾选",                      "unchecking");
            A("v6.loghead",   "---- {0} IPv6 协议 ----",       "---- {0} IPv6 ----");
            A("v6.done.on",   "IPv6 已开启。网卡状态会在几秒内更新。",
                              "IPv6 enabled. The adapter will update within a few seconds.");
            A("v6.done.off",  "IPv6 已关闭。网卡状态会在几秒内更新。",
                              "IPv6 disabled. The adapter will update within a few seconds.");

            // ---------------- 应用方式
            A("m.replace",    "覆盖（替换原有 IP）",            "Replace existing IP");
            A("m.append",     "追加（保留原有 IP）",            "Keep existing IP");
            A("m.nogw",       "不设置默认网关",                 "No default gateway");

            // ---------------- 按钮
            A("b.apply",      "应用配置",                      "Apply");
            A("b.dhcp",       "一键恢复 DHCP",                 "Restore DHCP");
            A("b.restore",    "还原上次快照",                   "Restore Snapshot");
            A("b.test",       "测试连通",                      "Test Connectivity");
            A("b.log",        "日志",                          "Log");

            // ---------------- 启动日志
            A("log.ready",    "就绪。选中适配器 → 填要改的项 → 点「应用配置」。",
                              "Ready. Pick an adapter, fill in what you want to change, then click Apply.");
            A("log.safe",     "留空的字段一律不修改；每次动手前都会自动存快照。",
                              "Blank fields are never touched. A snapshot is saved before every change.");

            // ---------------- 预设
            A("p.auto",       "恢复自动获取",                   "Automatic");
            A("p.nov6",       "（无 IPv6）",                    " (no IPv6)");
            A("p.ali",        "阿里 DNS",                      "AliDNS");
            A("p.tencent",    "腾讯 DNS",                      "Tencent DNS");
            A("p.114",        "114 DNS",                       "114 DNS");
            A("p.baidu",      "百度 DNS",                      "Baidu DNS");
            A("p.360",        "360 DNS",                       "360 DNS");
            A("p.volcano",    "火山引擎",                      "Volcano Engine");

            A("p.pick.auto.v4", "已选「恢复自动获取」：应用配置后，IPv4 DNS 会改回从上级路由器获取。",
                                "Selected Automatic: applying will set IPv4 DNS back to automatic (from the router).");
            A("p.pick.auto.v6", "已选「恢复自动获取」：应用配置后，IPv6 DNS 会改回从上级路由器获取。",
                                "Selected Automatic: applying will set IPv6 DNS back to automatic (from the router).");
            A("p.fill.v4",    "已填入 {0} 的 IPv4 DNS：{1}（还没生效，点「应用配置」才写下去）",
                              "Filled {0} IPv4 DNS: {1} (not active yet — click Apply to write it)");
            A("p.fill.v6",    "已填入 {0} 的 IPv6 DNS：{1}（还没生效，点「应用配置」才写下去）",
                              "Filled {0} IPv6 DNS: {1} (not active yet — click Apply to write it)");

            // ---------------- 通用
            A("common.pick",  "请先选择一个适配器。",            "Select an adapter first.");
            A("common.ok",    "确认",                          "Confirm");
            A("common.yes",   "确定",                          "OK");
            A("common.already","已经",                         "already");

            // ---------------- 应用：校验
            A("val.empty",    "什么都没填。至少要填一项 IP 地址或 DNS，或者把「预设」选成「恢复自动获取」来把 DNS 改回自动。",
                              "Nothing to apply. Fill in at least one IP address or DNS, or set Preset to Automatic to reset DNS.");
            A("val.badv4",    "IPv4 地址不合法：{0}",           "Invalid IPv4 address: {0}");
            A("val.nomask",   "填了 IPv4 地址就必须填子网掩码。", "An IPv4 address requires a subnet mask.");
            A("val.badmask",  "子网掩码不合法（必须是连续的掩码，如 255.255.255.0 或 /24）：{0}",
                              "Invalid subnet mask (must be contiguous, e.g. 255.255.255.0 or /24): {0}");
            A("val.badgw4",   "IPv4 网关不合法：{0}",           "Invalid IPv4 gateway: {0}");
            A("val.gwsubnet", "网关 {0} 不在 {1} 这个子网里。\r\n\r\n这样设下去通常上不了网（除非你就是要用别网段网关）。\r\n\r\n仍要继续吗？",
                              "Gateway {0} is not in subnet {1}.\r\n\r\nThis usually means no internet access (unless you really want an off-subnet gateway).\r\n\r\nContinue anyway?");
            A("val.gwsame",   "网关不能和本机地址相同。",        "The gateway cannot be the same as the local address.");
            A("val.lastbyte", "地址 {0} 的最后一段是 0 或 255，通常是网络号/广播地址。\r\n\r\n仍要继续吗？",
                              "The last octet of {0} is 0 or 255, which is usually a network or broadcast address.\r\n\r\nContinue anyway?");
            A("val.badv6",    "IPv6 地址不合法：{0}",           "Invalid IPv6 address: {0}");
            A("val.badgw6",   "IPv6 网关不合法：{0}",           "Invalid IPv6 gateway: {0}");
            A("val.dns4a",    "IPv4 DNS 1 必须是 IPv4 地址：{0}", "IPv4 DNS 1 must be an IPv4 address: {0}");
            A("val.dns4b",    "IPv4 DNS 2 必须是 IPv4 地址：{0}", "IPv4 DNS 2 must be an IPv4 address: {0}");
            A("val.dns6a",    "IPv6 DNS 1 必须是 IPv6 地址：{0}", "IPv6 DNS 1 must be an IPv6 address: {0}");
            A("val.dns6b",    "IPv6 DNS 2 必须是 IPv6 地址：{0}", "IPv6 DNS 2 must be an IPv6 address: {0}");

            // ---------------- 应用：日志
            A("log.snap",     "已存快照: {0}",                  "Snapshot saved: {0}");
            A("log.snapfail", "快照写入失败(不影响继续): {0}",   "Snapshot write failed (continuing anyway): {0}");
            A("log.applyhead","---- 开始应用 {0} 条命令 ----",   "---- Applying {0} command(s) ----");
            A("log.verify",   "---- 校验 ----",                 "---- Verification ----");
            A("log.noread",   "回读失败：适配器不见了。",        "Read-back failed: the adapter is gone.");
            A("log.v4now",    "IPv4 现在是 : {0}   来源: {1}",   "IPv4 now: {0}   source: {1}");
            A("log.dns4now",  "DNS4 现在是 : {0}",              "DNS4 now: {0}");
            A("log.dns6now",  "DNS6 现在是 : {0}",              "DNS6 now: {0}");
            A("log.v4ok",     "√ 目标 IPv4 {0} 已生效",         "OK  target IPv4 {0} is active");
            A("log.v4fail",   "× 目标 IPv4 {0} 没有出现，请看上面的 FAIL 行",
                              "FAIL  target IPv4 {0} did not appear — see the FAIL lines above");
            A("log.v6ok",     "√ 目标 IPv6 {0} 已生效",         "OK  target IPv6 {0} is active");
            A("log.v6fail",   "× 目标 IPv6 {0} 没有出现",       "FAIL  target IPv6 {0} did not appear");
            A("log.alldone",  "完成，全部命令成功。",            "Done. All commands succeeded.");
            A("log.partfail", "完成，但有 {0} 条命令失败。",     "Done, but {0} command(s) failed.");

            // ---------------- 恢复 DHCP
            A("dhcp.confirm", "把 [{0}] 全部改回自动获取？\r\n\r\n· IPv4 地址、IPv4 DNS 回到 DHCP\r\n· IPv6 静态地址清掉、IPv6 DNS 回到自动、重新开启路由器通告\r\n\r\n（改之前会自动存快照，随时能用「还原上次快照」退回）",
                              "Reset [{0}] back to automatic?\r\n\r\n- IPv4 address and IPv4 DNS go back to DHCP\r\n- IPv6 static addresses are removed, IPv6 DNS goes back to automatic, router advertisements re-enabled\r\n\r\n(A snapshot is saved first, so Restore Snapshot can always undo it)");
            A("dhcp.head",    "---- 恢复 DHCP {0} 条命令 ----",  "---- Restoring DHCP: {0} command(s) ----");
            A("dhcp.src",     "校验：IPv4 来源 = {0}",           "Check: IPv4 source = {0}");
            A("dhcp.restored","DHCP（已恢复）",                  "DHCP (restored)");
            A("dhcp.still",   "仍是静态！",                      "still static!");
            A("dhcp.done",    "恢复完成。等十几秒让 DHCP 拿到地址。",
                              "Restore complete. Wait ~15 seconds for DHCP to assign an address.");
            A("dhcp.donefail","恢复完成，但有 {0} 条命令失败（多半是本来就没有该地址，可忽略）。",
                              "Restore complete, but {0} command(s) failed (usually a non-existent address — safe to ignore).");

            // ---------------- 还原快照
            A("rs.nosnap",    "这块网卡还没有快照——只有执行过「应用配置」或「一键恢复 DHCP」之后才有。",
                              "No snapshot for this adapter yet. One is created when you use Apply or Restore DHCP.");
            A("rs.confirm",   "要把 [{0}] 还原成下面这样？\r\n\r\nIPv4 来源 : {1}\r\n{2}DNS4      : {3}\r\nDNS6      : {4}\r\nIPv6      : {5}",
                              "Restore [{0}] to this?\r\n\r\nIPv4 source : {1}\r\n{2}DNS4        : {3}\r\nDNS6        : {4}\r\nIPv6        : {5}");
            A("rs.gwline",    "IPv4 网关 : {0}\r\n",             "IPv4 gateway: {0}\r\n");
            A("rs.auto",      "(自动)",                         "(automatic)");
            A("rs.head",      "---- 还原快照 {0} 条命令 ----",   "---- Restoring snapshot: {0} command(s) ----");
            A("rs.done",      "还原完成。",                      "Restore complete.");
            A("rs.donefail",  "还原完成，但有 {0} 条命令失败。",  "Restore complete, but {0} command(s) failed.");

            // ---------------- 连通性
            A("t.head",       "---- 连通性测试 ----",            "---- Connectivity Test ----");
            A("t.v4",         "· IPv4",                         "IPv4");
            A("t.v6",         "· IPv6",                         "IPv6");
            A("t.pinggw",     "    ping 网关 {0} : {1}",         "    ping gateway {0} : {1}");
            A("t.pingdns",    "    ping DNS  {0} : {1}",         "    ping DNS     {0} : {1}");
            A("t.nogw4",      "    没有 IPv4 网关。",            "    No IPv4 gateway.");
            A("t.nogw6",      "    没有 IPv6 网关（默认路由）。", "    No IPv6 gateway (default route).");
            A("t.nodns6",     "    没有 IPv6 DNS。",             "    No IPv6 DNS.");
            A("t.ok",         "通",                            "OK");
            A("t.fail",       "不通",                          "FAIL");
            A("t.net4",       "    ping 223.5.5.5（阿里 DNS）     : {0}",
                              "    ping 223.5.5.5 (AliDNS)      : {0}");
            A("t.net4ok",     "通（外网 IPv4 可达）",            "OK (IPv4 internet reachable)");
            A("t.net6",       "    ping 2400:3200::1（阿里 DNS）  : {0}",
                              "    ping 2400:3200::1 (AliDNS)   : {0}");
            A("t.net6ok",     "通（外网 IPv6 可达）",            "OK (IPv6 internet reachable)");
            A("t.noglobal",   "    提示：这块网卡没有全局 IPv6 地址，下面多半不会通。",
                              "    Note: this adapter has no global IPv6 address; the tests below will likely fail.");
            A("t.globals",    "    本机全局地址：{0}",           "    Local global address(es): {0}");
            A("t.v6off",      "    注意：这块网卡的 IPv6 协议当前是【关闭】状态，点「开启 IPv6」再测。",
                              "    Note: IPv6 is currently DISABLED on this adapter. Click Enable IPv6 first.");

            // ---------------- 崩溃日志
            A("crash.ui",     "UI 线程异常",                    "UI thread exception");
            A("crash.unhandled","未捕获异常",                    "Unhandled exception");
            A("crash.start",  "启动异常",                       "Startup exception");

            // ---------------- 命令行
            A("cli.title",    "NetSwitcher — Windows 网络配置快速切换",
                              "NetSwitcher - Windows Network Configuration Quick Switcher");
            A("cli.noarg",    "  不带参数            打开图形界面",        "  (no arguments)      Open the GUI");
            A("cli.list",     "  --list, -l          列出所有适配器及其当前配置",
                              "  --list, -l          List all adapters and their current configuration");
            A("cli.selftest", "  --selftest          列出适配器，并打印会执行的 netsh 命令（不实际执行）",
                              "  --selftest          List adapters and print the netsh commands that would run (dry run)");
            A("cli.uidump",   "  --uidump            列出界面上每个控件的实际生效字体（验证全局字体用）",
                              "  --uidump            List the effective font of every control (for font verification)");
            A("cli.dns",      "  --dns               列出内置的常用 DNS 预设",
                              "  --dns               List the built-in DNS presets");
            A("cli.out",      "  -o <文件>           把报告同时写成一个 UTF-8 文本文件（方便贴给别人看）",
                              "  -o <file>           Also write the report to a UTF-8 text file");
            A("cli.fonthead", "控件字体清单（验证全局字体）",  "Control font list (global font verification)");
            A("cli.fonttail", "全部都是继承来的。只要不给某个控件单独 set Font，它就会一直跟随窗体。",
                              "All inherited. As long as no control sets Font itself, it follows the form forever.");
            A("cli.dnshead",  "内置常用 DNS 预设（来源: github.com/lalifeier/awesome-public-dns）",
                              "Built-in public DNS presets (source: github.com/lalifeier/awesome-public-dns)");
            A("cli.dnscol",   "{0,-12} {1,-18} {2,-18} {3,-24} {4}",
                              "{0,-14} {1,-16} {2,-16} {3,-28} {4}");
            A("cli.name",     "名称",                          "Name");
            A("cli.v4a",      "IPv4 主",                       "IPv4 primary");
            A("cli.v4b",      "IPv4 备",                       "IPv4 secondary");
            A("cli.v6a",      "IPv6 主",                       "IPv6 primary");
            A("cli.v6b",      "IPv6 备",                       "IPv6 secondary");
            A("cli.dnsnote",  "注：114DNS 官方没有公开 IPv6 解析地址，所以它只有 IPv4。",
                              "Note: 114DNS does not publish an IPv6 resolver address, so it is IPv4-only.");
            A("cli.listhead", "适配器列表",                    "Adapter list");
            A("cli.nolist",   "(没有找到适配器)",              "(no adapters found)");
            A("cli.idx",      "索引 {0}  {1}",                 "Index {0}  {1}");
            A("cli.model",    "        型号   : {0}",          "        Model  : {0}");
            A("cli.state",    "        状态   : {0}",          "        Status : {0}");
            A("cli.v4gw",     "        IPv4网关: {0}",         "        IPv4 GW: {0}");
            A("cli.selftesthead","命令构造自检（不执行）：",     "Command construction self-test (not executed):");
            A("cli.notest",   "(无适配器可测)",                "(no adapter to test)");
            A("cli.target",   "  目标适配器: [{0}] {1}",        "  Target adapter: [{0}] {1}");
            A("cli.maskhead", "掩码换算自检：",                "Mask conversion self-test:");
            A("cli.illegal",  "非法",                          "invalid");
            A("v6.readfail",  "读取 IPv6 绑定状态失败，无法开关。", "Failed to read the IPv6 binding state, so it cannot be toggled.");
            A("rs.head2",     "要把 [{0}] 还原成下面这样？",       "Restore [{0}] to this?");
            A("rs.srcline",   "IPv4 来源 : {0}",                 "IPv4 source : {0}");
            A("rs.v4line",    "IPv4      : {0}",                 "IPv4        : {0}");
            A("rs.dns4line",  "DNS4      : {0}",                 "DNS4        : {0}");
            A("rs.dns6line",  "DNS6      : {0}",                 "DNS6        : {0}");
            A("rs.v6line",    "IPv6      : {0}",                 "IPv6        : {0}");
            A("rs.gw2",       "IPv4 网关 : {0}",                 "IPv4 gateway: {0}");
            A("rs.dhcptag",   "(DHCP)",                          "(DHCP)");
            A("err.writefail","\r\n[写文件失败] {0}\r\n",         "\r\n[write failed] {0}\r\n");
            A("err.crash",    "出错了：\r\n\r\n{0}",              "Something went wrong:\r\n\r\n{0}");
            A("err.fatal",    "程序异常：\r\n\r\n{0}",            "Fatal error:\r\n\r\n{0}");
        }
    }

    // ================================================================ 主窗体

    internal class MainForm : Form
    {
        // ---- 全局字体：只设这一处，所有子控件自动继承
        internal static readonly Font UiFont = new Font("Microsoft YaHei", 9f, FontStyle.Regular);

        // ------------------------------------------------------------ 列网格
        // 中英各一套：英文标签明显更宽（Gateway 57 vs 网关 32、Subnet Mask 84 vs 子网掩码 56），
        // 硬塞进中文那套列宽会把对齐顶坏，所以英文版整体加宽客户区、单独给标签留位。
        // 字段宽度两边相同 —— 它是由「内容」决定的（IP 地址有多长），跟语言无关。
#if EN
        private const int ClientW     = 720;   // 客户区宽 = GroupW + 24
        private const int LblW1       = 83;    // 列1 标签区：IP Address 71 + 12 余量
        private const int LblW2       = 96;    // 列2 标签区：Subnet Mask 84 + 12 余量
        private const int LblW3       = 69;    // 列3 标签区：Gateway 57 + 12 余量
        private const int AdapterCboX = 78;    // Adapter 55 + 余量
        private const int BtnW        = 167;
        private const int BtnLastW    = 165;
#else
        private const int ClientW     = 660;
        private const int LblW1       = 64;
        private const int LblW2       = 74;
        private const int LblW3       = 50;
        private const int AdapterCboX = 72;
        private const int BtnW        = 154;
        private const int BtnLastW    = 144;
#endif
        private const int InnerL  = 14;        // 组内左边距
        private const int FieldW1 = 146;       // 列1 输入框：IPv6 地址文字区实测需 140
        private const int FieldW2 = 132;       // 列2 输入框：掩码下拉文字区实测需 109（含右侧箭头）
        private const int FieldW3 = 122;       // 列3 输入框：预设下拉文字区实测需 94
        private const int ColGap  = 10;        // 列间距
        private const int BtnGap  = 10;        // 按钮间距
        private const int BtnH    = 32;
        private const int RefreshW = 96;
        private const int RefreshX = ClientW - 12 - RefreshW;

        private const int Field1X = InnerL + LblW1;                 // 列1 框 x
        private const int Label2X = Field1X + FieldW1 + ColGap;     // 列2 标签 x
        private const int Field2X = Label2X + LblW2;                // 列2 框 x
        private const int Label3X = Field2X + FieldW2 + ColGap;     // 列3 标签 x
        private const int Field3X = Label3X + LblW3;                // 列3 框 x
        private const int GroupW  = Field3X + FieldW3 + InnerL;     // 组宽
        private const int AdapterCboW = RefreshX - 8 - AdapterCboX;

        // ------------------------------------------------------------ 图标 / 菜单

        /// <summary>
        /// 从嵌入资源里取图标。app.ico 是多尺寸的（16~256），
        /// 这里按需挑一个最接近的大小的，比 ExtractAssociatedIcon 清楚。
        /// </summary>
        internal static Icon LoadAppIcon(int size)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("NetSwitcher.app.ico"))
                {
                    if (s != null) return new Icon(s, new Size(size, size));
                }
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return null; }
        }

        /// <summary>
        /// 从嵌入的 app.ico 里抽出一个尺寸，当作 Bitmap 返回。
        ///
        /// 为什么不用 Icon.ToBitmap()：那个方法对「PNG 压缩的 32bpp 图标」支持有问题，
        /// 实测会渲染成一片彩色雪花。app.ico 里存的正是 PNG 压缩数据，
        /// 所以这里自己解析 ICO 目录、把那一块的 PNG 字节直接喂给 Bitmap。
        /// </summary>
        internal static Bitmap LoadAppPng(int size)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("NetSwitcher.app.ico"))
                {
                    if (s == null) return null;
                    var buf = new MemoryStream();
                    s.CopyTo(buf);
                    byte[] data = buf.ToArray();
                    if (data.Length < 6) return null;

                    int count = BitConverter.ToInt16(data, 4);
                    int best = -1, bestDiff = int.MaxValue;
                    for (int i = 0; i < count; i++)
                    {
                        int off = 6 + i * 16;
                        if (off + 16 > data.Length) break;
                        int w = data[off] == 0 ? 256 : data[off];
                        int diff = Math.Abs(w - size);
                        if (diff < bestDiff) { bestDiff = diff; best = i; }
                    }
                    if (best < 0) return null;

                    int bo = 6 + best * 16;
                    int len = BitConverter.ToInt32(data, bo + 8);
                    int img = BitConverter.ToInt32(data, bo + 12);
                    if (img < 0 || len <= 0 || img + len > data.Length) return null;
                    // PNG 签名 89 50 4E 47
                    if (!(data[img] == 0x89 && data[img + 1] == 0x50
                          && data[img + 2] == 0x4E && data[img + 3] == 0x47)) return null;

                    using (var png = new MemoryStream(data, img, len))
                    using (Bitmap tmp = new Bitmap(png))
                        return new Bitmap(tmp);          // 克隆一份，脱离流
                }
            }
            catch { return null; }
        }

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip();
            // ⚠ 整份代码里唯一一处显式给控件设 Font，而且是必须的：
            //   ToolStrip/MenuStrip 的 Font 默认取 SystemFonts.MenuFont（中文系统上是
            //   "Microsoft YaHei UI"，跟"微软雅黑"是两个不同的字体族），它不跟随窗体的 Font。
            //   不写这一行，菜单就成了整界面唯一字体不一致的控件。
            //   下拉项会从这个 ToolStrip 继承，所以设在这里就够了。
            menu.Font = UiFont;

            var about = new ToolStripMenuItem(L.T("menu.about"));
            about.Click += delegate { ShowAbout(); };
            menu.Items.Add(about);
            return menu;
        }

        private void ShowAbout()
        {
            using (var f = new AboutForm(LoadAppPng(256)))
                f.ShowDialog(this);
        }

        private ComboBox cboAdapters = new ComboBox();
        private Button btnRefresh = new Button();
        private TextBox txtCurrent = new TextBox();

        private TextBox txtV4Ip = new TextBox();
        private ComboBox cmbMask = new ComboBox();
        private TextBox txtV4Gw = new TextBox();
        private TextBox txtDns1 = new TextBox();
        private TextBox txtDns2 = new TextBox();
        private ComboBox cboDns4 = new ComboBox();

        private TextBox txtV6Ip = new TextBox();
        private TextBox txtV6Prefix = new TextBox();
        private TextBox txtV6Gw = new TextBox();
        private TextBox txtV6Dns1 = new TextBox();
        private TextBox txtV6Dns2 = new TextBox();
        private ComboBox cboDns6 = new ComboBox();
        private Button btnV6Toggle = new Button();

        private RadioButton rdoReplace = new RadioButton();
        private RadioButton rdoAppend = new RadioButton();
        private CheckBox chkNoGateway = new CheckBox();

        private Button btnApply = new Button();
        private Button btnDhcp = new Button();
        private Button btnRestore = new Button();
        private Button btnPing = new Button();

        private TextBox txtLog = new TextBox();

        private List<AdapterInfo> _adapters = new List<AdapterInfo>();
        private bool _loading;

        private static string DataDir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSwitcher");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public MainForm()
        {
            // ============ 全局字体就这一行，子控件全部继承
            Font = UiFont;

            Text = L.T("apptitle");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            // 标题栏/任务栏图标。exe 的图标是编译时用 /win32icon 嵌的，
            // 这里再从嵌入资源里取一份给窗体用，避免用 ExtractAssociatedIcon 拿到糊的 32x32。
            Icon appIcon = LoadAppIcon(32);
            if (appIcon != null) Icon = appIcon;

            // ============ 顶部菜单条
            MenuStrip menu = BuildMenu();
            Controls.Add(menu);
            int top = menu.Height;                  // 菜单占掉的高度，下面的布局整体下移

            ClientSize = new Size(ClientW, 774 + top);

            BuildUi(top);
            FillPresetCombos();   // 填充两个「常用 DNS」下拉

            Load += delegate
            {
                ReloadAdapters(false);
                Log(L.T("log.ready"));
                Log(L.T("log.safe"));
            };
        }

        // ------------------------------------------------------------ 布局

        private void BuildUi(int top)
        {
            int y = top + 12;

            // ============ 适配器
            Controls.Add(MkLabel(L.T("adapter"), 12, y + 3));
            cboAdapters.DropDownStyle = ComboBoxStyle.DropDownList;
            cboAdapters.SetBounds(AdapterCboX, y, AdapterCboW, 24);
            cboAdapters.SelectedIndexChanged += OnAdapterChanged;
            Controls.Add(cboAdapters);

            btnRefresh.Text = L.T("refresh");
            btnRefresh.SetBounds(RefreshX, y - 1, RefreshW, 26);
            btnRefresh.Click += delegate { ReloadAdapters(true); };
            Controls.Add(btnRefresh);
            y += 34;

            // ============ 当前状态
            Controls.Add(MkLabel(L.T("status"), 12, y + 3));
            y += 22;

            txtCurrent.Multiline = true;
            txtCurrent.ReadOnly = true;
            txtCurrent.ScrollBars = ScrollBars.Vertical;
            txtCurrent.SetBounds(12, y, GroupW, 112);
            Controls.Add(txtCurrent);
            y += 124;

            // ============ IPv4
            var g4 = new GroupBox();
            g4.Text = "IPv4";
            g4.SetBounds(12, y, GroupW, 128);
            Controls.Add(g4);

            // ---- 统一列网格（IPv4 和 IPv6 共用，这样两组纵向完全对齐）
            // 列1: 标签14  框78  宽146 -> 78..224   文字区 140（IPv6 最长地址实测 132，余 8）
            // 列2: 标签234 框308 宽132 -> 308..440  下拉框文字区 109（掩码 255.255.255.255 实测 101，余 8）
            // 列3: 标签450 框500 宽122 -> 500..622  下拉框文字区  99（预设最长 Cloudflare 实测 68，余 31）
            // 标签最宽占位: 列1 "DNS 1"=53、列2 L.T("f.mask")=64、列3 L.T("f.preset")=40，
            // 每处都留 10~11px 余量，保证不会压到输入框。
            // 注: 列3 的标签从 "常用 DNS"(70) 缩成 L.T("f.preset")(40)，否则第三列挤不出下拉框所需的宽度。
            g4.Controls.Add(MkLabel(L.T("f.address"), InnerL, 29));
            txtV4Ip.SetBounds(Field1X, 26, FieldW1, 23);
            g4.Controls.Add(txtV4Ip);

            g4.Controls.Add(MkLabel(L.T("f.mask"), Label2X, 29));
            cmbMask.DropDownStyle = ComboBoxStyle.DropDown;   // 可编辑：既能选也能手打
            cmbMask.Items.AddRange(new object[] {
                "255.255.255.0","255.255.0.0","255.0.0.0","255.255.255.128","255.255.255.192",
                "255.255.255.224","255.255.255.240","255.255.255.248","255.255.255.252","255.255.255.255"
            });
            cmbMask.SetBounds(Field2X, 26, FieldW2, 23);
            g4.Controls.Add(cmbMask);

            g4.Controls.Add(MkLabel(L.T("f.gateway"), Label3X, 29));
            txtV4Gw.SetBounds(Field3X, 26, FieldW3, 23);
            g4.Controls.Add(txtV4Gw);

            g4.Controls.Add(MkLabel(L.T("f.dns1"), InnerL, 65));
            txtDns1.SetBounds(Field1X, 62, FieldW1, 23);
            g4.Controls.Add(txtDns1);

            g4.Controls.Add(MkLabel(L.T("f.dns2"), Label2X, 65));
            txtDns2.SetBounds(Field2X, 62, FieldW2, 23);
            g4.Controls.Add(txtDns2);

            g4.Controls.Add(MkLabel(L.T("f.preset"), Label3X, 65));
            cboDns4.DropDownStyle = ComboBoxStyle.DropDownList;
            cboDns4.SetBounds(Field3X, 62, FieldW3, 23);
            cboDns4.SelectedIndexChanged += OnDns4Preset;
            g4.Controls.Add(cboDns4);

            g4.Controls.Add(MkHint(L.T("hint.v4"), InnerL, 95));
            y += 128 + 12;

            // ============ IPv6
            var g6 = new GroupBox();
            g6.Text = "IPv6";
            g6.SetBounds(12, y, GroupW, 136);
            Controls.Add(g6);

            // ---- 与 IPv4 完全相同的列网格，保证两组纵向对齐
            g6.Controls.Add(MkLabel(L.T("f.address"), InnerL, 29));
            txtV6Ip.SetBounds(Field1X, 26, FieldW1, 23);
            g6.Controls.Add(txtV6Ip);

            g6.Controls.Add(MkLabel(L.T("f.prefix"), Label2X, 29));
            txtV6Prefix.Text = "64";
            txtV6Prefix.SetBounds(Field2X, 26, FieldW2, 23);
            g6.Controls.Add(txtV6Prefix);

            g6.Controls.Add(MkLabel(L.T("f.gateway"), Label3X, 29));
            txtV6Gw.SetBounds(Field3X, 26, FieldW3, 23);
            g6.Controls.Add(txtV6Gw);

            g6.Controls.Add(MkLabel(L.T("f.dns1"), InnerL, 65));
            txtV6Dns1.SetBounds(Field1X, 62, FieldW1, 23);
            g6.Controls.Add(txtV6Dns1);

            g6.Controls.Add(MkLabel(L.T("f.dns2"), Label2X, 65));
            txtV6Dns2.SetBounds(Field2X, 62, FieldW2, 23);
            g6.Controls.Add(txtV6Dns2);

            g6.Controls.Add(MkLabel(L.T("f.preset"), Label3X, 65));
            cboDns6.DropDownStyle = ComboBoxStyle.DropDownList;
            cboDns6.SetBounds(Field3X, 62, FieldW3, 23);
            cboDns6.SelectedIndexChanged += OnDns6Preset;
            g6.Controls.Add(cboDns6);

            btnV6Toggle.Text = L.T("v6.busy");
            btnV6Toggle.Enabled = false;
            btnV6Toggle.SetBounds(14, 94, 130, 26);
            btnV6Toggle.Click += delegate { ToggleIpv6(); };
            g6.Controls.Add(btnV6Toggle);

            g6.Controls.Add(MkHint(L.T("hint.v6"), 156, 99));
            y += 136 + 12;

            // ============ 应用方式
            var gm = new GroupBox();
            gm.Text = L.T("g.mode");
            gm.SetBounds(12, y, GroupW, 60);
            Controls.Add(gm);

            rdoReplace.Text = L.T("m.replace");
            rdoReplace.Checked = true;
            rdoReplace.SetBounds(14, 24, 180, 22);
            gm.Controls.Add(rdoReplace);

            rdoAppend.Text = L.T("m.append");
            rdoAppend.SetBounds(204, 24, 180, 22);
            gm.Controls.Add(rdoAppend);

            chkNoGateway.Text = L.T("m.nogw");
            chkNoGateway.SetBounds(394, 24, 180, 22);
            chkNoGateway.CheckedChanged += delegate
            {
                txtV4Gw.Enabled = !chkNoGateway.Checked;
                txtV6Gw.Enabled = !chkNoGateway.Checked;
            };
            gm.Controls.Add(chkNoGateway);
            y += 60 + 14;

            // ============ 按钮
            btnApply.Text = L.T("b.apply");
            btnApply.SetBounds(12, y, BtnW, BtnH);
            btnApply.Click += delegate { Apply(); };
            Controls.Add(btnApply);

            btnDhcp.Text = L.T("b.dhcp");
            btnDhcp.SetBounds(12 + BtnW + BtnGap, y, BtnW, BtnH);
            btnDhcp.Click += delegate { RestoreDhcp(); };
            Controls.Add(btnDhcp);

            btnRestore.Text = L.T("b.restore");
            btnRestore.SetBounds(12 + 2 * (BtnW + BtnGap), y, BtnW, BtnH);
            btnRestore.Click += delegate { RestoreSnapshot(); };
            Controls.Add(btnRestore);

            btnPing.Text = L.T("b.test");
            btnPing.SetBounds(12 + 3 * (BtnW + BtnGap), y, BtnLastW, BtnH);
            btnPing.Click += delegate { TestConnectivity(); };
            Controls.Add(btnPing);
            y += 32 + 14;

            // ============ 日志
            Controls.Add(MkLabel(L.T("b.log"), 12, y + 3));
            y += 22;

            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.SetBounds(12, y, GroupW, 140);
            Controls.Add(txtLog);
        }

        private static Label MkLabel(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            // 透明背景：万一标签的自动尺寸顶到了隔壁输入框，也只盖住文字，
            // 不会糊掉别人的边框（默认的 Control 灰底会）。
            l.BackColor = Color.Transparent;
            l.Location = new Point(x, y);
            return l;
        }

        private static Label MkHint(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.BackColor = Color.Transparent;
            l.ForeColor = SystemColors.GrayText;
            l.Location = new Point(x, y);
            return l;
        }

        /// <summary>
        /// 仅用于「对齐测试版」：把每个框都填成最长的典型内容，
        /// 用来看排版有没有把文字裁掉。生产版没有这个方法。
        /// </summary>
        public void FillDemoValues()
        {
            txtV4Ip.Text = "192.168.100.200";
            cmbMask.Text = "255.255.255.255";
            txtV4Gw.Text = "192.168.1.1";
            txtDns1.Text = "114.114.115.115";
            txtDns2.Text = "223.6.6.6";
            txtV6Ip.Text = "2001:4860:4860::8888";
            txtV6Prefix.Text = "128";
            txtV6Gw.Text = "fe80::1";
            txtV6Dns1.Text = "2606:4700:4700::1111";
            txtV6Dns2.Text = "2400:3200:baba::1";
        }

        // ------------------------------------------------------------ 基础

        private void Log(string s)
        {
            txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + "\r\n");
        }

        private string SnapPath(int idx) { return Path.Combine(DataDir, "snap-" + idx + ".txt"); }
        private string LastPath(int idx) { return Path.Combine(DataDir, "last-" + idx + ".txt"); }

        private int PrefixValue
        {
            get
            {
                int p;
                if (int.TryParse(txtV6Prefix.Text.Trim(), out p) && p >= 0 && p <= 128) return p;
                return 64;
            }
        }

        private AdapterInfo Selected
        {
            get
            {
                int i = cboAdapters.SelectedIndex;
                if (i < 0 || i >= _adapters.Count) return null;
                return _adapters[i];
            }
        }

        private void ReloadAdapters(bool keepSelection)
        {
            int want = -1;
            AdapterInfo cur = Selected;
            if (keepSelection && cur != null) want = cur.Index;

            _adapters = Adapters.List();
            _loading = true;
            cboAdapters.Items.Clear();
            foreach (AdapterInfo a in _adapters) cboAdapters.Items.Add(a);

            if (_adapters.Count == 0)
            {
                _loading = false;
                txtCurrent.Text = L.T("status.no");
                Log(L.T("log.noadapter"));
                return;
            }

            int pick = 0;
            if (want >= 0)
            {
                for (int i = 0; i < _adapters.Count; i++) if (_adapters[i].Index == want) { pick = i; break; }
            }
            cboAdapters.SelectedIndex = pick;
            _loading = false;
            OnAdapterChanged(null, null);
        }

        private void OnAdapterChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            AdapterInfo a = Selected;
            if (a == null) { txtCurrent.Text = ""; return; }

            var sb = new StringBuilder();
            sb.AppendLine(a.Name + "   ·   " + a.StatusText + "   ·   " + L.T("word.index") + " " + a.Index);
            sb.AppendLine(a.Description);
            sb.AppendLine("IPv4  " + (a.V4.Count == 0 ? L.T("word.none") : string.Join(", ", a.V4.ToArray()))
                        + "   ·   " + (a.V4Dhcp ? L.T("word.dhcp") : L.T("word.static")));
            string v6 = "";
            foreach (string v in a.V6) if (!v.ToLowerInvariant().StartsWith("fe80")) v6 += (v6.Length == 0 ? "" : ", ") + v;
            sb.AppendLine(L.T("word.gw") + "  " + (a.V4Gw.Count == 0 ? L.T("word.none") : string.Join(", ", a.V4Gw.ToArray()))
                        + "   ·   IPv6  " + (v6.Length == 0 ? L.T("word.noglobal") : v6));
            sb.AppendLine("DNS4  " + (a.Dns4.Count == 0 ? L.T("word.none") : string.Join(", ", a.Dns4.ToArray()))
                        + "   ·   DNS6  " + (a.Dns6.Count == 0 ? L.T("word.none") : string.Join(", ", a.Dns6.ToArray())));
            txtCurrent.Text = sb.ToString();

            Snapshot last = Snapshot.Load(LastPath(a.Index));
            if (last != null)
            {
                txtV4Ip.Text = FirstOrEmpty(last.V4);
                if (last.V4.Count > 0)
                {
                    int slash = last.V4[0].IndexOf('/');
                    if (slash > 0)
                    {
                        int p;
                        if (int.TryParse(last.V4[0].Substring(slash + 1), out p))
                            cmbMask.Text = IpUtil.PrefixToMask(p);
                    }
                }
                txtV4Gw.Text = FirstOrEmpty(last.V4Gw);
                txtDns1.Text = last.Dns4.Count > 0 ? last.Dns4[0] : "";
                txtDns2.Text = last.Dns4.Count > 1 ? last.Dns4[1] : "";
                txtV6Ip.Text = FirstOrEmpty(last.V6);
                txtV6Gw.Text = FirstOrEmpty(last.V6Gw);
                txtV6Dns1.Text = last.Dns6.Count > 0 ? last.Dns6[0] : "";
                txtV6Dns2.Text = last.Dns6.Count > 1 ? last.Dns6[1] : "";
            }

            RefreshV6Button();
        }

        private static string FirstOrEmpty(List<string> l)
        {
            if (l == null || l.Count == 0) return "";
            string s = l[0];
            int slash = s.IndexOf('/');
            return slash > 0 ? s.Substring(0, slash) : s;
        }

        // ------------------------------------------------------------ DNS 预设

        // 预设下拉的第一项固定是「恢复自动获取」：选它就把两个 DNS 输入框清空，
        // 应用配置时执行 netsh ... set dnsservers source=dhcp，
        // 也就是这一族 DNS 改回从上级路由器自动获取。
        private static readonly string PresetAuto = L.T("p.auto");

        private void OnDns4Preset(object sender, EventArgs e)
        {
            if (_loading) return;
            int i = cboDns4.SelectedIndex;
            if (i < 0) return;
            if (i == 0)
            {
                txtDns1.Text = "";
                txtDns2.Text = "";
                Log(L.T("p.pick.auto.v4"));
                return;
            }
            DnsPreset p = Dns.All[i - 1];
            txtDns1.Text = p.V4a;
            txtDns2.Text = p.V4b;
            Log(L.F("p.fill.v4", p.Name, p.V4a + (p.V4b.Length > 0 ? " / " + p.V4b : "")));
        }

        private void OnDns6Preset(object sender, EventArgs e)
        {
            if (_loading) return;
            int i = cboDns6.SelectedIndex;
            if (i < 0) return;
            if (i == 0)
            {
                txtV6Dns1.Text = "";
                txtV6Dns2.Text = "";
                Log(L.T("p.pick.auto.v6"));
                return;
            }
            DnsPreset p = _v6Presets[i - 1];
            txtV6Dns1.Text = p.V6a;
            txtV6Dns2.Text = p.V6b;
            Log(L.F("p.fill.v6", p.Name, p.V6a + (p.V6b.Length > 0 ? " / " + p.V6b : "")));
        }

        private List<DnsPreset> _v6Presets = new List<DnsPreset>();

        private void FillPresetCombos()
        {
            _loading = true;

            cboDns4.Items.Clear();
            cboDns4.Items.Add(PresetAuto);
            foreach (DnsPreset p in Dns.All) if (p.V4a.Length > 0) cboDns4.Items.Add(p.Name);
            cboDns4.SelectedIndex = 0;        // 默认停在第 0 项「恢复自动获取」

            _v6Presets.Clear();
            cboDns6.Items.Clear();
            cboDns6.Items.Add(PresetAuto);
            foreach (DnsPreset p in Dns.All)
            {
                if (p.V6a.Length == 0) continue;      // 没有 IPv6 地址的厂商不进 v6 列表
                _v6Presets.Add(p);
                cboDns6.Items.Add(p.Name);
            }
            cboDns6.SelectedIndex = 0;

            _loading = false;
        }

        // ------------------------------------------------------------ IPv6 协议开关

        private static bool? QueryV6Binding(int idx)
        {
            string o;
            bool ok = Shell.Ps("Get-NetAdapter -InterfaceIndex " + idx
                             + " | Get-NetAdapterBinding -ComponentID ms_tcpip6"
                             + " | Select-Object -ExpandProperty Enabled", out o);
            if (!ok) return null;
            o = o.Trim().ToLowerInvariant();
            if (o.StartsWith("true")) return true;
            if (o.StartsWith("false")) return false;
            return null;
        }

        private void RefreshV6Button()
        {
            AdapterInfo a = Selected;
            if (a == null) { btnV6Toggle.Text = L.T("v6.unknown"); btnV6Toggle.Enabled = false; return; }
            bool? en = QueryV6Binding(a.Index);
            if (en == null) { btnV6Toggle.Text = L.T("v6.unknown"); btnV6Toggle.Enabled = false; return; }
            btnV6Toggle.Enabled = true;
            btnV6Toggle.Text = en.Value ? L.T("v6.off") : L.T("v6.on");
        }

        private void ToggleIpv6()
        {
            AdapterInfo a = Selected;
            if (a == null) { Warn(L.T("common.pick")); return; }

            bool? cur = QueryV6Binding(a.Index);
            if (cur == null)
            {
                Warn(L.T("v6.readfail") + "\r\n\r\n" + L.T("v6.needwin8"));
                return;
            }

            bool turnOff = cur.Value;
            if (!Confirm(L.F("v6.confirm",
                turnOff ? L.T("v6.offword") : L.T("v6.onword"), a.Name,
                turnOff ? L.T("v6.uncheck") : L.T("v6.check")))) return;

            string verb = turnOff ? "Disable" : "Enable";
            string outp;
            bool ok = Shell.Ps("Get-NetAdapter -InterfaceIndex " + a.Index
                             + " | " + verb + "-NetAdapterBinding -ComponentID ms_tcpip6", out outp);

            Log(L.F("v6.loghead", turnOff ? L.T("v6.offword") : L.T("v6.onword")));
            Log((ok ? "OK   " : "FAIL ") + "powershell " + verb + "-NetAdapterBinding -ComponentID ms_tcpip6"
                + (outp.Length > 0 ? "\r\n        " + outp.Replace("\r\n", " / ") : ""));

            System.Threading.Thread.Sleep(800);
            RefreshV6Button();
            if (ok)
            {
                Log(turnOff ? L.T("v6.done.off") : L.T("v6.done.on"));
                ReloadAdapters(true);
            }
        }

        // ------------------------------------------------------------ 应用

        private void Apply()
        {
            AdapterInfo a = Selected;
            if (a == null) { Warn(L.T("common.pick")); return; }

            string v4 = txtV4Ip.Text.Trim();
            string maskIn = cmbMask.Text.Trim();
            string v4gw = chkNoGateway.Checked ? "" : txtV4Gw.Text.Trim();
            string v6 = txtV6Ip.Text.Trim();
            string v6gw = chkNoGateway.Checked ? "" : txtV6Gw.Text.Trim();
            string d41 = txtDns1.Text.Trim();
            string d42 = txtDns2.Text.Trim();
            string d61 = txtV6Dns1.Text.Trim();
            string d62 = txtV6Dns2.Text.Trim();
            int v6prefix = PrefixValue;

            bool doV4 = v4.Length > 0;
            bool doV6 = v6.Length > 0;
            // DNS 的三种意图，按优先级判断：
            //   1) 输入框里有地址                -> 写静态 DNS
            //   2) 框是空的 + 下拉停在第 0 项「恢复自动获取」 -> 改回自动获取（从上级路由器拿）
            //   3) 框是空的 + 下拉停在别的预设项    -> 说明是选完预设又手工清了框，什么都不做
            bool dns4Static = d41.Length > 0 || d42.Length > 0;
            bool dns6Static = d61.Length > 0 || d62.Length > 0;
            bool dns4Auto = !dns4Static && cboDns4.SelectedIndex == 0;
            bool dns6Auto = !dns6Static && cboDns6.SelectedIndex == 0;

            if (!doV4 && !doV6 && !dns4Static && !dns6Static && !dns4Auto && !dns6Auto)
            {
                Warn(L.T("val.empty"));
                return;
            }

            int prefix = -1;
            string mask = null;
            if (doV4)
            {
                if (!IpUtil.IsV4(v4)) { Warn(L.F("val.badv4", v4)); return; }
                if (maskIn.Length == 0) { Warn(L.T("val.nomask")); return; }
                mask = IpUtil.NormalizeMask(maskIn, out prefix);
                if (mask == null || prefix < 0) { Warn(L.F("val.badmask", maskIn)); return; }
                if (v4gw.Length > 0)
                {
                    if (!IpUtil.IsV4(v4gw)) { Warn(L.F("val.badgw4", v4gw)); return; }
                    if (!IpUtil.SameSubnet(IPAddress.Parse(v4), IPAddress.Parse(v4gw), prefix))
                    {
                        if (!Confirm(L.F("val.gwsubnet", v4gw, v4 + "/" + prefix)))
                            return;
                    }
                }
                if (v4gw.Length > 0 && v4gw == v4) { Warn(L.T("val.gwsame")); return; }
                byte lastByte = IPAddress.Parse(v4).GetAddressBytes()[3];
                if (lastByte == 0 || lastByte == 255)
                {
                    if (!Confirm(L.F("val.lastbyte", v4))) return;
                }
            }
            if (doV6)
            {
                if (!IpUtil.IsV6(v6)) { Warn(L.F("val.badv6", v6)); return; }
                if (v6gw.Length > 0 && !IpUtil.IsV6(v6gw)) { Warn(L.F("val.badgw6", v6gw)); return; }
            }
            if (dns4Static)
            {
                if (d41.Length > 0 && !IpUtil.IsV4(d41)) { Warn(L.F("val.dns4a", d41)); return; }
                if (d42.Length > 0 && !IpUtil.IsV4(d42)) { Warn(L.F("val.dns4b", d42)); return; }
            }
            if (dns6Static)
            {
                if (d61.Length > 0 && !IpUtil.IsV6(d61)) { Warn(L.F("val.dns6a", d61)); return; }
                if (d62.Length > 0 && !IpUtil.IsV6(d62)) { Warn(L.F("val.dns6b", d62)); return; }
            }

            try
            {
                Snapshot.Capture(a).Save(SnapPath(a.Index));
                Log(L.F("log.snap", SnapPath(a.Index)));
            }
            catch (Exception ex) { Log(L.F("log.snapfail", ex.Message)); }

            var cmds = new List<string>();
            int idx = a.Index;

            if (doV4)
            {
                if (rdoReplace.Checked)
                {
                    cmds.Add(string.Format("interface ipv4 set address name={0} source=static address={1} mask={2} gateway={3} gwmetric=1",
                        idx, v4, mask, v4gw.Length > 0 ? v4gw : "none"));
                    foreach (string old in a.V4)
                    {
                        string oldIp = old;
                        int s = oldIp.IndexOf('/');
                        if (s > 0) oldIp = oldIp.Substring(0, s);
                        if (oldIp == v4) continue;
                        if (oldIp.StartsWith("169.254.")) continue;
                        cmds.Add(string.Format("interface ipv4 delete address name={0} address={1}", idx, oldIp));
                    }
                }
                else
                {
                    cmds.Add(string.Format("interface ipv4 add address name={0} address={1} mask={2}", idx, v4, mask));
                }
            }

            if (doV6)
            {
                if (rdoReplace.Checked)
                {
                    cmds.Add(string.Format("interface ipv6 set interface interface={0} routerdiscovery=disabled", idx));
                    foreach (string old in a.V6)
                    {
                        string oldIp = old;
                        int s = oldIp.IndexOf('/');
                        if (s > 0) oldIp = oldIp.Substring(0, s);
                        if (oldIp.ToLowerInvariant().StartsWith("fe80")) continue;
                        if (oldIp.ToLowerInvariant().StartsWith("::1")) continue;
                        cmds.Add(string.Format("interface ipv6 delete address interface={0} address={1}", idx, oldIp));
                    }
                    cmds.Add(string.Format("interface ipv6 add address interface={0} address={1}/{2}", idx, v6, v6prefix));
                    if (v6gw.Length > 0)
                        cmds.Add(string.Format("interface ipv6 add route prefix=::/0 interface={0} nexthop={1} metric=1", idx, v6gw));
                }
                else
                {
                    cmds.Add(string.Format("interface ipv6 add address interface={0} address={1}/{2}", idx, v6, v6prefix));
                }
            }

            // DNS4：有地址就写静态，否则若预设停在「恢复自动获取」就改回自动获取
            if (dns4Static) AppendDns(cmds, "ipv4", idx, d41, d42);
            else if (dns4Auto)
                cmds.Add(string.Format("interface ipv4 set dnsservers name={0} source=dhcp", idx));

            // DNS6：同理
            if (dns6Static) AppendDns(cmds, "ipv6", idx, d61, d62);
            else if (dns6Auto)
                cmds.Add(string.Format("interface ipv6 set dnsservers name={0} source=dhcp", idx));

            Log(L.F("log.applyhead", cmds.Count));
            int fail = 0;
            foreach (string c in cmds)
            {
                NetshResult r = Shell.Netsh(c);
                bool ok = Benign(r) || BestEffort(c);
                if (!ok) fail++;
                Log((ok ? "OK   " : "FAIL ") + "netsh " + c + (r.Output.Length > 0 ? "\r\n        " + r.Output.Replace("\r\n", " / ") : ""));
            }

            try
            {
                var rec = new Snapshot();
                rec.Adapter = a.Name;
                rec.Index = idx;
                if (doV4) rec.V4.Add(v4 + "/" + prefix);
                if (doV4 && v4gw.Length > 0) rec.V4Gw.Add(v4gw);
                if (doV6) rec.V6.Add(v6 + "/" + v6prefix);
                if (doV6 && v6gw.Length > 0) rec.V6Gw.Add(v6gw);
                if (d41.Length > 0) rec.Dns4.Add(d41);
                if (d42.Length > 0) rec.Dns4.Add(d42);
                if (d61.Length > 0) rec.Dns6.Add(d61);
                if (d62.Length > 0) rec.Dns6.Add(d62);
                rec.Save(LastPath(idx));
            }
            catch { }

            System.Threading.Thread.Sleep(1500);
            ReloadAdapters(true);

            AdapterInfo after = Adapters.ByIndex(idx);
            Log(L.T("log.verify"));
            if (after == null) Log(L.T("log.noread"));
            else
            {
                Log(L.F("log.v4now", after.V4.Count == 0 ? L.T("word.none") : string.Join(", ", after.V4.ToArray()), after.V4Dhcp ? "DHCP" : L.T("word.static")));
                Log(L.F("log.dns4now", after.Dns4.Count == 0 ? L.T("word.none") : string.Join(", ", after.Dns4.ToArray())));
                Log(L.F("log.dns6now", after.Dns6.Count == 0 ? L.T("word.none") : string.Join(", ", after.Dns6.ToArray())));
                if (doV4)
                {
                    bool hit = false;
                    foreach (string s in after.V4) if (s.StartsWith(v4 + "/")) hit = true;
                    Log(hit ? L.F("log.v4ok", v4) : L.F("log.v4fail", v4));
                }
                if (doV6)
                {
                    bool hit = false;
                    foreach (string s in after.V6) if (s.ToLowerInvariant().StartsWith(v6.ToLowerInvariant() + "/")) hit = true;
                    Log(hit ? L.F("log.v6ok", v6) : L.F("log.v6fail", v6));
                }
            }
            Log(fail == 0 ? L.T("log.alldone") : L.F("log.partfail", fail));
        }

        /// <summary>拼 IPv4 / IPv6 的 DNS 命令。family 是 "ipv4" 或 "ipv6"。</summary>
        private static void AppendDns(List<string> cmds, string family, int idx, string first, string second)
        {
            var list = new List<string>();
            if (first.Length > 0) list.Add(first);
            if (second.Length > 0) list.Add(second);
            if (list.Count == 0) return;

            cmds.Add(string.Format("interface {0} set dnsservers name={1} source=static address={2} register=primary validate=no",
                family, idx, list[0]));
            for (int i = 1; i < list.Count; i++)
                cmds.Add(string.Format("interface {0} add dnsservers name={1} address={2} index={3} validate=no",
                    family, idx, list[i], i + 1));
        }

        // ------------------------------------------------------------ 恢复 DHCP

        private void RestoreDhcp()
        {
            AdapterInfo a = Selected;
            if (a == null) { Warn(L.T("common.pick")); return; }
            if (!Confirm(L.F("dhcp.confirm", a.Name))) return;

            try { Snapshot.Capture(a).Save(SnapPath(a.Index)); Log(L.F("log.snap", SnapPath(a.Index))); }
            catch (Exception ex) { Log(L.F("log.snapfail", ex.Message)); }

            int idx = a.Index;
            var cmds = new List<string>();
            cmds.Add(string.Format("interface ipv4 set address name={0} source=dhcp", idx));
            cmds.Add(string.Format("interface ipv4 set dnsservers name={0} source=dhcp", idx));
            cmds.Add(string.Format("interface ipv6 set dnsservers name={0} source=dhcp", idx));
            foreach (string old in a.V6)
            {
                string oldIp = old;
                int s = oldIp.IndexOf('/');
                if (s > 0) oldIp = oldIp.Substring(0, s);
                if (oldIp.ToLowerInvariant().StartsWith("fe80")) continue;
                if (oldIp.ToLowerInvariant().StartsWith("::1")) continue;
                cmds.Add(string.Format("interface ipv6 delete address interface={0} address={1}", idx, oldIp));
            }
            cmds.Add(string.Format("interface ipv6 delete route prefix=::/0 interface={0}", idx));
            cmds.Add(string.Format("interface ipv6 set interface interface={0} routerdiscovery=enabled", idx));

            Log(L.F("dhcp.head", cmds.Count));
            int fail = 0;
            foreach (string c in cmds)
            {
                NetshResult r = Shell.Netsh(c);
                bool ok = Benign(r) || BestEffort(c);
                if (!ok) fail++;
                Log((ok ? "OK   " : "FAIL ") + "netsh " + c + (r.Output.Length > 0 ? "\r\n        " + r.Output.Replace("\r\n", " / ") : ""));
            }

            System.Threading.Thread.Sleep(2000);
            ReloadAdapters(true);
            AdapterInfo after = Adapters.ByIndex(idx);
            if (after != null)
                Log(L.F("dhcp.src", after.V4Dhcp ? L.T("dhcp.restored") : L.T("dhcp.still")));
            Log(fail == 0 ? L.T("dhcp.done") : L.F("dhcp.donefail", fail));
        }

        // ------------------------------------------------------------ 还原快照

        private void RestoreSnapshot()
        {
            AdapterInfo a = Selected;
            if (a == null) { Warn(L.T("common.pick")); return; }
            Snapshot s = Snapshot.Load(SnapPath(a.Index));
            if (s == null) { Warn(L.T("rs.nosnap")); return; }

            var sb = new StringBuilder();
            sb.AppendLine(L.F("rs.head2", a.Name));
            sb.AppendLine();
            sb.AppendLine(L.F("rs.srcline", s.V4Dhcp ? L.T("word.dhcp") : L.T("word.static")));
            if (!s.V4Dhcp)
            {
                sb.AppendLine(L.F("rs.v4line", string.Join(", ", s.V4.ToArray())));
                sb.AppendLine(L.F("rs.gw2", s.V4Gw.Count == 0 ? L.T("word.none") : string.Join(", ", s.V4Gw.ToArray())));
            }
            sb.AppendLine(L.F("rs.dns4line", s.Dns4.Count == 0 ? L.T("rs.dhcptag") : string.Join(", ", s.Dns4.ToArray())));
            sb.AppendLine(L.F("rs.dns6line", s.Dns6.Count == 0 ? L.T("rs.auto") : string.Join(", ", s.Dns6.ToArray())));
            sb.AppendLine(L.F("rs.v6line", s.V6.Count == 0 ? L.T("rs.auto") : string.Join(", ", s.V6.ToArray())));
            if (!Confirm(sb.ToString())) return;

            int idx = a.Index;
            var cmds = new List<string>();
            if (s.V4Dhcp)
            {
                cmds.Add(string.Format("interface ipv4 set address name={0} source=dhcp", idx));
            }
            else if (s.V4.Count > 0)
            {
                string first = s.V4[0];
                string ip = first;
                int prefix = 24;
                int s1 = first.IndexOf('/');
                if (s1 > 0) { ip = first.Substring(0, s1); int.TryParse(first.Substring(s1 + 1), out prefix); }
                string mask = IpUtil.PrefixToMask(prefix);
                if (mask == null) mask = "255.255.255.0";
                string gw = s.V4Gw.Count > 0 ? s.V4Gw[0] : "none";
                cmds.Add(string.Format("interface ipv4 set address name={0} source=static address={1} mask={2} gateway={3} gwmetric=1",
                    idx, ip, mask, gw));
            }

            if (s.Dns4.Count == 0)
                cmds.Add(string.Format("interface ipv4 set dnsservers name={0} source=dhcp", idx));
            else
                AppendDns(cmds, "ipv4", idx, s.Dns4[0], s.Dns4.Count > 1 ? s.Dns4[1] : "");

            if (s.Dns6.Count == 0)
                cmds.Add(string.Format("interface ipv6 set dnsservers name={0} source=dhcp", idx));
            else
                AppendDns(cmds, "ipv6", idx, s.Dns6[0], s.Dns6.Count > 1 ? s.Dns6[1] : "");

            if (s.V6.Count == 0)
            {
                cmds.Add(string.Format("interface ipv6 set interface interface={0} routerdiscovery=enabled", idx));
            }
            else
            {
                cmds.Add(string.Format("interface ipv6 set interface interface={0} routerdiscovery=disabled", idx));
                foreach (string v in s.V6)
                {
                    string ip = v;
                    int p = 64;
                    int s2 = v.IndexOf('/');
                    if (s2 > 0) { ip = v.Substring(0, s2); int.TryParse(v.Substring(s2 + 1), out p); }
                    cmds.Add(string.Format("interface ipv6 add address interface={0} address={1}/{2}", idx, ip, p));
                }
            }

            Log(L.F("rs.head", cmds.Count));
            int fail = 0;
            foreach (string c in cmds)
            {
                NetshResult r = Shell.Netsh(c);
                bool ok = Benign(r) || BestEffort(c);
                if (!ok) fail++;
                Log((ok ? "OK   " : "FAIL ") + "netsh " + c + (r.Output.Length > 0 ? "\r\n        " + r.Output.Replace("\r\n", " / ") : ""));
            }
            System.Threading.Thread.Sleep(1500);
            ReloadAdapters(true);
            Log(fail == 0 ? L.T("rs.done") : L.F("rs.donefail", fail));
        }

        // ------------------------------------------------------------ 连通性

        private void TestConnectivity()
        {
            AdapterInfo a = Selected;
            if (a == null) { Warn(L.T("common.pick")); return; }
            Log(L.T("t.head"));

            // ============ IPv4
            Log(L.T("t.v4"));
            if (a.V4Gw.Count > 0)
            {
                foreach (string gw in a.V4Gw)
                    Log(L.F("t.pinggw", Pad(gw, 26), Shell.Ping(gw, 1500) ? L.T("t.ok") : L.T("t.fail")));
            }
            else Log(L.T("t.nogw4"));
            if (a.Dns4.Count > 0)
                Log(L.F("t.pingdns", Pad(a.Dns4[0], 26), Shell.Ping(a.Dns4[0], 1500) ? L.T("t.ok") : L.T("t.fail")));
            Log(L.F("t.net4", Shell.Ping("223.5.5.5", 2000) ? L.T("t.net4ok") : L.T("t.fail")));

            // ============ IPv6
            Log(L.T("t.v6"));

            // 先看有没有全局地址 —— 没有的话后面怎么 ping 都不通，先把原因说清楚
            string v6global = "";
            foreach (string v in a.V6)
                if (!v.ToLowerInvariant().StartsWith("fe80")) v6global += (v6global.Length == 0 ? "" : ", ") + v;
            if (v6global.Length == 0)
                Log(L.T("t.noglobal"));
            else
                Log(L.F("t.globals", v6global));

            // 网关通常是链路本地地址（带 %区域号），.NET 的 Ping 认这种写法
            if (a.V6Gw.Count > 0)
            {
                foreach (string gw in a.V6Gw)
                    Log(L.F("t.pinggw", Pad(gw, 26), Shell.Ping(gw, 1500) ? L.T("t.ok") : L.T("t.fail")));
            }
            else Log(L.T("t.nogw6"));

            if (a.Dns6.Count > 0)
                Log(L.F("t.pingdns", Pad(a.Dns6[0], 26), Shell.Ping(a.Dns6[0], 1500) ? L.T("t.ok") : L.T("t.fail")));
            else Log(L.T("t.nodns6"));

            // 外网 IPv6 用阿里公共 DNS 的 IPv6 地址，和上面 IPv4 用的 223.5.5.5 是同一家
            Log(L.F("t.net6", Shell.Ping("2400:3200::1", 2500) ? L.T("t.net6ok") : L.T("t.fail")));

            // IPv6 协议绑定被关掉的话上面全不通，直接点出来省得猜
            bool? bound = QueryV6Binding(a.Index);
            if (bound.HasValue && !bound.Value)
                Log(L.T("t.v6off"));
        }

        /// <summary>日志框里对齐用：不足 len 就补空格。</summary>
        private static string Pad(string s, int len)
        {
            if (s == null) s = "";
            if (s.Length >= len) return s;
            return s + new string(' ', len - s.Length);
        }

        // ------------------------------------------------------------ 小工具

        private static bool Benign(NetshResult r)
        {
            if (r.Ok) return true;
            string o = r.Output == null ? "" : r.Output.ToLowerInvariant();
            if (o.Contains("already")) return true;
            if (r.Output != null && r.Output.Contains(L.T("common.already"))) return true;
            return false;
        }

        private static bool BestEffort(string cmd)
        {
            return cmd.StartsWith("interface ipv4 delete") || cmd.StartsWith("interface ipv6 delete");
        }

        private void Warn(string s)
        {
            Log("！" + s.Replace("\r\n", " "));
            MessageBox.Show(this, s, "NetSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private bool Confirm(string s)
        {
            return MessageBox.Show(this, s, L.T("common.ok"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }
    }

    // ================================================================ 关于

    internal class AboutForm : Form
    {
        public AboutForm(Bitmap art)
        {
            // 跟主窗体用同一个字体对象，不单独给任何子控件设 Font
            Font = MainForm.UiFont;
            Icon appIcon = MainForm.LoadAppIcon(16);
            if (appIcon != null) Icon = appIcon;

            Text = L.T("menu.about");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 440);

            if (art != null)
            {
                var pic = new PictureBox();
                pic.Image = art;
                pic.SizeMode = PictureBoxSizeMode.StretchImage;
                pic.SetBounds(24, 26, 80, 80);
                Controls.Add(pic);
            }

            // ---- 标题：英文在上，中文在下
            Add("NetSwitcher", 120, 28);
            Add("Windows Network Configuration Quick Switcher", 120, 50);
            Add("Windows 网络配置快速切换器", 120, 70);

            Sep(24, 112, 392, 1);

            // ---- 中英对照
            Add("Developed with DeepSeek V4.1 FLASH", 24, 124);
            Add("使用 DeepSeek V4.1 FLASH 开发", 24, 144);

            Add("AI is changing the world.", 24, 168);
            Add("AI 正在改变世界。", 24, 188);

            Sep(24, 214, 392, 1);

            Add("Copyright © Feng-Studio0595. All rights reserved.", 24, 226);
            Add("版权所有 © Feng-Studio0595。保留所有权利。", 24, 246);

            Add("Built by a router tinkerer with zero programming background.", 24, 270);
            Add("由一位完全没有编程基础的路由器爱好者打造。", 24, 290);

            Sep(24, 314, 392, 1);

            // ---- 图标素材署名（MIT 协议要求的版权声明，别删）
            Add("Icon © 2026 MeteorNOX — MIT License", 24, 326);
            Add("图标素材 © 2026 MeteorNOX，MIT 协议", 24, 346);
            Add("Unofficial · 非官方项目，与 DeepSeek 无关联", 24, 370);

            var ok = new Button();
            ok.Text = L.T("common.yes");
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(336, 398, 80, 28);
            Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = ok;
        }

        private void Add(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.BackColor = Color.Transparent;
            l.Location = new Point(x, y);
            Controls.Add(l);
        }

        private void Sep(int x, int y, int w, int h)
        {
            var p = new Panel();
            p.BackColor = SystemColors.ControlDark;
            p.SetBounds(x, y, w, h);
            Controls.Add(p);
        }
    }

    // ================================================================ 入口

    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                string c = args[0].ToLowerInvariant();
                if (c == "--list" || c == "-l" || c == "--selftest" || c == "--uidump" || c == "--dns"
                    || c == "--demo" || c == "--about"
                    || c == "--help" || c == "-h" || c == "/?")
                {
                    if (c == "--demo")
                    {
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        MainForm demo = new MainForm();
                        demo.FillDemoValues();
                        Application.Run(demo);
                        return;
                    }
                    if (c == "--about")
                    {
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        using (AboutForm ab = new AboutForm(MainForm.LoadAppPng(256)))
                            ab.ShowDialog();
                        return;
                    }
                    string outPath = null;
                    for (int i = 1; i < args.Length - 1; i++)
                        if (args[i] == "-o" || args[i] == "--out") outPath = args[i + 1];

                    var buf = new StringWriter();
                    Console.SetOut(buf);
                    Cli(c);
                    string text = buf.ToString();

                    if (outPath != null)
                    {
                        try { File.WriteAllText(outPath, text, new UTF8Encoding(true)); }
                        catch (Exception ex) { text += L.F("err.writefail", ex.Message); }
                    }

                    if (!AttachConsole(-1)) AllocConsole();
                    var so = new StreamWriter(Console.OpenStandardOutput());
                    so.AutoFlush = true;
                    Console.SetOut(so);
                    Console.Write(text);
                    Console.Out.Flush();
                    return;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs te)
            {
                CrashLog(L.T("crash.ui"), te.Exception);
                MessageBox.Show(L.F("err.crash", te.Exception), "NetSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs ue)
            {
                CrashLog(L.T("crash.unhandled"), ue.ExceptionObject as Exception);
            };
            try { Application.Run(new MainForm()); }
            catch (Exception ex)
            {
                CrashLog(L.T("crash.start"), ex);
                MessageBox.Show(L.F("err.fatal", ex), "NetSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void CrashLog(string tag, Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "netswitcher-crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + tag + "]\r\n" + ex + "\r\n\r\n",
                    new UTF8Encoding(true));
            }
            catch { }
        }

        private static void DumpFonts(Control root, string indent)
        {
            foreach (Control c in root.Controls)
            {
                string text = c.Text == null ? "" : c.Text;
                if (text.Length > 18) text = text.Substring(0, 18) + "…";
                Console.WriteLine("{0}{1,-16} Font = {2,-16} {3,5}pt   [{4}]",
                    indent, c.GetType().Name, c.Font.Name, c.Font.Size, text);
                DumpFonts(c, indent + "  ");
            }
        }

        private static void Cli(string cmd)
        {
            if (cmd == "--help" || cmd == "-h" || cmd == "/?")
            {
                Console.WriteLine(L.T("cli.title"));
                Console.WriteLine();
                Console.WriteLine(L.T("cli.noarg"));
                Console.WriteLine(L.T("cli.list"));
                Console.WriteLine(L.T("cli.selftest"));
                Console.WriteLine(L.T("cli.uidump"));
                Console.WriteLine(L.T("cli.dns"));
                Console.WriteLine(L.T("cli.out"));
                Console.WriteLine();
                return;
            }

            if (cmd == "--uidump")
            {
                Console.WriteLine(L.T("cli.fonthead"));
                Console.WriteLine(new string('-', 78));
                MainForm f = new MainForm();
                Console.WriteLine("{0,-18} Font = {1} {2}pt", "Form", f.Font.Name, f.Font.Size);
                DumpFonts(f, "  ");
                Console.WriteLine();
                Console.WriteLine(L.T("cli.fonttail"));
                return;
            }

            if (cmd == "--dns")
            {
                Console.WriteLine(L.T("cli.dnshead"));
                Console.WriteLine(new string('-', 78));
                Console.WriteLine(L.T("cli.dnscol"), L.T("cli.name"), L.T("cli.v4a"), L.T("cli.v4b"), L.T("cli.v6a"), L.T("cli.v6b"));
                foreach (DnsPreset p in Dns.All)
                {
                    Console.WriteLine("{0,-12} {1,-18} {2,-18} {3,-24} {4}",
                        p.Name, p.V4a, p.V4b.Length == 0 ? "-" : p.V4b,
                        p.V6a.Length == 0 ? "-" : p.V6a, p.V6b.Length == 0 ? "-" : p.V6b);
                }
                Console.WriteLine();
                Console.WriteLine(L.T("cli.dnsnote"));
                return;
            }

            Console.WriteLine(L.T("cli.listhead"));
            Console.WriteLine(new string('-', 78));
            List<AdapterInfo> list = Adapters.List();
            if (list.Count == 0) Console.WriteLine(L.T("cli.nolist"));
            foreach (AdapterInfo a in list)
            {
                Console.WriteLine(L.F("cli.idx", a.Index, a.Name));
                Console.WriteLine(L.F("cli.model", a.Description));
                Console.WriteLine(L.F("cli.state", a.Status));
                Console.WriteLine("        IPv4   : {0}   [{1}]",
                    a.V4.Count == 0 ? L.T("word.none") : string.Join(", ", a.V4.ToArray()),
                    a.V4Dhcp ? "DHCP" : L.T("word.static"));
                Console.WriteLine(L.F("cli.v4gw", a.V4Gw.Count == 0 ? L.T("word.none") : string.Join(", ", a.V4Gw.ToArray())));
                string v6 = "";
                foreach (string v in a.V6) if (!v.ToLowerInvariant().StartsWith("fe80")) v6 += (v6.Length == 0 ? "" : ", ") + v;
                Console.WriteLine("        IPv6   : {0}", v6.Length == 0 ? L.T("word.noglobal") : v6);
                Console.WriteLine("        DNS4   : {0}", a.Dns4.Count == 0 ? L.T("word.none") : string.Join(", ", a.Dns4.ToArray()));
                Console.WriteLine("        DNS6   : {0}", a.Dns6.Count == 0 ? L.T("word.none") : string.Join(", ", a.Dns6.ToArray()));
                Console.WriteLine();
            }

            if (cmd == "--selftest")
            {
                Console.WriteLine(new string('-', 78));
                Console.WriteLine(L.T("cli.selftesthead"));
                AdapterInfo first = null;
                foreach (AdapterInfo a in list) { if (a.Status == OperationalStatus.Up) { first = a; break; } }
                if (first == null && list.Count > 0) first = list[0];
                if (first == null) { Console.WriteLine(L.T("cli.notest")); return; }
                int idx = first.Index;
                Console.WriteLine(L.F("cli.target", idx, first.Name));
                Console.WriteLine();
                Console.WriteLine("  netsh interface ipv4 set address name={0} source=static address=192.168.1.2 mask=255.255.255.0 gateway=192.168.1.1 gwmetric=1", idx);
                Console.WriteLine("  netsh interface ipv4 add address name={0} address=192.168.1.2 mask=255.255.255.0", idx);
                Console.WriteLine("  netsh interface ipv4 delete address name={0} address=192.168.1.2", idx);
                Console.WriteLine("  netsh interface ipv4 set address name={0} source=dhcp", idx);
                Console.WriteLine("  netsh interface ipv4 set dnsservers name={0} source=static address=223.5.5.5 register=primary validate=no", idx);
                Console.WriteLine("  netsh interface ipv4 add dnsservers name={0} address=223.6.6.6 index=2 validate=no", idx);
                Console.WriteLine("  netsh interface ipv4 set dnsservers name={0} source=dhcp", idx);
                Console.WriteLine("  netsh interface ipv6 set dnsservers name={0} source=static address=2400:3200::1 register=primary validate=no", idx);
                Console.WriteLine("  netsh interface ipv6 add dnsservers name={0} address=2400:3200:baba::1 index=2 validate=no", idx);
                Console.WriteLine("  netsh interface ipv6 set dnsservers name={0} source=dhcp", idx);
                Console.WriteLine("  netsh interface ipv6 add address interface={0} address=fd00::2/64", idx);
                Console.WriteLine("  netsh interface ipv6 add route prefix=::/0 interface={0} nexthop=fd00::1 metric=1", idx);
                Console.WriteLine("  netsh interface ipv6 set interface interface={0} routerdiscovery=enabled", idx);
                Console.WriteLine();
                Console.WriteLine("  powershell Get-NetAdapter -InterfaceIndex {0} | Get-NetAdapterBinding -ComponentID ms_tcpip6 | Select-Object -ExpandProperty Enabled", idx);
                Console.WriteLine("  powershell Get-NetAdapter -InterfaceIndex {0} | Disable-NetAdapterBinding -ComponentID ms_tcpip6", idx);
                Console.WriteLine("  powershell Get-NetAdapter -InterfaceIndex {0} | Enable-NetAdapterBinding -ComponentID ms_tcpip6", idx);
                Console.WriteLine();
                Console.WriteLine(L.T("cli.maskhead"));
                string[] tests = { "255.255.255.0", "/24", "255.255.0.0", "/16", "255.255.255.252", "/30", "255.255.255.1", "abc" };
                foreach (string t in tests)
                {
                    int p;
                    string m = IpUtil.NormalizeMask(t, out p);
                    Console.WriteLine("  {0,-18} -> {1}", t, m == null ? L.T("cli.illegal") : (m + "  =/" + p));
                }
            }
        }
    }
}
