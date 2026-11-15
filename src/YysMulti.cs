using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 阴阳师多开器
//   双击             -> 共 2 个
//   多开器 3         -> 共 3 个
//   多开器 /close    -> 关掉全部
//   多开器 /arrange  -> 只重排当前桌面的窗口
//   多开器 /plan 6   -> 只计算版面并写日志，不启动游戏
//   多开器 /keep 3   -> 只启动，不排版
//   多开器 /launcher -> 第一个实例改走 Launch.exe（会提权，排版会失效，仅用于需要更新时）
//
// 为什么默认不用 Launch.exe：
//   Launch.exe 的清单是 requireAdministrator，它拉起的客户端会继承管理员完整性级别，
//   普通权限的多开器对它调用 SetWindowPos 会返回 ERROR_ACCESS_DENIED（UIPI），
//   结果就是那一个窗口永远停在默认位置和尺寸，把其它窗口挤乱。
//   直连 bin\onmyoji.exe 是普通权限，可以正常移动和缩放。
//
// 排版策略：客户端会自己修正窗口尺寸（不同实例修正结果还不一样），所以不再硬套 16:9 网格，
//   而是先尽量把窗口设成目标格子大小，再按“每行最大高度”逐个打包，保证互不重叠。

static class Program
{
    // ---------------- Win32 ----------------
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_SHOWWINDOW = 0x0040;
    const int ERROR_ACCESS_DENIED = 5;

    // ---------------- 版面参数 ----------------
    const int Gap = 8;
    const int MinCellW = 860;
    const int MinCellH = 484;

    // ---------------- 虚拟桌面 COM ----------------
    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr h, out int onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr h, out Guid id);
        [PreserveSig] int MoveWindowToDesktop(IntPtr h, ref Guid id);
    }

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);

    static IVirtualDesktopManager _vdm;
    static IVirtualDesktopManager Vdm()
    {
        if (_vdm != null) return _vdm;
        var clsid = new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A");
        var iid = new Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B");
        IntPtr p;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out p) != 0) return null;
        _vdm = (IVirtualDesktopManager)Marshal.GetObjectForIUnknown(p);
        return _vdm;
    }

    // ---------------- 路径 ----------------
    static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string Launcher = Path.Combine(Root, "Launch.exe");
    static readonly string Client = Path.Combine(Root, "bin", "onmyoji.exe");
    static readonly string ClientAlt = Path.Combine(Root, "bin", "onmyoji_future.exe");
    static readonly string LogPath = Path.Combine(Root, "YysMulti-log.txt");

    static bool _accessDeniedLogged;

    static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + msg + Environment.NewLine,
                new UTF8Encoding(true));
        }
        catch { }
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Log("--- 启动 " + string.Join(" ", args) + " ---");

            if (Has(args, "/close", "--close", "-close")) { CloseAll(); return 0; }

            if (Has(args, "/plan", "--plan"))
            {
                PlanReport(ParseCount(args, 2));
                return 0;
            }

            if (Has(args, "/arrange", "--arrange"))
            {
                var list = CurrentDesktopWindows();
                Log("重新排版: " + list.Count + " 个窗口");
                if (list.Count > 0) Arrange(list);
                return 0;
            }

            bool useLauncher = Has(args, "/launcher", "--launcher");

            if (!File.Exists(Client) && !File.Exists(ClientAlt) && !File.Exists(Launcher))
            {
                Log("找不到 bin\\onmyoji.exe 或 Launch.exe");
                MessageBox.Show("没找到游戏：\r\n" + Client + "\r\n" + Launcher +
                    "\r\n\r\n请把本程序放在阴阳师客户端根目录。", "阴阳师多开器",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            bool keep = Has(args, "/keep", "--keep");
            int forcedCapacity = ParseSwitch(args, "/capacity", 0);
            bool forceVd = Has(args, "/forcevd", "--forcevd");

            int count = ParseCount(args, 2);
            if (count > 12) count = 12;

            int capacity = forcedCapacity > 0 ? forcedCapacity : Capacity();
            var existing = RunningPids();
            int need = count - existing.Count;

            Log("已有实例: " + existing.Count + "，目标: " + count + "，单屏容量: " + capacity +
                (useLauncher ? "，首个用启动器" : ""));

            if (need <= 0)
            {
                Log("实例数已够，不再启动");
                if (!keep)
                {
                    var cur = CurrentDesktopWindows();
                    if (cur.Count > 0) Arrange(cur);
                }
                return 0;
            }

            bool useVd = false;
            if (count > capacity)
            {
                if (forceVd) useVd = true;
                else
                {
                    int extra = count - capacity;
                    var r = MessageBox.Show(
                        "当前屏幕最多能舒适放下 " + capacity + " 个实例。\r\n\r\n" +
                        "你要开 " + count + " 个，多出的 " + extra + " 个可以放到新的虚拟桌面：\r\n" +
                        "程序会自动新建桌面、把实例开在那边，然后切回来。\r\n" +
                        "期间屏幕会切换几次，请不要操作。\r\n\r\n" +
                        "是 = 使用虚拟桌面    否 = 只在当前桌面开 " + capacity + " 个",
                        "阴阳师多开器  " + count + " 开",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                    useVd = (r == DialogResult.Yes);
                }
                if (!useVd)
                {
                    count = capacity;
                    need = count - existing.Count;
                    Log("不用虚拟桌面，实例数改为 " + count);
                    if (need <= 0) return 0;
                }
            }

            int groupIndex = 0;
            int done = 0;
            Guid homeDesktop = Guid.Empty;
            IntPtr homeAnchor = IntPtr.Zero;
            while (done < need)
            {
                int room = capacity - (groupIndex == 0 ? existing.Count : 0);
                if (room < 1) room = capacity;

                bool onNewDesktop = false;
                if (groupIndex > 0)
                {
                    onNewDesktop = CreateDesktop(homeAnchor);
                    if (!onNewDesktop) Log("无法新建虚拟桌面，本组仍开在当前桌面（会更挤）");
                }

                int take = Math.Min(room, need - done);
                Log("第 " + (groupIndex + 1) + " 组: 启动 " + take + " 个");

                var handles = new List<IntPtr>();
                for (int i = 0; i < take; i++)
                {
                    bool launcherThis = useLauncher && groupIndex == 0 && done == 0 && i == 0 &&
                                        existing.Count == 0 && File.Exists(Launcher);
                    int pid = StartInstance(launcherThis, existing);
                    if (pid > 0)
                    {
                        IntPtr h = WaitWindow(pid, 200);
                        if (h != IntPtr.Zero)
                        {
                            handles.Add(h);
                            if (groupIndex == 0 && homeAnchor == IntPtr.Zero) homeAnchor = h;
                            Guid d = DesktopOf(h);
                            if (homeDesktop == Guid.Empty) homeDesktop = d;
                            else if (groupIndex > 0 && d == homeDesktop)
                                Log("提示: 这个窗口仍落在原桌面（热键可能被拦截）");
                            else if (groupIndex > 0)
                                Log("确认窗口在新桌面: " + d);
                        }
                        else Log("PID " + pid + " 200 秒内没有可用窗口");
                    }
                    if (i < take - 1) Thread.Sleep(12000);
                }

                if (handles.Count > 0 && !keep)
                {
                    WaitStable(handles, 30);
                    Arrange(handles);
                }

                if (onNewDesktop) SwitchBack(homeAnchor);

                done += take;
                groupIndex++;
            }

            // 最后把当前桌面上的所有实例统一排一次（新开的 + 已有的），避免新旧窗口尺寸不一致
            if (!keep)
            {
                var all = CurrentDesktopWindows();
                if (all.Count > 1)
                {
                    Log("统一排版 " + all.Count + " 个窗口");
                    WaitStable(all, 15);
                    Arrange(all);
                }
            }

            Log("全部完成");
            return 0;
        }
        catch (Exception ex)
        {
            Log("异常: " + ex);
            return 1;
        }
    }

    // ---------------- 参数 ----------------
    static bool Has(string[] a, params string[] keys)
    {
        foreach (var s in a)
            foreach (var k in keys)
                if (string.Equals(s, k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static int ParseCount(string[] a, int def)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (i > 0 && string.Equals(a[i - 1], "/capacity", StringComparison.OrdinalIgnoreCase)) continue;
            int v;
            if (int.TryParse(a[i], out v)) return Math.Max(1, v);
        }
        return def;
    }

    static int ParseSwitch(string[] a, string key, int def)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (string.Equals(a[i], key, StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(a[i + 1], out v)) return v;
            }
        return def;
    }

    // ---------------- 实例 ----------------
    static HashSet<int> RunningPids()
    {
        var set = new HashSet<int>();
        foreach (var name in new[] { "onmyoji", "onmyoji_future" })
            foreach (var p in Process.GetProcessesByName(name)) set.Add(p.Id);
        return set;
    }

    static Process StartProcess(string exe)
    {
        try { return Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Root, UseShellExecute = true }); }
        catch (Exception ex) { Log("启动失败 " + exe + ": " + ex.Message); return null; }
    }

    static int StartInstance(bool useLauncher, HashSet<int> known)
    {
        if (useLauncher)
        {
            Log("首个实例走 Launch.exe（会以管理员权限运行，可能无法排版）");
            StartProcess(Launcher);
            int lpid = WaitNewClient(known, 200);
            if (lpid > 0) { known.Add(lpid); Log("启动器已拉起客户端 PID=" + lpid); return lpid; }
            Log("启动器没拉起客户端，改为直连");
        }
        string exe = File.Exists(Client) ? Client : ClientAlt;
        var p = StartProcess(exe);
        if (p == null) return 0;
        known.Add(p.Id);
        Log("直连客户端 PID=" + p.Id);
        return p.Id;
    }

    static int WaitNewClient(HashSet<int> known, int seconds)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            foreach (var name in new[] { "onmyoji", "onmyoji_future" })
                foreach (var p in Process.GetProcessesByName(name))
                    if (!known.Contains(p.Id)) return p.Id;
            Thread.Sleep(500);
        }
        return 0;
    }

    static IntPtr WaitWindow(int pid, int seconds)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            IntPtr h = PickGameWindow(pid);
            if (h != IntPtr.Zero) { Log("窗口就绪 PID=" + pid + " hwnd=" + h); return h; }
            Thread.Sleep(500);
        }
        return IntPtr.Zero;
    }

    static IntPtr PickGameWindow(int pid)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((h, l) =>
        {
            uint wpid;
            GetWindowThreadProcessId(h, out wpid);
            if (wpid != (uint)pid) return true;
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            string t = Title(h);
            if (t.Length == 0) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area < 200 * 200) return true;
            if (t.Contains("阴阳师")) { best = h; bestArea = long.MaxValue; return false; }
            if (area > bestArea) { best = h; bestArea = area; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    static string Title(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, 512);
        return sb.ToString();
    }

    // ---------------- 容量 ----------------
    static Rectangle WorkArea() { return Screen.PrimaryScreen.WorkingArea; }

    static bool TryPlan(int n, out int cols, out int rows, out int cw, out int ch)
    {
        Rectangle wa = WorkArea();
        cols = 1; rows = n; cw = 0; ch = 0;
        double bestScale = 0;
        bool ok = false;

        for (int c = 1; c <= n; c++)
        {
            int r = (int)Math.Ceiling((double)n / c);
            int availW = (wa.Width - Gap * (c + 1)) / c;
            int availH = (wa.Height - Gap * (r + 1)) / r;
            if (availW < 100 || availH < 100) continue;

            double s = Math.Min(availW / 16.0, availH / 9.0);
            int w = (int)(s * 16);
            int ht = (int)(s * 9);
            if (w < MinCellW || ht < MinCellH) continue;

            if (!ok) { ok = true; bestScale = s; cols = c; rows = r; cw = w; ch = ht; }
            else if (s > bestScale * 1.08) { bestScale = s; cols = c; rows = r; cw = w; ch = ht; }
            else if (s >= bestScale * 0.92 && c > cols) { bestScale = s; cols = c; rows = r; cw = w; ch = ht; }
        }
        return ok;
    }

    static int Capacity()
    {
        int cap = 1;
        for (int n = 1; n <= 12; n++)
        {
            int c, r, w, h;
            if (TryPlan(n, out c, out r, out w, out h)) cap = n;
        }
        return cap;
    }

    static void PlanReport(int n)
    {
        Rectangle wa = WorkArea();
        Log("屏幕工作区: " + wa.Width + "x" + wa.Height + "，容量: " + Capacity());
        int top = Math.Min(12, Math.Max(n, Capacity()));
        for (int i = 1; i <= top; i++)
        {
            int c, r, w, h;
            if (TryPlan(i, out c, out r, out w, out h))
                Log("  " + i + " 个 -> " + c + "列 x " + r + "行，目标格子 " + w + "x" + h);
            else
                Log("  " + i + " 个 -> 放不下（低于最小格 " + MinCellW + "x" + MinCellH + "）");
        }
    }

    // ---------------- 排版 ----------------
    static void WaitStable(List<IntPtr> handles, int maxSeconds)
    {
        var end = DateTime.Now.AddSeconds(maxSeconds);
        var last = new Dictionary<IntPtr, string>();
        DateTime lastChange = DateTime.Now;

        while (DateTime.Now < end)
        {
            bool changed = false;
            foreach (var h in handles)
            {
                RECT r;
                if (!GetWindowRect(h, out r)) continue;
                string sig = r.Left + "," + r.Top + "," + (r.Right - r.Left) + "," + (r.Bottom - r.Top);
                string old;
                if (!last.TryGetValue(h, out old) || old != sig) { last[h] = sig; changed = true; }
            }
            if (changed) lastChange = DateTime.Now;
            else if ((DateTime.Now - lastChange).TotalSeconds >= 1.5) return;
            Thread.Sleep(300);
        }
        Log("等待窗口稳定超时，直接排版");
    }

    // 先尽量把窗口调成目标格子，再按每行最大高度打包，保证不重叠
    static void Arrange(List<IntPtr> handles)
    {
        if (handles.Count == 0) return;

        int cols, rows, cw, ch;
        if (!TryPlan(handles.Count, out cols, out rows, out cw, out ch))
        {
            cw = MinCellW;
            ch = MinCellH;
            Log("警告: " + handles.Count + " 个超出容量，按最小格 " + cw + "x" + ch + " 处理");
        }

        for (int pass = 0; pass < 2; pass++)
        {
            int bottom = ArrangePass(handles, cw, ch);
            Rectangle wa = WorkArea();
            if (bottom <= wa.Bottom) return;

            double f = (double)(wa.Height - Gap * 2) / (bottom - wa.Top);
            if (f >= 0.98 || pass == 1) return;

            cw = Math.Max(720, (int)(cw * f));
            ch = Math.Max(405, (int)(ch * f));
            Log("排版超出屏幕，缩小目标到 " + cw + "x" + ch + " 重排");
        }
    }

    // 返回本次排版的底边，用于判断是否超出屏幕
    static int ArrangePass(List<IntPtr> handles, int cw, int ch)
    {
        int n = handles.Count;
        Rectangle wa = WorkArea();
        int left = wa.Left + Gap, top = wa.Top + Gap;

        // 1) 先把能动的窗口挪到左上并尝试设成目标格子
        var movable = new List<IntPtr>();
        for (int i = 0; i < n; i++)
        {
            if (SetWindowPos(handles[i], IntPtr.Zero, left + i * 20, top + i * 20, cw, ch,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW))
            {
                movable.Add(handles[i]);
            }
            else if (Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED)
            {
                if (!_accessDeniedLogged)
                {
                    _accessDeniedLogged = true;
                    Log("注意: 有实例以管理员权限运行，普通权限下无法移动它（UIPI 拒绝），排版会跳过它");
                }
            }
            else movable.Add(handles[i]);
        }
        Thread.Sleep(1200);

        // 2) 读回实际尺寸，按行打包（客户端可能自行改了尺寸）
        int x = left, y = top, rowH = 0;
        int right = wa.Right - Gap;
        int placed = 0;
        for (int i = 0; i < movable.Count; i++)
        {
            RECT r;
            if (!GetWindowRect(movable[i], out r)) continue;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 100 || h < 100) { Log("跳过异常窗口（" + w + "x" + h + "）"); continue; }

            if (x > left && x + w > right)
            {
                x = left;
                y += rowH + Gap;
                rowH = 0;
            }

            bool ok = SetWindowPos(movable[i], IntPtr.Zero, x, y, w, h,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            if (ok) placed++;

            RECT got;
            GetWindowRect(movable[i], out got);
            Log("排列 " + (i + 1) + " -> 目标 " + x + "," + y + "，实际 " + got.Left + "," + got.Top +
                " " + (got.Right - got.Left) + "x" + (got.Bottom - got.Top));

            x += w + Gap;
            if (h > rowH) rowH = h;
        }

        Log("排版完成: " + placed + "/" + n + " 个窗口落位（" + (n - movable.Count) + " 个管理员实例未处理）");
        return y + rowH;
    }

    static List<IntPtr> CurrentDesktopWindows()
    {
        var list = new List<IntPtr>();
        foreach (var pid in RunningPids())
        {
            IntPtr h = PickGameWindow(pid);
            if (h != IntPtr.Zero && OnCurrentDesktop(h)) list.Add(h);
        }
        return list;
    }

    static void CloseAll()
    {
        int killed = 0;
        foreach (var name in new[] { "onmyoji", "onmyoji_future", "Launch" })
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in ps) { try { p.Kill(); killed++; } catch { } }
        }
        Log("关闭全部: " + killed + " 个进程");
    }

    // ---------------- 虚拟桌面 ----------------
    static void WinCtrl(byte vk)
    {
        keybd_event(0x5B, 0, 0, UIntPtr.Zero); Thread.Sleep(50);
        keybd_event(0x11, 0, 0, UIntPtr.Zero); Thread.Sleep(50);
        keybd_event(vk, 0, 0, UIntPtr.Zero); Thread.Sleep(50);
        keybd_event(vk, 0, 2, UIntPtr.Zero); Thread.Sleep(50);
        keybd_event(0x11, 0, 2, UIntPtr.Zero); Thread.Sleep(50);
        keybd_event(0x5B, 0, 2, UIntPtr.Zero);
    }

    static Guid DesktopOf(IntPtr h)
    {
        var m = Vdm();
        if (m == null) return Guid.Empty;
        Guid g;
        return m.GetWindowDesktopId(h, out g) == 0 ? g : Guid.Empty;
    }

    static bool OnCurrentDesktop(IntPtr h)
    {
        var m = Vdm();
        if (m == null) return true;
        int on;
        return m.IsWindowOnCurrentVirtualDesktop(h, out on) == 0 && on != 0;
    }

    static bool CreateDesktop(IntPtr homeAnchor)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            WinCtrl(0x44);
            Thread.Sleep(1600);
            if (homeAnchor == IntPtr.Zero || !OnCurrentDesktop(homeAnchor))
            {
                Log("已新建虚拟桌面");
                return true;
            }
            Log("新建桌面热键没生效（前台可能是管理员窗口），重试");
            Thread.Sleep(600);
        }
        return false;
    }

    static void SwitchBack(IntPtr homeAnchor)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            WinCtrl(0x25); // Win+Ctrl+Left
            Thread.Sleep(1200);
            if (homeAnchor == IntPtr.Zero || OnCurrentDesktop(homeAnchor))
            {
                Log("已切回原桌面");
                return;
            }
        }
        Log("警告: 未能确认切回原桌面，请手动 Win+Ctrl+左");
    }
}
