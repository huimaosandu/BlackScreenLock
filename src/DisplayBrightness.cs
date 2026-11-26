using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;

namespace BlackScreenLock
{
    /// <summary>
    /// 锁屏期间把显示器背光调到最低，解锁后恢复原亮度，让“纯黑遮罩”更接近全黑
    /// （纯黑像素仍挡不住背光，降背光是软件层能做到的最黑状态；不关屏，不影响 DWM 与截图）。
    ///
    /// 两类通道：
    ///   DDC/CI —— 外接显示器，经 dxva2 的 Get/SetMonitorBrightness（VCP 0x10）逐台调节；
    ///   WMI    —— 笔记本内置面板，root\wmi 的 WmiMonitorBrightnessMethods（内置面板通常不支持 DDC）。
    /// 任一台都不支持时静默跳过：遮罩本身仍是纯黑，只是背光没降。
    ///
    /// 线程模型：Dim/Restore 在 UI 线程调用，但只是投递“期望状态”（调暗/恢复），
    /// 真正的 WMI/DDC 硬件操作全部由后台线程串行收敛执行——UI 线程（也是低级键鼠钩子的
    /// 服务线程）绝不被阻塞，否则钩子回调超时会让物理输入穿透到桌面。
    /// 期望状态快速连续变化时只收敛到最终状态（如锁屏后立刻唤醒，可能来不及调暗）。
    ///
    /// 防呆：调暗前先把各台的原亮度写入 BlackScreenLock.dim 状态文件；
    /// 若进程在锁屏中崩溃（亮度停在最低值），下次启动读取该文件把亮度还原并删除文件。
    /// </summary>
    internal static class DisplayBrightness
    {
        private sealed class DimTarget
        {
            public IntPtr Handle;       // DDC 物理显示器句柄；WMI 目标为 IntPtr.Zero
            public uint Original;       // 调暗前的亮度值
            public uint Minimum;        // 报告的最低亮度（通常 0）
            public uint Maximum;        // 报告的最高亮度（通常 100）
            public bool IsWmi;
            public object WmiObject;    // WmiMonitorBrightnessMethods 实例，保持到恢复时复用
            public int Index;           // 所属通道内的序号（DDC/WMI 分别从 0 编号），崩溃恢复时按序对位
            public string Description;
        }

        private const string StateFileName = "BlackScreenLock.dim";
        private const int BlockingWaitTimeoutMs = 5000;

        private static readonly object Gate = new object();
        private static List<DimTarget> _dimmed;     // 非 null = 当前已调暗（Gate 保护）
        private static int _desired;                // 期望状态：0=恢复 1=调暗（Gate 保护）
        private static bool _busy;                  // 收敛线程是否在运行（Gate 保护）

        /// <summary>当前是否处于“已调暗”状态（锁屏中）。</summary>
        public static bool IsDimmed
        {
            get { lock (Gate) { return _dimmed != null; } }
        }

        /// <summary>请求调暗（异步，后台线程执行硬件操作）。已处于调暗状态时收敛为空操作。</summary>
        public static void Dim()
        {
            RequestState(1);
        }

        /// <summary>请求恢复原亮度（异步）。未调暗时是空操作。</summary>
        public static void Restore()
        {
            RequestState(0);
        }

        /// <summary>
        /// 同步恢复：先等在途的后台操作排空（超时兜底），再当面收敛一次。
        /// 只在进程退出路径（Dispose）使用——退出后没有下次机会，必须确保恢复落地。
        /// </summary>
        public static void RestoreBlocking()
        {
            lock (Gate)
            {
                _desired = 0;
                long start = Environment.TickCount;
                while (_busy && Environment.TickCount - start < BlockingWaitTimeoutMs)
                    Monitor.Wait(Gate, 50);
            }

            if (IsDimmed)
                RestoreCore();
        }

        // ---------------- 期望状态收敛 ----------------

        /// <summary>投递期望状态；没有在跑的收敛线程时启动一个（用完即退，不常驻）。</summary>
        private static void RequestState(int desired)
        {
            lock (Gate)
            {
                _desired = desired;
                if (!_busy)
                {
                    _busy = true;
                    Thread worker = new Thread(ConvergeLoop);
                    worker.IsBackground = true;
                    worker.Name = "BlackScreenLock.Backlight";
                    worker.Start();
                }
            }
        }

        /// <summary>后台线程主体：反复检查期望状态并执行到一致为止；一步失败则放弃（等下次投递重试）。</summary>
        private static void ConvergeLoop()
        {
            while (true)
            {
                int desired;
                lock (Gate)
                {
                    desired = _desired;
                    bool dimmedNow = _dimmed != null;
                    if (desired == (dimmedNow ? 1 : 0))
                    {
                        _busy = false;
                        Monitor.PulseAll(Gate); // 唤醒 RestoreBlocking 的等待
                        return;
                    }
                }

                bool converged = desired == 1 ? DimCore() : RestoreCore();
                if (!converged)
                {
                    lock (Gate)
                    {
                        _busy = false;
                        Monitor.PulseAll(Gate);
                    }
                    return;
                }
            }
        }

        // ---------------- 核心操作（只在后台线程/退出路径调用） ----------------

        /// <summary>收集目标、写状态文件、把所有可调显示器调到最低。返回是否完成调暗。</summary>
        private static bool DimCore()
        {
            lock (Gate)
            {
                if (_dimmed != null) return true; // 已调暗，视为收敛
            }

            List<DimTarget> targets = CollectTargets();
            if (targets.Count == 0)
            {
                Log.Write("背光调低：没有支持亮度调节的显示器（DDC/CI 与 WMI 均不可用），保持纯黑遮罩");
                return false;
            }

            // 状态文件先于调暗动作：任何时刻被杀都能按文件还原
            WriteStateFile(targets);

            bool anyDimmed = false;
            foreach (DimTarget target in targets)
            {
                if (ApplyTarget(target, target.Minimum)) anyDimmed = true;
            }

            if (!anyDimmed)
            {
                ReleaseTargets(targets);
                DeleteStateFile();
                Log.Write("背光调低：所有显示器调暗均失败，保持原亮度");
                return false;
            }

            lock (Gate)
            {
                _dimmed = targets;
            }
            Log.Write("背光调低：共 " + targets.Count + " 台显示器，状态文件已写入");
            return true;
        }

        /// <summary>把已调暗的显示器恢复到原亮度。返回是否完成恢复（全失败时返回 false 并保留重试机会）。</summary>
        private static bool RestoreCore()
        {
            List<DimTarget> targets;
            lock (Gate)
            {
                targets = _dimmed;
                _dimmed = null;
            }
            if (targets == null) return true;

            int restored = 0;
            foreach (DimTarget target in targets)
            {
                if (ApplyTarget(target, target.Original)) restored++;
            }
            ReleaseTargets(targets);

            if (restored == 0)
            {
                // 一台都没恢复成功（句柄失效/面板拒绝写入）：把状态留在“已调暗”，
                // 状态文件也不删，等下次恢复投递（如解锁、退出）重试
                bool rolledBack = false;
                lock (Gate)
                {
                    if (_dimmed == null)
                    {
                        _dimmed = targets;
                        rolledBack = true;
                    }
                    // _dimmed 已被新一轮调暗占据时，旧目标就此放弃，由新一轮流程接管
                }
                if (rolledBack)
                    Log.Write("背光恢复全部失败（面板拒绝写入或句柄失效），保留状态待重试");
                return false;
            }

            if (restored == targets.Count)
            {
                DeleteStateFile();
                Log.Write("背光已恢复原亮度");
            }
            else
            {
                // 部分失败：状态文件保留，崩溃恢复时会对已恢复的显示器重复写原值（幂等无害）
                Log.Write("背光部分恢复：" + restored + "/" + targets.Count + " 台成功，状态文件保留");
            }
            return true;
        }

        /// <summary>
        /// 启动时的崩溃恢复：上次进程调暗后没走到恢复逻辑（崩溃/被结束）会把亮度留在最低值，
        /// 这里读状态文件尽力还原，然后删除文件。正常退出后文件不存在，本方法是空操作。
        /// 只在 App 构造时（钩子未装、锁未激活）同步调用，不会阻塞任何关键路径。
        /// </summary>
        public static void RecoverFromStateFileIfAny()
        {
            lock (Gate)
            {
                if (_dimmed != null) return;
            }

            List<string[]> entries = ReadStateFile();
            if (entries == null) return;

            try
            {
                List<DimTarget> targets = CollectTargets();
                List<DimTarget> ddc = targets.FindAll(delegate(DimTarget t) { return !t.IsWmi; });
                List<DimTarget> wmi = targets.FindAll(delegate(DimTarget t) { return t.IsWmi; });

                foreach (string[] entry in entries)
                {
                    if (entry.Length < 3) continue;

                    bool isWmi = entry[0] == "WMI";
                    List<DimTarget> pool = isWmi ? wmi : ddc;
                    int index;
                    uint original;
                    if (!int.TryParse(entry[1], out index)) continue;
                    if (!uint.TryParse(entry[2], out original)) continue;

                    if (index < 0 || index >= pool.Count)
                    {
                        Log.Write("崩溃恢复：跳过 " + entry[0] + "#" + index +
                            "（当前该通道只有 " + pool.Count + " 台，显示器拓扑可能与记录时不一致）");
                        continue;
                    }

                    ApplyTarget(pool[index], original);
                }

                ReleaseTargets(targets);
                Log.Write("检测到未恢复的调暗状态文件，已按记录还原背光（" + entries.Count + " 条）");
            }
            catch (Exception ex)
            {
                Log.Error("DisplayBrightness.RecoverFromStateFileIfAny", ex);
            }

            DeleteStateFile();
        }

        /// <summary>只读探测当前环境支持哪些亮度通道，供自检报告；不改变任何亮度。</summary>
        public static string ProbeSupport()
        {
            List<DimTarget> targets = CollectTargets();
            try
            {
                if (targets.Count == 0)
                    return "未发现支持亮度调节的显示器（DDC/CI 与 WMI 均不可用），锁屏时保持纯黑遮罩、不调背光";

                StringBuilder detail = new StringBuilder();
                detail.Append("共 ").Append(targets.Count).Append(" 台可调：");
                for (int i = 0; i < targets.Count; i++)
                {
                    DimTarget target = targets[i];
                    if (i > 0) detail.Append("；");
                    detail.Append(target.IsWmi ? "WMI 面板" : "DDC 显示器");
                    detail.Append("「").Append(target.Description).Append("」当前 ")
                          .Append(target.Original).Append("，范围 ").Append(target.Minimum).Append("~").Append(target.Maximum);
                }
                return detail.ToString();
            }
            finally
            {
                ReleaseTargets(targets);
            }
        }

        // ---------------- 目标收集 ----------------

        private static List<DimTarget> CollectTargets()
        {
            List<DimTarget> targets = new List<DimTarget>();
            HashSet<IntPtr> seen = new HashSet<IntPtr>();

            try
            {
                NativeMethods.MonitorEnumProc proc = delegate(IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
                {
                    CollectFromHmonitor(hMonitor, targets, seen);
                    return true;
                };
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Error("DisplayBrightness.CollectTargets(DDC)", ex);
            }

            try
            {
                CollectWmiTargets(targets);
            }
            catch (Exception ex)
            {
                // 台式机没有 WmiMonitorBrightness 类、或 System.Management 加载失败，都属正常
                Log.Write("WMI 亮度通道不可用: " + ex.Message);
            }

            // 按所属通道分别编号：状态文件与崩溃恢复按“类型 + 通道内序号”对位，
            // 避免 DDC/WMI 混布时全局序号与恢复侧的分组序号错开
            int ddcIndex = 0, wmiIndex = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].IsWmi) targets[i].Index = wmiIndex++;
                else targets[i].Index = ddcIndex++;
            }
            return targets;
        }

        private static void CollectFromHmonitor(IntPtr hMonitor, List<DimTarget> targets, HashSet<IntPtr> seen)
        {
            try
            {
                uint count = 0;
                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref count) || count == 0)
                    return;

                NativeMethods.PHYSICAL_MONITOR[] monitors = new NativeMethods.PHYSICAL_MONITOR[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                    return;

                for (int i = 0; i < monitors.Length; i++)
                {
                    IntPtr handle = monitors[i].hPhysicalMonitor;
                    string name = monitors[i].szPhysicalMonitorDescription;
                    if (handle == IntPtr.Zero)
                        continue;

                    // 同一块物理面板可能经多个 HMONITOR 重复上报（镜像输出）：重复的句柄也要销毁，否则泄漏
                    if (!seen.Add(handle))
                    {
                        try { NativeMethods.DestroyPhysicalMonitor(handle); } catch { }
                        continue;
                    }

                    uint minimum = 0, current = 0, maximum = 0;
                    if (!NativeMethods.GetMonitorBrightness(handle, ref minimum, ref current, ref maximum))
                    {
                        // 不支持亮度调节（多为内置虚拟屏、旧面板），立刻释放句柄并跳过
                        try { NativeMethods.DestroyPhysicalMonitor(handle); } catch { }
                        continue;
                    }

                    DimTarget target = new DimTarget();
                    target.Handle = handle;
                    target.Original = current;
                    target.Minimum = minimum;
                    target.Maximum = maximum;
                    target.IsWmi = false;
                    target.Description = string.IsNullOrEmpty(name)
                        ? ("DDC#" + targets.Count)
                        : name.Trim();
                    targets.Add(target);
                }
            }
            catch (Exception ex)
            {
                Log.Error("DisplayBrightness.CollectFromHmonitor", ex);
            }
        }

        private static void CollectWmiTargets(List<DimTarget> targets)
        {
            // 先读 WmiMonitorBrightness 拿各面板的当前亮度（恢复值），再对 WmiMonitorBrightnessMethods 逐个调
            Dictionary<string, uint> originals = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "root\\wmi", "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness"))
            using (ManagementObjectCollection brightnessItems = searcher.Get())
            {
                foreach (ManagementBaseObject item in brightnessItems)
                {
                    try
                    {
                        string instance = Convert.ToString(item["InstanceName"]);
                        object brightness = item["CurrentBrightness"];
                        if (!string.IsNullOrEmpty(instance) && brightness != null)
                            originals[instance] = Convert.ToUInt32(brightness);
                    }
                    catch { }
                }
            }

            using (ManagementClass methods = new ManagementClass("root\\wmi", "WmiMonitorBrightnessMethods", null))
            using (ManagementObjectCollection instances = methods.GetInstances())
            {
                foreach (ManagementObject instance in instances)
                {
                    try
                    {
                        string name = Convert.ToString(instance["InstanceName"]);
                        if (string.IsNullOrEmpty(name) || !originals.ContainsKey(name))
                            continue; // 没读到原亮度的面板不动，避免恢复时没有正确值

                        DimTarget target = new DimTarget();
                        target.Handle = IntPtr.Zero;
                        target.Original = originals[name];
                        target.Minimum = 0;
                        target.Maximum = 100;
                        target.IsWmi = true;
                        target.WmiObject = instance;
                        target.Description = name;
                        targets.Add(target);
                    }
                    catch { }
                }
            }
        }

        // ---------------- 亮度设置 ----------------

        private static bool ApplyTarget(DimTarget target, uint value)
        {
            try
            {
                if (target.IsWmi)
                {
                    ManagementObject wmi = target.WmiObject as ManagementObject;
                    if (wmi == null) return false;
                    object result = wmi.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, value });
                    return result == null || Convert.ToInt32(result) == 0;
                }

                if (value < target.Minimum) value = target.Minimum;
                if (value > target.Maximum) value = target.Maximum;
                return NativeMethods.SetMonitorBrightness(target.Handle, value);
            }
            catch (Exception ex)
            {
                Log.Error("DisplayBrightness.ApplyTarget(" + (target.IsWmi ? "WMI" : "DDC") + ")", ex);
                return false;
            }
        }

        private static void ReleaseTargets(List<DimTarget> targets)
        {
            foreach (DimTarget target in targets)
            {
                if (target.IsWmi)
                {
                    ManagementObject wmi = target.WmiObject as ManagementObject;
                    if (wmi != null)
                    {
                        try { wmi.Dispose(); } catch { }
                        target.WmiObject = null;
                    }
                }
                else if (target.Handle != IntPtr.Zero)
                {
                    try { NativeMethods.DestroyPhysicalMonitor(target.Handle); } catch { }
                    target.Handle = IntPtr.Zero;
                }
            }
        }

        // ---------------- 状态文件（崩溃恢复） ----------------

        /// <summary>与配置文件同目录（exe 目录，不可写时为 %APPDATA%\BlackScreenLock）。</summary>
        private static string StateFilePath()
        {
            string dir = Path.GetDirectoryName(Config.ResolvePath());
            if (string.IsNullOrEmpty(dir)) dir = ".";
            return Path.Combine(dir, StateFileName);
        }

        private static void WriteStateFile(List<DimTarget> targets)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 锁屏背光调暗状态。正常解锁/退出后会被删除；");
                sb.AppendLine("# 若本文件残留，说明上次进程在锁屏中未正常恢复，启动时会按记录还原亮度。");
                foreach (DimTarget target in targets)
                    sb.AppendLine((target.IsWmi ? "WMI|" : "DDC|") + target.Index + "|" + target.Original);
                File.WriteAllText(StateFilePath(), sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Error("DisplayBrightness.WriteStateFile", ex);
            }
        }

        /// <summary>读状态文件；文件不存在或为空返回 null。条目格式：类型|通道内序号|原亮度。</summary>
        private static List<string[]> ReadStateFile()
        {
            try
            {
                string path = StateFilePath();
                if (!File.Exists(path)) return null;

                List<string[]> entries = new List<string[]>();
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    entries.Add(line.Split('|'));
                }
                return entries.Count > 0 ? entries : null;
            }
            catch (Exception ex)
            {
                Log.Error("DisplayBrightness.ReadStateFile", ex);
                return null;
            }
        }

        private static void DeleteStateFile()
        {
            try
            {
                string path = StateFilePath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }
}
