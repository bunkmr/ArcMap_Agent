using System;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Windows.Automation;
using ESRI.ArcGIS;
using ESRI.ArcGIS.esriSystem;
using ESRI.ArcGIS.Framework;
using ESRI.ArcGIS.ArcMapUI;
using ESRI.ArcGIS.Carto;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;

class Program
{
    static System.Collections.Generic.List<string> report = new System.Collections.Generic.List<string>();
    static void Log(string s) { report.Add(s); Console.WriteLine(s); }

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    // ---- DPI 相关 ----
    // 本机为高 DPI（物理 3072x1920，缩放约 225%）。进程若不声明 DPI 感知，
    // Windows 会把它当作 DPI-UNWARE：此时经 MSAA→UIA 桥拿到的跨进程元素会走
    // 「虚拟化」路径，表现为
    //   · BoundingRectangle 被放大 ~2.25 倍（与实际绘制位置对不上）
    //   · IsEnabled / IsKeyboardFocusable 一律报 False（哪怕界面明明可用）
    // 这曾让回归测试误判为「发送按钮被禁用」而整条工具链验证全部跳过。
    // 正确做法：进程启动最早期就声明 DPI 感知。
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int awareness); // 0=unaware,1=system,2=permonitor
    [DllImport("user32.dll")] static extern int GetDpiForSystem();

    /// <summary>尽早声明 DPI 感知（优先 Windows 8.1+ 的 per-monitor-v1，回退 SetProcessDPIAware）。</summary>
    static void MakeDpiAware()
    {
        try { if (SetProcessDpiAwareness(1) == 0) return; } catch { }
        try { SetProcessDPIAware(); } catch { }
    }

    const string Shapefile = @"C:\Program Files (x86)\ArcGIS\Desktop10.4\ArcGlobeData\continent.shp";

    static int Main()
    {
        MakeDpiAware();
        ClearPanelLog(); // 清空面板日志，保证本轮输出可读
        Log("=== ArcMap Agent 本地测试开始 " + DateTime.Now + " ===");
        try { Log("[0] 进程 DPI 感知已声明，系统 DPI=" + GetDpiForSystem()); } catch { }

        // 1) 绑定 ArcObjects 运行时
        try { RuntimeManager.Bind(ProductCode.Desktop); Log("[1] RuntimeManager.Bind(Desktop) OK"); }
        catch (Exception ex) { Log("[1] Bind 失败: " + ex.Message); }

        // 2) 许可（尽力而为，外部自动化不一定能再签出）
        try
        {
            var ao = new AoInitializeClass();
            var st = ao.Initialize(esriLicenseProductCode.esriLicenseProductCodeStandard);
            Log("[2] AoInitialize 状态: " + st);
        }
        catch (Exception ex) { Log("[2] AoInitialize 异常（继续尝试）: " + ex.Message); }

        // 3) 启动 / 附加 ArcMap
        bool weStarted;
        Process arcProc;
        IApplication app = LaunchOrAttachArcMap(out weStarted, out arcProc);
        if (app == null) { Log("[3] 无法获取 ArcMap 应用实例，测试中止。"); Dump(); return 1; }
        Log("[3] 已附加到 ArcMap: " + (app.Caption ?? "") + (weStarted ? "（由测试启动）" : "（已存在会话）"));

        // 等待启动完成（Add-In 提取/加载发生在启动过程中）
        // ★ 启动期间 ArcMap 会弹出「ArcMap - 启动 / Getting Started」**进程级模态**对话框
        //   （上次 ArcMap 被强杀后就容易出现）。它不关掉的话 ArcMap 永远不进入就绪状态，
        //   而且模态会让整个无障碍树把我们的面板报成 disabled。
        //   这里边等边把启动期对话框关掉。
        Log("[3] 等待 ArcMap 启动就绪（并自动关闭启动/欢迎对话框）…");
        IApplication ready = null;
        for (int i = 0; i < 180; i++)   // 最多 180s
        {
            int n = DismissArcMapDialogs(arcProc);
            if (n > 0) Log("[3] 已关闭 " + n + " 个 ArcMap 启动期对话框");
            if (IsAppReady(out ready)) break;
            if (arcProc != null && arcProc.HasExited) { Log("[3] ArcMap 进程已退出"); break; }
            Thread.Sleep(1000);
        }
        if (ready != null) { app = ready; Log("[3] 应用实例就绪（支持 ICommandBars）"); }
        else Log("[3] 仍未拿到支持 ICommandBars 的应用实例，继续尝试");

        // 4) 验证工具栏（插件已加载的标志）
        //    注意：ICommandBars 在 ArcMap 里由 **IDocument.CommandBars** 提供，
        //    直接 `app as ICommandBars` 一定是 null（曾因此误判「插件没加载」）。
        bool toolbarOk = false;
        try
        {
            ICommandBars cb = null;
            try { var doc = app.Document; if (doc != null) cb = doc.CommandBars; } catch { }
            if (cb != null)
            {
                ICommandItem item = null;
                try { item = cb.Find("ArcMapAgent_Toolbar", false, false); } catch { }
                if (item != null) { toolbarOk = true; Log("[4] 工具栏已找到: " + (item.Name ?? "ArcMap Agent")); }
                else Log("[4] 未找到工具栏 ArcMapAgent_Toolbar（showInitially=false 时属正常，可由用户手动勾选）");
            }
            else Log("[4] 无法取得 ICommandBars（app.Document 为空）");
        }
        catch (Exception ex) { Log("[4] 工具栏检查异常: " + ex.Message); }

        // 5) 显示可停靠窗口（触发 OnCreateChild -> AgentControl 构建，验证我们的 UI 代码）
        bool dockableOk = false;
        string[] uidCandidates = {
            "ArcMapAgent_AgentDockableWindow",
            "{6F3A9C52-8B14-4E2D-9A7B-1D5C0E4F83A6}_ArcMapAgent_AgentDockableWindow",
            "{6F3A9C52-8B14-4E2D-9A7B-1D5C0E4F83A6}",
            "ArcMapAgent.AgentDockableWindow"
        };
        try
        {
            IDockableWindowManager dwm = app as IDockableWindowManager;
            if (dwm != null)
            {
                foreach (var cand in uidCandidates)
                {
                    try
                    {
                        UID uid = new UIDClass();
                        uid.Value = cand;
                        IDockableWindow dw = dwm.GetDockableWindow(uid);
                        if (dw != null) { dw.Show(true); dockableOk = true; Log("[5] 可停靠窗口已显示（UID=" + cand + "）"); break; }
                    }
                    catch (Exception ex) { Log("[5] UID=" + cand + " 失败: " + ex.Message); }
                }
                if (!dockableOk) Log("[5] 三种 UID 均未取得，改用 UIA 面板存在性判定");
            }
            else Log("[5] 无法转换 IDockableWindowManager");
        }
        catch (Exception ex) { Log("[5] 可停靠窗口异常: " + ex.Message); }

        Thread.Sleep(2000); // 等待控件创建

        // 6) 通过 UI Automation 驱动面板：/layers
        bool uiLayersOk = false, uiAddOk = false, uiNlOk = false, uiLabelOk = false;
        int beforeCount = -1, afterCount = -1;
        try
        {
            // 优先用「本测试自己启动的 ArcMap 进程」的主窗口句柄做 UI Automation：
            // 若用户同时开着另一个 ArcMap，app.hWnd 可能指向对方的窗口，导致驱动错对象。
            IntPtr hwnd = WaitMainWindow(arcProc, 90);
            if (hwnd == IntPtr.Zero) { hwnd = new IntPtr(app.hWnd); Log("[6] 未取得本进程主窗口句柄，回退用 app.hWnd"); }
            else Log("[6] 使用本测试启动的 ArcMap 主窗口句柄");
            AutomationElement aeApp = AutomationElement.FromHandle(hwnd);
            if (aeApp == null) Log("[6] 无法从 HWND 获取 AutomationElement");
            else
            {
                // 面板会恢复上次停留的页签，且只有当前页的控件出现在 UI Automation 树里。
                // 因此先切到「对话」页，否则找不到 AgentInput/AgentSend/AgentOutput。
                if (ClickByName(aeApp, "对话")) { Log("[6] 已切换到「对话」页签"); Thread.Sleep(800); }
                else Log("[6] 未找到「对话」页签按钮（可能面板未显示）");

                Func<(AutomationElement, AutomationElement, AutomationElement)> find3 = () => (
                    FindByIdOrName(aeApp, "AgentInput"),
                    FindByIdOrName(aeApp, "AgentSend"),
                    FindByIdOrName(aeApp, "AgentOutput"));
                var (input, send, output) = find3();
                if (input == null || send == null || output == null)
                {
                    Log("[6] 首次未找全面板控件，尝试点击工具栏按钮 'ArcMap Agent' 后重试…");
                    if (ClickByName(aeApp, "ArcMap Agent")) Log("[6] 已通过 UIA 触发工具栏按钮");
                    else Log("[6] 未找到可调用的 'ArcMap Agent' 按钮");
                    Thread.Sleep(3000);
                    if (ClickByName(aeApp, "对话")) Thread.Sleep(800);
                    (input, send, output) = find3();
                }
                if (input == null) Log("[6] 未找到输入控件 AgentInput");
                if (send == null) Log("[6] 未找到发送按钮 AgentSend");
                if (output == null) Log("[6] 未找到输出控件 AgentOutput");

                if (input != null && send != null && output != null)
                {
                    dockableOk = true; // UIA 能找到面板控件 => OnCreateChild 已构建 UI

                    // 把三个控件在无障碍树里的状态打出来存档。
                    // 注意：ArcMap 是 DPI-UNWARE 进程，高 DPI 下跨进程 MSAA→UIA 桥对它的
                    // 子窗口**一律报 enabled=False / focusable=False**（误报）。
                    // 因此这里的 enabled 只能当参考，绝不能用来决定「要不要发指令」。
                    Log("[6] 控件状态（enabled 可能因高 DPI 误报，仅作参考）:");
                    Log(Describe(input));
                    Log(Describe(send));
                    Log(Describe(output));
                }

                if (input != null && send != null && output != null)
                {
                    // ---- 驱动方式：真实鼠标 + 键盘 ----
                    // 不用 UIA 的 ValuePattern.SetValue / InvokePattern：
                    // 高 DPI 下跨进程走「虚拟化代理」，SetValue 会抛
                    // ElementNotEnabledException（「对于未启用的元素，不允许执行此操作」），
                    // 而控件其实完全可用。
                    // 改为在 UIA 给出的物理屏幕上「真的点一下输入框、真的敲字、真的按回车」——
                    // 这条路不依赖无障碍状态，而且顺便证明了面板对真实用户是可交互的。
                    Func<string, int, string> runCmd = (cmd, waitMs) =>
                    {
                        string before = ReadAllPanelLog();
                        string last = "";

                        // 面板刚显示 / 被还原时位置会重排一次，第一次读到的坐标可能已过期，
                        // 因此每次重试都重新读边界；发成功后日志里会立刻出现 "You: …"，
                        // 所以「日志没有新增」就可以安全判定为没点中，重试不会重复发送。
                        FocusWindow(hwnd);
                        Thread.Sleep(800);   // 让窗口先稳定下来再取坐标

                        for (int attempt = 1; attempt <= 5; attempt++)
                        {
                            AutomationElement inp = FindByIdOrName(aeApp, "AgentInput");
                            if (inp == null) return "(找不到输入控件)";

                            var r = inp.Current.BoundingRectangle;   // UIA 边界为物理屏幕坐标
                            int cx = (int)(r.X + r.Width / 2), cy = (int)(r.Y + r.Height / 2);
                            Log("    (第 " + attempt + " 次：点击输入框 @" + cx + "," + cy + " 并输入)");

                            FocusWindow(hwnd);
                            ClickAt(cx, cy);
                            Thread.Sleep(300);
                            // 清空可能的残留内容
                            SendVk(0x11); SendVk(0x41); SendVkUp(0x41); SendVkUp(0x11);  // Ctrl+A
                            Thread.Sleep(80);
                            SendVk(0x2E); SendVkUp(0x2E);                               // Delete
                            Thread.Sleep(80);

                            TypeUnicode(cmd);
                            Thread.Sleep(300);
                            SendVk(0x0D); SendVkUp(0x0D);                                // Enter 触发发送

                            Thread.Sleep(waitMs);
                            last = PanelLogDelta(before);
                            if (!string.IsNullOrWhiteSpace(last)) return last;
                            Log("    (面板无新输出 —— 判定没点中输入框，重新取坐标后重试)");
                            Thread.Sleep(1500);
                        }
                        return last;
                    };

                    // 先记录当前图层数（用于对比）
                    beforeCount = GetLayerCount(app);

                    string out1 = runCmd("/layers", 2000);
                    // 只认 slash 处理器的输出前缀，避免把模型自由回答里的「图层」二字误判为通过
                    uiLayersOk = out1.IndexOf(">> 列出图层", StringComparison.Ordinal) >= 0;
                    Log("[6a] /layers 输出:\n" + Indent(out1));

                    // /add 一个示例 shapefile
                    string out2 = runCmd("/add " + Shapefile, 3000);
                    uiAddOk = out2.IndexOf(">> 添加图层", StringComparison.Ordinal) >= 0
                              && out2.IndexOf("已添加图层", StringComparison.Ordinal) >= 0;
                    Log("[6b] /add 输出:\n" + Indent(out2));

                    afterCount = GetLayerCount(app);

                    // [6c] 自然语言 -> 工具调用闭环：验证原生 function calling 与多轮 Agent 循环
                    // 仍然只做只读操作，不会改动用户地图内容
                    long nlPos = PanelLogPosition();          // 兼容保留（不再用于读取）
                    string nlBefore = ReadAllPanelLog();
                    {
                        // 复用 runCmd（含真实输入 + 重试），发送后再轮询日志等结果
                        runCmd("列出当前地图的所有图层，并统计 continent 图层的要素数量", 3000);

                        string nlLog = "";
                        for (int i = 0; i < 120; i++)   // 最多等 120s
                        {
                            Thread.Sleep(1000);
                            nlLog = PanelLogDelta(nlBefore);
                            if (nlLog.IndexOf("[错误]", StringComparison.Ordinal) >= 0) break;
                            if (nlLog.IndexOf("[工具结果]", StringComparison.Ordinal) >= 0
                                && nlLog.IndexOf("Agent:", StringComparison.Ordinal) >= 0) break;
                        }
                        int calls = CountOccurrences(nlLog, "[调用工具]");
                        int results = CountOccurrences(nlLog, "[工具结果]");
                        uiNlOk = calls >= 1 && results >= 1
                                 && nlLog.IndexOf("[错误]", StringComparison.Ordinal) < 0;
                        Log("[6c] 自然语言工具调用（调用 " + calls + " 次 / 返回 " + results + " 次）:\n" + Indent(nlLog));

                        // 留一点时间让面板把最终回复渲染完，避免影响后面的截图
                        Thread.Sleep(3000);
                    }

                    // [6d] 标注链路：v0.4.1 新增了 set_labels。先用 /fields 取真实字段名，
                    // 再用自然语言让模型去标注 —— 覆盖「模型声称没有标注功能」这个历史问题。
                    {
                        string fldOut = runCmd("/fields continent", 3000);
                        string labelField = ParseFirstFieldName(fldOut);
                        Log("[6d] /fields continent 输出:\n" + Indent(fldOut));
                        Log("[6d] 选中的标注字段: " + (labelField ?? "(未解析出)"));

                        // 降级：解析不出字段名时，改成不给字段的指令（考察模型能否自己挑字段），
                        // 保证这一步永远不会被静默跳过 —— 历史上有一次就是因为解析器拿不到字段名
                        // 而整条标注链路被跳过，最后被误读成「标注功能没接上」。
                        string labelCmd = string.IsNullOrEmpty(labelField)
                            ? "给 continent 图层显示标注"
                            : "给 continent 图层显示标注，标注字段用 " + labelField + "，字号 11，加白色晕圈";
                        Log("[6d] 标注指令: " + labelCmd);

                        {
                            string lbBefore = ReadAllPanelLog();
                            runCmd(labelCmd, 3000);

                            string lbLog = "";
                            for (int i = 0; i < 120; i++)
                            {
                                Thread.Sleep(1000);
                                lbLog = PanelLogDelta(lbBefore);
                                if (lbLog.IndexOf("[错误]", StringComparison.Ordinal) >= 0) break;
                                if (lbLog.IndexOf("[工具结果]", StringComparison.Ordinal) >= 0
                                    && lbLog.IndexOf("Agent:", StringComparison.Ordinal) >= 0) break;
                            }
                            int lbCalls = CountOccurrences(lbLog, "[调用工具]");
                            bool calledLabels = lbLog.IndexOf("set_labels", StringComparison.Ordinal) >= 0;
                            bool labelOk = lbLog.IndexOf("已为图层", StringComparison.Ordinal) >= 0
                                           && lbLog.IndexOf("标注", StringComparison.Ordinal) >= 0;
                            uiLabelOk = calledLabels && labelOk
                                        && lbLog.IndexOf("[错误]", StringComparison.Ordinal) < 0;
                            Log("[6d] 标注链路（调用 " + lbCalls + " 次，set_labels=" + calledLabels
                                + "，成功=" + labelOk + "）:\n" + Indent(lbLog));
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Log("[6] UI Automation 异常: " + ex.Message); }

        // 7) 截图作为可视证据
        try
        {
            IntPtr hwnd = WaitMainWindow(arcProc, 3);
            if (hwnd == IntPtr.Zero) hwnd = new IntPtr(app.hWnd);
            string png = CaptureWindow(hwnd);
            Log("[7] 截图已保存: " + (png ?? "失败"));
        }
        catch (Exception ex) { Log("[7] 截图异常: " + ex.Message); }

        // 8) 收尾：仅关闭由本测试启动的 ArcMap（先优雅 WM_CLOSE，超时再 Kill）
        if (weStarted)
        {
            try
            {
                Log("[8] 优雅关闭由测试启动的 ArcMap（WM_CLOSE）…");
                PostMessage(new IntPtr(app.hWnd), 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(1000);
                    bool alive = false;
                    foreach (var p in Process.GetProcessesByName("ArcMap")) { alive = !p.HasExited; p.Dispose(); }
                    if (!alive) { Log("[8] ArcMap 已正常退出"); break; }
                    if (i == 9) Log("[8] 仍在退出中…");
                }
            }
            catch { }
            foreach (var p in Process.GetProcessesByName("ArcMap"))
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
            }
        }
        else
        {
            Log("[8] 附加到已有 ArcMap 会话，测试不关闭它（请自行关闭）。");
        }

        // 汇总
        Log("");
        Log("=== 结果汇总 ===");
        Log("工具栏加载: " + (toolbarOk ? "PASS" : "— (见说明)"));
        Log("可停靠窗口(OnCreateChild): " + (dockableOk ? "PASS" : "FAIL"));
        Log("/layers 工具链路: " + (uiLayersOk ? "PASS" : "FAIL"));
        Log("/add 工具链路: " + (uiAddOk ? "PASS" : "FAIL"));
        Log("自然语言工具调用(多轮): " + (uiNlOk ? "PASS" : "FAIL"));
        Log("标注链路(set_labels): " + (uiLabelOk ? "PASS" : "FAIL"));
        Log("图层数 before=" + beforeCount + " after=" + afterCount + (afterCount > beforeCount ? " (新增成功)" : ""));

        Dump();
        return (dockableOk && uiLayersOk && uiAddOk && uiNlOk && uiLabelOk) ? 0 : 2;
    }

    /// <summary>
    /// 本测试要操作的 ArcMap 进程号。
    /// 用户机器上常常已经开着一个 ArcMap（可能在编辑未保存的文档），直接附加过去会：
    ///   ① 用真实键鼠在别人的会话里打字；② 可能测的还是旧 DLL。
    /// 因此这里始终另起一个独立实例，并用 PID 过滤 AppROT，确保只操作自己启动的那一个。
    /// </summary>
    static int _targetPid = 0;

    /// <summary>判断某个 ROT 里的 ArcMap 应用是否属于本测试启动的进程。</summary>
    static bool BelongsToTarget(IApplication a)
    {
        if (a == null) return false;
        if (_targetPid <= 0) return true;
        try
        {
            uint wpid;
            GetWindowThreadProcessId(new IntPtr(a.hWnd), out wpid);
            return (int)wpid == _targetPid;
        }
        catch { return false; }
    }

    static IApplication LaunchOrAttachArcMap(out bool weStarted, out Process arcProcess)
    {
        weStarted = false;
        var running = Process.GetProcessesByName("ArcMap");
        if (running.Length > 0)
            Log("[3] 检测到已有 " + running.Length + " 个 ArcMap 在运行 —— 为避免影响用户会话，另起独立实例");

        Log("[3] 启动 ArcMap…");
        var psi = new ProcessStartInfo(@"C:\Program Files (x86)\ArcGIS\Desktop10.4\bin\arcmap.exe") { UseShellExecute = true };
        Process arc = Process.Start(psi);
        weStarted = true;
        arcProcess = arc;
        _targetPid = arc.Id;
        Log("[3] 本次测试的 ArcMap PID = " + _targetPid);

        // 轮询 AppROT 直到出现「属于本进程」的应用（最多 ~150s）
        for (int i = 0; i < 150; i++)
        {
            try
            {
                var rot = new AppROTClass();
                for (int k = 0; k < rot.Count; k++)
                {
                    IApplication a = null;
                    try { a = rot.Item[k] as IApplication; } catch { }
                    if (BelongsToTarget(a)) return a;
                }
            }
            catch (Exception ex) { if (i == 0) Log("[3] AppROT 探测: " + ex.Message); }
            Thread.Sleep(1000);
            if (arc != null && arc.HasExited)
            {
                // 安装为单实例时，第二次启动会被合并到已有窗口并立刻退出 → 退回附加模式
                Log("[3] 新进程已退出（ArcMap 可能是单实例），改为附加到已运行的实例");
                _targetPid = 0;
                weStarted = false;
                var alive = Process.GetProcessesByName("ArcMap");
                if (alive.Length > 0) { arcProcess = alive[0]; arc = alive[0]; }
                break;
            }
        }

        for (int i = 0; i < 60; i++)
        {
            try
            {
                var rot = new AppROTClass();
                for (int k = 0; k < rot.Count; k++)
                {
                    IApplication a = null;
                    try { a = rot.Item[k] as IApplication; } catch { }
                    if (BelongsToTarget(a)) return a;
                }
            }
            catch { }
            Thread.Sleep(1000);
        }
        return null;
    }

    /// <summary>
    /// 轮询 ROT，返回一个「已完全初始化」的 ArcMap 应用实例（能转换为 ICommandBars，说明主窗口/命令条已就绪）。
    /// 启动早期注册进 ROT 的对象常常是未初始化代理，直接使用会得到 ICommandBars=null 或
    /// GetDockableWindow 抛 E_INVALIDARG，表现为「插件明明装了却判定为未加载」。
    /// </summary>
    // ================= 窗口枚举：关闭 ArcMap 启动期模态对话框 =================
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    const uint WM_CLOSE_MSG = 0x0010;

    /// <summary>
    /// 关闭 ArcMap 进程里可见的「启动 / Getting Started」模态对话框（class #32770）。
    /// 返回关掉的数量。
    /// </summary>
    static int DismissArcMapDialogs(Process arc)
    {
        if (arc == null) return 0;
        int pid;
        try { pid = arc.Id; } catch { return 0; }

        int closed = 0;
        EnumWindows((h, l) =>
        {
            uint wpid;
            GetWindowThreadProcessId(h, out wpid);
            if (wpid != (uint)pid) return true;
            if (!IsWindowVisible(h)) return true;

            var cls = new System.Text.StringBuilder(256);
            GetClassName(h, cls, 256);
            if (cls.ToString() != "#32770") return true;   // 只处理对话框

            var title = new System.Text.StringBuilder(512);
            GetWindowText(h, title, 512);
            string t = title.ToString();

            bool isStartupDialog = t.IndexOf("启动", StringComparison.Ordinal) >= 0
                                || t.IndexOf("Getting Started", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isStartupDialog)
            {
                Console.WriteLine("    关闭启动期对话框: '" + t + "'");
                PostMessage(h, WM_CLOSE_MSG, IntPtr.Zero, IntPtr.Zero);
                closed++;
            }
            else if (t.Length > 0)
            {
                Console.WriteLine("    (发现其他对话框，保留不动: '" + t + "')");
            }
            return true;
        }, IntPtr.Zero);
        return closed;
    }

    /// <summary>
    /// 判断 ArcMap 是否已真正就绪。
    /// 判据用 `Document.CommandBars`（可靠），**不能**用 `a as ICommandBars`（恒为 null）。
    /// </summary>
    static bool IsAppReady(out IApplication app)
    {
        app = null;
        try
        {
            var rot = new AppROTClass();
            for (int k = 0; k < rot.Count; k++)
            {
                IApplication a = null;
                try { a = rot.Item[k] as IApplication; } catch { }
                if (!BelongsToTarget(a)) continue;
                bool ok = false;
                try { var doc = a.Document; if (doc != null) ok = doc.CommandBars != null; } catch { }
                if (ok) { app = a; return true; }
            }
        }
        catch { }
        return false;
    }

    static IApplication WaitForReadyApp(int seconds)
    {
        IApplication fallback = null;
        for (int i = 0; i < seconds; i++)
        {
            try
            {
                var rot = new AppROTClass();
                for (int k = 0; k < rot.Count; k++)
                {
                    IApplication a = null;
                    try { a = rot.Item[k] as IApplication; } catch { }
                    if (!BelongsToTarget(a)) continue;
                    if (fallback == null) fallback = a;
                    bool cmdbars = false;
                    try { cmdbars = (a as ICommandBars) != null; } catch { }
                    if (cmdbars) return a;
                }
            }
            catch { }
            Thread.Sleep(1000);
        }
        return fallback;
    }

    static int GetLayerCount(IApplication app)    {
        try
        {
            IMxDocument mx = app.Document as IMxDocument;
            if (mx == null) return -1;
            return mx.FocusMap.LayerCount;
        }
        catch { return -1; }
    }

    static readonly string PanelLogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArcMapAgent", "panel.log");

    static void ClearPanelLog()
    {
        try { if (System.IO.File.Exists(PanelLogPath)) System.IO.File.Delete(PanelLogPath); } catch { }
    }

    /// <summary>整份读日志（与其他进程的写入共享读）。</summary>
    static string ReadAllPanelLog()
    {
        try
        {
            using (var fs = new System.IO.FileStream(PanelLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    /// <summary>
    /// 取「相对快照 before 的新增内容」。
    /// 不用文件偏移量：偏移法在文件被重建/截断时会退化成「全量读」，
    /// 于是把启动期的旧日志当成新输出，导致「有没有点中输入框」判断失效
    /// （曾因此让 /layers、/add 的自动重试永远不触发）。
    /// </summary>
    static string PanelLogDelta(string before)
    {
        string now = ReadAllPanelLog();
        if (string.IsNullOrEmpty(before)) return now;
        if (now == before) return "";
        if (now.Length > before.Length && now.StartsWith(before)) return now.Substring(before.Length);
        return now;   // 文件被重写/截断 → 退回全量
    }

    static long PanelLogPosition()
    {
        try { return new System.IO.FileInfo(PanelLogPath).Length; } catch { return 0; }
    }

    static string ReadPanelLog(long fromPos)
    {
        try
        {
            using (var fs = new System.IO.FileStream(PanelLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fromPos > 0 && fs.Length > fromPos) fs.Seek(fromPos, SeekOrigin.Begin);
                else if (fromPos == 0 && fs.Length > 0) { /* 新文件直接全读 */ }
                using (var sr = new StreamReader(fs))
                    return sr.ReadToEnd();
            }
        }
        catch { return "(读取日志失败)"; }
    }

    /// <summary>轮询进程主窗口句柄（ArcMap 启动后需要一段时间才创建主窗口）。</summary>
    static IntPtr WaitMainWindow(Process p, int seconds)
    {
        if (p == null) return IntPtr.Zero;
        for (int i = 0; i < seconds; i++)
        {
            try
            {
                p.Refresh();
                if (p.HasExited) return IntPtr.Zero;
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
            catch { return IntPtr.Zero; }
            Thread.Sleep(1000);
        }
        return IntPtr.Zero;
    }

    /// <summary>等待 UI 元素变为可用（面板「与模型交互中」时发送按钮会被禁用）。</summary>
    // ================= 真实鼠标 / 键盘输入 =================
    // 不走 UIA 的 ValuePattern / InvokePattern：高 DPI 下跨进程代理会误报「元素未启用」。
    // 直接模拟真实用户操作 —— 点一下、敲字、按回车。
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
    const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION U; }

    /// <summary>把目标窗口拉到前台并还原（否则模拟点击可能落在别的窗口上）。</summary>
    static void FocusWindow(IntPtr hwnd)
    {
        try { ShowWindow(hwnd, SW_RESTORE); SetForegroundWindow(hwnd); Thread.Sleep(200); } catch { }
    }

    /// <summary>在物理屏幕坐标处点一下左键。</summary>
    static void ClickAt(int x, int y)
    {
        try
        {
            SetCursorPos(x, y);
            Thread.Sleep(60);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        }
        catch { }
    }

    static void SendKey(ushort vk, bool up)
    {
        var inp = new INPUT[1];
        inp[0].type = INPUT_KEYBOARD;
        inp[0].U.ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = up ? KEYEVENTF_KEYUP : 0 };
        SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
    }
    static void SendVk(ushort vk) { SendKey(vk, false); }
    static void SendVkUp(ushort vk) { SendKey(vk, true); }

    /// <summary>
    /// 以 Unicode 方式逐字符敲入文本（不受输入法 / 键盘布局影响，中文也能直接送进去）。
    /// </summary>
    static void TypeUnicode(string text)
    {
        foreach (char ch in text)
        {
            var inp = new INPUT[2];
            inp[0].type = INPUT_KEYBOARD;
            inp[0].U.ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE };
            inp[1].type = INPUT_KEYBOARD;
            inp[1].U.ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
            SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(12);
        }
    }

    /// <summary>把元素在无障碍树里的状态打成一行，便于判断 IsEnabled=false 的真实原因。</summary>
    static string Describe(AutomationElement el)    {
        if (el == null) return "   (null)";
        try
        {
            var c = el.Current;
            var r = c.BoundingRectangle;
            return "   name='" + c.Name + "' autoId='" + c.AutomationId + "' cls='" + c.ClassName
                 + "' type=" + c.ControlType.ProgrammaticName
                 + " enabled=" + c.IsEnabled + " offscreen=" + c.IsOffscreen
                 + " focusable=" + c.IsKeyboardFocusable
                 + " bounds=(" + (int)r.X + "," + (int)r.Y + "," + (int)r.Width + "x" + (int)r.Height + ")";
        }
        catch (Exception ex) { return "   (describe failed: " + ex.Message + ")"; }
    }

    static AutomationElement WaitEnabled(AutomationElement el, int seconds)
    {
        for (int i = 0; i < seconds * 2; i++)
        {
            try { if (el.Current.IsEnabled) return el; } catch { return el; }
            Thread.Sleep(500);
        }
        return el;
    }

    /// <summary>按可见名称点击一个元素（按钮用 Invoke，页签用 SelectionItem）。</summary>
    static bool ClickByName(AutomationElement root, string name)
    {
        try
        {
            var el = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, name));
            if (el == null) return false;
            object pat;
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out pat)) { ((InvokePattern)pat).Invoke(); return true; }
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pat)) { ((SelectionItemPattern)pat).Select(); return true; }
            return false;
        }
        catch { return false; }
    }

    static AutomationElement FindByIdOrName(AutomationElement root, string id)    {
        var cond = new OrCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, id),
            new PropertyCondition(AutomationElement.NameProperty, id));
        // 先直接搜一次
        var found = root.FindFirst(TreeScope.Descendants, cond);
        if (found != null) return found;
        return null;
    }

    static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>
    /// 把 panel.log 的一行「逻辑多行文本」还原成真正的多行。
    /// 注意：AgentLog 落盘时会把 \r\n / \n 统一替换为 " | "，保证一条日志占一行；
    /// 因此任何按行解析 panel.log 的代码都必须先过这里，否则永远只能看到第一条子行
    /// （曾导致 ParseFirstFieldName 只拿到 FID 后 continue 出循环，[6d] 标注测试被静默跳过）。
    /// </summary>
    static string[] PanelLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return new string[0];
        return s.Replace(" | ", "\n").Split('\n');
    }

    /// <summary>
    /// 从 /fields 的输出里解析出第一个「可标注」字段名（跳过 OID / Geometry / Blob）。
    /// 输出形如：  1. CONTINENT  类型=esriFieldTypeString  长度=254
    /// </summary>
    static string ParseFirstFieldName(string fieldsOutput)
    {
        if (string.IsNullOrEmpty(fieldsOutput)) return null;
        foreach (string raw in PanelLines(fieldsOutput))
        {
            string s = raw.Trim();
            int dot = s.IndexOf(". ", StringComparison.Ordinal);
            if (dot <= 0) continue;
            string rest = s.Substring(dot + 2);
            int t = rest.IndexOf("类型=", StringComparison.Ordinal);
            if (t <= 0) continue;
            string name = rest.Substring(0, t).Trim();
            if (name.Length == 0) continue;
            if (rest.IndexOf("esriFieldTypeOID", StringComparison.Ordinal) >= 0) continue;
            if (rest.IndexOf("esriFieldTypeGeometry", StringComparison.Ordinal) >= 0) continue;
            if (rest.IndexOf("esriFieldTypeBlob", StringComparison.Ordinal) >= 0) continue;
            return name;
        }
        return null;
    }

    static void SetValue(AutomationElement el, string text)
    {
        object pat;
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out pat))
            ((ValuePattern)pat).SetValue(text);
        else if (el.TryGetCurrentPattern(TextPattern.Pattern, out pat))
            Console.WriteLine("TextPattern 不支持 SetValue");
    }

    static void Invoke(AutomationElement el)
    {
        object pat;
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out pat))
            ((InvokePattern)pat).Invoke();
    }

    static string GetValue(AutomationElement el)
    {
        object pat;
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out pat))
            return ((ValuePattern)pat).Current.Value ?? "";
        return "";
    }

    static string Indent(string s)
    {
        if (string.IsNullOrEmpty(s)) return "    (空)";
        var lines = PanelLines(s);
        return "    " + string.Join("\n    ", lines).TrimEnd();
    }

    static string CaptureWindow(IntPtr hwnd)
    {
        // DPI 不一致时 GetWindowRect 会返回与屏幕实况不符的尺寸（本机 225% 缩放下会得到整屏大小），
        // 优先用 DWM 的「扩展边框」，并且在真正截屏前重新量一次窗口位置。
        RECT r;
        if (!TryGetWindowBounds(hwnd, out r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;
        using (var bmp = new Bitmap(w, h))
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            bool ok = PrintWindow(hwnd, hdc, 0);
            g.ReleaseHdc(hdc);
            if (!ok) return null;
            string path = @"D:\Work\projects\arcmap_agent\_arcmap_test.png";
            bmp.Save(path, ImageFormat.Png);
            return path;
        }
    }

    static bool TryGetWindowBounds(IntPtr hwnd, out RECT r)
    {
        try
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0)
            {
                if (r.Right > r.Left && r.Bottom > r.Top) return true;
            }
        }
        catch { }
        return GetWindowRect(hwnd, out r);
    }

    static void Dump()
    {
        string path = @"D:\Work\projects\arcmap_agent\_test_result.txt";
        File.WriteAllText(path, string.Join("\n", report), System.Text.Encoding.UTF8);
    }
}
