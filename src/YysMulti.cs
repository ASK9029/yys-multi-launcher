using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 阴阳师多开器（启动器感知版）
//   双击     -> 共 2 个：第 1 个走官方 Launch.exe，其余直连 bin\onmyoji.exe，错峰并自动并排
//   多开器 3 -> 共 3 个
//   多开器 /close -> 关掉全部
//
// 为什么这么做：Launch.exe 内部有 OnmyojiLaunch 互斥体，并且会 KillClient(onmyoji.exe)，
// 连续点两次启动器很可能把已开的客户端杀掉；而直连 bin\onmyoji.exe 可以并存。
// 所以第 1 个用启动器保证版本/补丁校验，后续用客户端本体。

static class Program
{
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_SHOWWINDOW = 0x0040;

    static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string Launcher = Path.Combine(Root, "Launch.exe");
    static readonly string Client = Path.Combine(Root, "bin", "onmyoji.exe");
    static readonly string ClientAlt = Path.Combine(Root, "bin", "onmyoji_future.exe");
    static readonly string LogPath = Path.Combine(Root, "YysMulti-log.txt");

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

            if (args.Length > 0 && (args[0] == "/close" || args[0] == "--close" || args[0] == "-close"))
            {
                CloseAll();
                return 0;
            }

            if (args.Length > 0 && (args[0] == "/arrange" || args[0] == "--arrange"))
            {
                var exist = new HashSet<int>();
                foreach (var name in new[] { "onmyoji", "onmyoji_future" })
                    foreach (var p in Process.GetProcessesByName(name)) exist.Add(p.Id);
                var hs = WaitWindows(exist, 20);
                Log("重新排版: " + hs.Count + " 个窗口");
                if (hs.Count > 0) Arrange(hs);
                return 0;
            }

            int count = 2;
            if (args.Length > 0) int.TryParse(args[0], out count);
            if (count < 1) count = 1;
            if (count > 6) count = 6;

            if (!File.Exists(Launcher) && !File.Exists(Client))
            {
                Log("找不到 Launch.exe / bin\\onmyoji.exe");
                MessageBox.Show("没找到游戏：\r\n" + Launcher + "\r\n" + Client +
                    "\r\n\r\n请把本程序放在阴阳师客户端根目录。", "阴阳师多开器",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            var before = new HashSet<int>();
            foreach (var name in new[] { "onmyoji", "onmyoji_future" })
                foreach (var p in Process.GetProcessesByName(name))
                    before.Add(p.Id);

            Log("已有客户端实例: " + before.Count + "，目标: " + count);

            int need = count - before.Count;
            if (need > 0 && before.Count == 0 && File.Exists(Launcher))
            {
                Log("第 1 个走官方启动器 Launch.exe（含版本/补丁校验，约 1 分钟）");
                StartProcess(Launcher);
                int pid = WaitNewClient(before, 200);
                if (pid > 0) { before.Add(pid); need--; Log("启动器已拉起客户端 PID=" + pid); }
                else Log("启动器 200 秒内没拉起客户端，改为直连客户端");
                // 让第 1 个客户端先完成初始化，再开第 2 个，避免争抢配置文件
                if (need > 0) { Log("等待 10 秒再开下一个"); Thread.Sleep(10000); }
            }

            for (int i = 0; i < need; i++)
            {
                string exe = File.Exists(Client) ? Client : ClientAlt;
                var p = StartProcess(exe);
                if (p != null) { before.Add(p.Id); Log("直连客户端 PID=" + p.Id); }
                if (i < need - 1) Thread.Sleep(12000); // 错峰，避免同时读写配置/缓存
            }

            var handles = WaitWindows(before, 240);
            Log("捕获到游戏窗口: " + handles.Count);
            if (handles.Count > 0) Arrange(handles);
            return 0;
        }
        catch (Exception ex)
        {
            Log("异常: " + ex);
            return 1;
        }
    }

    static Process StartProcess(string exe)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Root, UseShellExecute = true });
        }
        catch (Exception ex) { Log("启动失败 " + exe + ": " + ex.Message); return null; }
    }

    // 等启动器把客户端拉起来
    static int WaitNewClient(HashSet<int> before, int seconds)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            foreach (var name in new[] { "onmyoji", "onmyoji_future" })
                foreach (var p in Process.GetProcessesByName(name))
                    if (!before.Contains(p.Id)) return p.Id;
            Thread.Sleep(500);
        }
        return 0;
    }

    static List<IntPtr> WaitWindows(HashSet<int> pids, int seconds)
    {
        var found = new Dictionary<int, IntPtr>();
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end && found.Count < pids.Count)
        {
            foreach (int pid in pids)
            {
                if (found.ContainsKey(pid)) continue;
                IntPtr h = PickGameWindow(pid);
                if (h != IntPtr.Zero)
                {
                    found[pid] = h;
                    Log("窗口就绪 PID=" + pid + " hwnd=" + h + " title='" + Title(h) + "'");
                }
            }
            if (found.Count < pids.Count) Thread.Sleep(500);
        }
        return new List<IntPtr>(found.Values);
    }

    // 进程里挑“阴阳师”主窗口：优先标题含“阴阳师”，否则取面积最大的可见窗口
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
            bool isGame = t.Contains("阴阳师");
            if (isGame) { best = h; bestArea = long.MaxValue; return false; }
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

    static void Arrange(List<IntPtr> handles)
    {
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        int n = handles.Count;
        int cols = n <= 1 ? 1 : 2;
        int rows = (int)Math.Ceiling((double)n / cols);
        int gap = 6;
        int cw = (wa.Width - gap * (cols + 1)) / cols;
        int ch = (int)(cw * 9.0 / 16.0);              // 保持 16:9，避免画面被拉扁/黑边
        int totalH = ch * rows + gap * (rows + 1);
        if (totalH > wa.Height)
        {
            ch = (wa.Height - gap * (rows + 1)) / rows;
            cw = (int)(ch * 16.0 / 9.0);
            totalH = ch * rows + gap * (rows + 1);
        }
        int startY = wa.Top + Math.Max(gap, (wa.Height - totalH) / 2 + gap);

        for (int i = 0; i < n; i++)
        {
            int r = i / cols, c = i % cols;
            int x = wa.Left + gap + c * (cw + gap);
            int y = startY + r * (ch + gap);
            SetWindowPos(handles[i], IntPtr.Zero, x, y, cw, ch, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            RECT got;
            GetWindowRect(handles[i], out got);
            Log("排列窗口 " + (i + 1) + " -> 目标 " + x + "," + y + " " + cw + "x" + ch +
                "，实际 " + got.Left + "," + got.Top + " " + (got.Right - got.Left) + "x" + (got.Bottom - got.Top));
        }
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
}
