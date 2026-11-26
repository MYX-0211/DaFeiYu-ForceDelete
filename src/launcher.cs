// ============================================================================
//  DeepSeek 宿主（C# 版，替代仓库里的 Go 宿主）
//
//  职责：
//    1. 把内嵌的 DeepSeek.ps1 / Ui.cs / logo.ico / bg.png 释放到
//       %LOCALAPPDATA%\DeepSeek\（内容未变则复用）
//    2. 用 CREATE_NO_WINDOW 拉起 powershell，保证 GUI 启动全程不闪黑窗
//    3. 右键菜单 -Add 走快速路径：直接写队列文件，毫秒级返回，不起进程
//    4. 已有窗口时再次双击 exe -> 激活原窗口，不重复开
//    5. -Install / -Uninstall / -SelfTest / -Probe / -Pending 前台执行并呈现结果
//
//  编译：build.ps1（csc.exe，目标 .NET Framework 4.x，Win10/11 自带）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeepSeek
{
    public static class Launcher
    {
        private const string AppName = "DeepSeek";
        private const string AppTitle = "大肥鱼 · 强制删除工具";
        private const string MutexName = @"Local\DeepSeek_Gui";
        private const string Version = "2026.11.26";

        // ---------------- Win32 ----------------

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string cls, string title);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleOutputCP(uint codePage);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, byte[] buffer, uint numberOfBytesToWrite, out uint numberOfBytesWritten, IntPtr lpOverlapped);

        // 有没有可用的标准输出。双击运行时没有（返回空句柄）；从终端、脚本、
        // 构建流程里调用时有。用来决定命令行模式的结果是写出去还是弹框。
        private static bool HasStdOut()
        {
            try
            {
                IntPtr h = GetStdHandle(-11);   // STD_OUTPUT_HANDLE
                if (h == IntPtr.Zero || h == new IntPtr(-1)) { return false; }
                return true;
            }
            catch { return false; }
        }

        // 把文本按 UTF-8 直接写到标准输出句柄。不经过 Console：重定向到文件时并没有控制台，
        // 而 Console 的编码设置恰恰依赖控制台。写不出去返回 false，由调用方改弹框。
        private static bool TryWriteStdOut(string text)
        {
            IntPtr h;
            try { h = GetStdHandle(-11); } catch { return false; }
            if (h == IntPtr.Zero || h == new IntPtr(-1)) { return false; }
            try { SetConsoleOutputCP(65001); } catch { }   // 真有控制台时让它按 UTF-8 显示中文；没有控制台时失败无所谓
            byte[] buf = Encoding.UTF8.GetBytes(text);
            uint done;
            bool ok = WriteFile(h, buf, (uint)buf.Length, out done, IntPtr.Zero);
            return ok && done == (uint)buf.Length;
        }

        // ---------------- 入口 ----------------

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MigrateLegacy();

            string addPath = null;
            bool forceNew = false;
            List<string> rest = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "-add" || a == "/add")
                {
                    if (i + 1 < args.Length)
                    {
                        addPath = args[i + 1];
                        i++;
                    }
                }
                else if (a == "-forcenew" || a == "/forcenew")
                {
                    forceNew = true;
                }
                else
                {
                    rest.Add(args[i]);
                }
            }

            string script = ResolveScript();
            if (script == null)
            {
                MsgBox("无法准备运行环境：脚本释放失败。\r\n请检查 %LOCALAPPDATA%\\DeepSeek 目录是否可写。", true);
                return 1;
            }

            // 右键菜单：已有窗口就只写队列，否则写队列并冷启动界面
            if (addPath != null)
            {
                if (!AddToQueue(addPath)) { return 0; }
                if (GuiRunning()) { return 0; }
                LaunchGui(script, false);
                return 0;
            }

            // 无参数：显示界面 / 激活已有窗口
            if (rest.Count == 0)
            {
                if (forceNew)
                {
                    WaitGuiGone(3000);
                    LaunchGui(script, false);
                    return 0;
                }
                if (GuiRunning())
                {
                    ActivateExisting();
                    return 0;
                }
                LaunchGui(script, false);
                return 0;
            }

            return RunConsole(script, rest.ToArray());
        }

        // ---------------- 路径 ----------------

        private static string ExePath()
        {
            try { return Application.ExecutablePath; }
            catch { return ""; }
        }

        private static string ExeDir()
        {
            string p = ExePath();
            if (p.Length == 0) { return ""; }
            try { return Path.GetDirectoryName(p); }
            catch { return ""; }
        }

        private static string DataRoot()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) { baseDir = Path.GetTempPath(); }
            return Path.Combine(baseDir, AppName);
        }

        private static string QueueFile()
        {
            return Path.Combine(DataRoot(), "queue.txt");
        }

        private static string LogFile()
        {
            return Path.Combine(DataRoot(), AppName + ".log");
        }

        // ---------------- 内嵌资源 ----------------

        private static byte[] LoadRes(string name)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string full = null;
            string[] names = asm.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    full = names[i];
                    break;
                }
            }
            if (full == null) { return null; }
            using (Stream st = asm.GetManifestResourceStream(full))
            {
                if (st == null) { return null; }
                byte[] buf = new byte[st.Length];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = st.Read(buf, read, buf.Length - read);
                    if (n <= 0) { break; }
                    read += n;
                }
                return buf;
            }
        }

        private static string Hash(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) { sb.Append(h[i].ToString("x2")); }
                return sb.ToString();
            }
        }

        /// <summary>释放内嵌资源，内容未变则跳过写入。</summary>
        private static string Extract(string resName, string fileName)
        {
            byte[] data = LoadRes(resName);
            if (data == null) { return null; }
            string root = DataRoot();
            try
            {
                Directory.CreateDirectory(root);
                string dst = Path.Combine(root, fileName);
                if (File.Exists(dst))
                {
                    try
                    {
                        byte[] cur = File.ReadAllBytes(dst);
                        if (cur.Length == data.Length && Hash(cur) == Hash(data)) { return dst; }
                    }
                    catch { }
                }
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        File.WriteAllBytes(dst, data);
                        return dst;
                    }
                    catch
                    {
                        // 多个进程同时首次启动（资源管理器多选后一次加入多项）会撞车：
                        // 对方可能正在写同一个文件。等一下再试；对方写完了就直接复用。
                        try
                        {
                            byte[] cur = File.ReadAllBytes(dst);
                            if (cur.Length == data.Length && Hash(cur) == Hash(data)) { return dst; }
                        }
                        catch { }
                        Thread.Sleep(150);
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>优先用 exe 同目录的 DeepSeek.ps1（便于直接改脚本调试），否则用内嵌副本。</summary>
        private static string ResolveScript()
        {
            string dir = ExeDir();
            if (dir.Length > 0)
            {
                string cand = Path.Combine(dir, "DeepSeek.ps1");
                try
                {
                    if (File.Exists(cand) && new FileInfo(cand).Length > 512) { return cand; }
                }
                catch { }
            }
            return Extract("ps1", "DeepSeek.ps1");
        }

        private static string PsExe()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (root.Length > 0)
            {
                string p = Path.Combine(root, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (File.Exists(p)) { return p; }
            }
            return "powershell.exe";
        }

        // ---------------- 进程 ----------------

        private static void LaunchGui(string script, bool wait)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(PsExe());
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -STA -File \"" + script + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;      // 关键：完全不创建控制台，不闪黑窗
                psi.WorkingDirectory = Path.GetDirectoryName(script);

                psi.EnvironmentVariables["WINDEL_HOST_EXE"] = ExePath();
                string ico = Extract("ico", "DeepSeek.ico");
                if (ico != null) { psi.EnvironmentVariables["WINDEL_ICON"] = ico; }
                string bg = Extract("bg", "bg.png");
                if (bg != null) { psi.EnvironmentVariables["WINDEL_BG"] = bg; }
                string ui = Extract("ui", "DeepSeek.Ui.cs");
                if (ui != null) { psi.EnvironmentVariables["WINDEL_UI"] = ui; }

                Process p = Process.Start(psi);
                if (wait && p != null) { p.WaitForExit(); }
                if (p != null) { Log("🪟 界面进程已拉起 pid=" + p.Id + " 脚本=" + script); }
            }
            catch (Exception ex)
            {
                Log("❌ 启动界面失败：" + ex.Message);
                MsgBox("启动界面失败：\r\n" + ex.Message, true);
            }
        }

        private static int RunConsole(string script, string[] args)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("-NoProfile -ExecutionPolicy Bypass -STA -File \"");
            sb.Append(script);
            sb.Append("\"");
            for (int i = 0; i < args.Length; i++)
            {
                sb.Append(' ');
                string a = args[i];
                if (a.IndexOf(' ') >= 0 || a.IndexOf('"') >= 0) { sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"'); }
                else { sb.Append(a); }
            }

            string output = "";
            int code = 0;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(PsExe());
                psi.Arguments = sb.ToString();
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                psi.WorkingDirectory = Path.GetDirectoryName(script);
                psi.EnvironmentVariables["WINDEL_HOST_EXE"] = ExePath();
                string ico = Extract("ico", "DeepSeek.ico");
                if (ico != null) { psi.EnvironmentVariables["WINDEL_ICON"] = ico; }

                using (Process p = Process.Start(psi))
                {
                    // 先读完标准输出再读错误输出会死锁：子进程把错误管道写满后会阻塞，
                    // 而它还要等标准输出这边。错误输出放到另一个线程读。
                    string errText = "";
                    Thread errReader = new Thread(delegate()
                    {
                        try { errText = p.StandardError.ReadToEnd(); } catch { }
                    });
                    errReader.IsBackground = true;
                    errReader.Start();

                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    errReader.Join();
                    code = p.ExitCode;
                    output = (o + errText).Trim();
                }
            }
            catch (Exception ex)
            {
                MsgBox("无法启动 PowerShell：\r\n" + ex.Message, true);
                return 1;
            }

            if (output.Length == 0) { return code; }

            string saveAs = Path.Combine(DataRoot(), "last-output.txt");
            try
            {
                Directory.CreateDirectory(DataRoot());
                File.WriteAllText(saveAs, output, Encoding.UTF8);
            }
            catch { }

            // 有标准输出（从终端、脚本、构建流程里调用）：原样写出去就返回，不弹窗。
            // 以前不管有没有控制台都弹系统对话框等用户点确定，脚本里调用会一直挂着。
            if (HasStdOut() && TryWriteStdOut(output + Environment.NewLine)) { return code; }

            // 双击运行、没有控制台：才用对话框呈现
            string shown = output;
            if (shown.Length > 1200)
            {
                shown = "……（输出过长，已截断，完整内容见）……\r\n" + shown.Substring(shown.Length - 1200);
            }
            shown += "\r\n\r\n完整输出：" + saveAs;
            MsgBox(shown, false);
            return code;
        }

        // ---------------- 单实例 ----------------

        private static bool GuiRunning()
        {
            try
            {
                using (Mutex m = Mutex.OpenExisting(MutexName)) { return true; }
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch { return false; }
        }

        private static void WaitGuiGone(int timeoutMs)
        {
            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                if (!GuiRunning()) { return; }
                Thread.Sleep(60);
                elapsed += 60;
            }
        }

        private static void ActivateExisting()
        {
            try
            {
                IntPtr h = FindWindowW(null, AppTitle);
                if (h == IntPtr.Zero) { return; }
                if (IsIconic(h)) { ShowWindow(h, 9); }
                SetForegroundWindow(h);
            }
            catch { }
        }

        private static void MsgBox(string text, bool error)
        {
            try
            {
                // 弹给用户看的标题用软件名，不用内部代号
                MessageBox.Show(text, AppTitle, MessageBoxButtons.OK,
                    error ? MessageBoxIcon.Error : MessageBoxIcon.Information);
            }
            catch { }
        }

        // ---------------- 队列（与脚本内 Add-QueuePath 保持一致） ----------------

        // 旧版本的数据目录：WinDel 是更早的名字，CC-DEL 是上游的名字。
        // 升级换名不该把用户攒了一路的待删列表弄丢。
        private static readonly string[] LegacyDirs = { "WinDel", "CC-DEL" };

        private static void MigrateLegacy()
        {
            try
            {
                if (File.Exists(QueueFile())) { return; }
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(baseDir)) { return; }
                for (int i = 0; i < LegacyDirs.Length; i++)
                {
                    string legacy = Path.Combine(baseDir, LegacyDirs[i], "queue.txt");
                    if (!File.Exists(legacy)) { continue; }
                    Directory.CreateDirectory(DataRoot());
                    File.Copy(legacy, QueueFile(), false);
                    Log("📦 已迁移旧版本的待删列表：" + legacy);
                    return;
                }
            }
            catch { }
        }

        private static List<string> ReadQueue()
        {
            List<string> list = new List<string>();
            try
            {
                if (!File.Exists(QueueFile())) { return list; }
                string s = File.ReadAllText(QueueFile(), Encoding.UTF8);
                if (s.StartsWith("\uFEFF")) { s = s.Substring(1); }
                string[] lines = s.Split(new char[] { '\n' });
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].TrimEnd('\r');
                    if (line.Trim().Length > 0) { list.Add(line); }
                }
            }
            catch { }
            return list;
        }

        private static void WriteQueue(List<string> items)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                sb.Append(items[i]);
                sb.Append("\r\n");
            }
            File.WriteAllText(QueueFile(), sb.ToString(), new UTF8Encoding(false));
        }

        private static string Normalize(string p)
        {
            p = p.Trim();
            p = p.Trim('"');
            p = p.Trim();
            if (p.Length == 0) { return ""; }
            if (p.Length == 3 && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) { return p; }
            if (p.Length == 2 && p[1] == ':') { return p; }
            return p.TrimEnd('\\');
        }

        private static bool IsReservedName(string name)
        {
            string b = name;
            int i = b.IndexOf('.');
            if (i >= 0) { b = b.Substring(0, i); }
            b = b.TrimEnd(' ').ToLowerInvariant();
            switch (b)
            {
                case "con":
                case "prn":
                case "aux":
                case "nul":
                case "ncl":
                case "com1": case "com2": case "com3": case "com4": case "com5":
                case "com6": case "com7": case "com8": case "com9":
                case "lpt1": case "lpt2": case "lpt3": case "lpt4": case "lpt5":
                case "lpt6": case "lpt7": case "lpt8": case "lpt9":
                    return true;
                default:
                    return false;
            }
        }

        private static string ExtendedPath(string p)
        {
            if (p.StartsWith(@"\\?\")) { return p; }
            if (p.StartsWith(@"\\")) { return @"\\?\UNC\" + p.Substring(2); }
            if (!Path.IsPathRooted(p))
            {
                try
                {
                    string abs = Path.GetFullPath(p);
                    if (!abs.StartsWith(@"\.\")) { p = abs; }
                }
                catch { }
            }
            return @"\\?\" + p;
        }

        private static bool PathExists(string p)
        {
            bool reserved = false;
            try { reserved = IsReservedName(Path.GetFileName(p)); } catch { }
            if (!reserved)
            {
                try { if (File.Exists(p) || Directory.Exists(p)) { return true; } } catch { }
            }
            try
            {
                string ep = ExtendedPath(p);
                if (File.Exists(ep) || Directory.Exists(ep)) { return true; }
            }
            catch { }
            return false;
        }

        private static bool AddToQueue(string raw)
        {
            string p = Normalize(raw);
            if (p.Length == 0) { return false; }
            if (!PathExists(p))
            {
                Log("⚠️ 路径不存在，已忽略：" + p);
                return false;
            }
            // 资源管理器多选后每一项各起一个进程，「读-改-写」必须串行，
            // 否则后写的会把先写的覆盖掉，表现为加入 5 个只剩 3 个。
            bool owned = false;
            using (Mutex gate = new Mutex(true, @"Local\DeepSeek_QueueLock", out owned))
            {
                try { gate.WaitOne(5000); } catch { }
                try
                {
                    List<string> items = ReadQueue();
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (string.Equals(items[i], p, StringComparison.OrdinalIgnoreCase))
                        {
                            Log("ℹ️ 已在列表中：" + p);
                            return false;
                        }
                    }
                    items.Add(p);
                    WriteQueue(items);
                }
                catch (Exception ex) { Log("❌ 写入队列失败：" + ex.Message); return false; }
                finally
                {
                    try { gate.ReleaseMutex(); } catch { }
                }
            }
            Log("➕ 加入列表：" + p);
            return true;
        }

        private static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(DataRoot());
                File.AppendAllText(LogFile(),
                    "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n",
                    Encoding.UTF8);
            }
            catch { }
        }
    }
}
