using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// ClassIsland 自动冻结管理器
    /// 根据 ClassIsland 课程信息自动判断并冻结白板笔迹
    /// 
    /// 判断逻辑：
    /// 1. 上课时记录当前老师信息
    /// 2. 课间休息/放学时，使用之前记录的老师与下节课比较
    ///    - 如果为同一位老师连堂，不执行冻结
    ///    - 如果为不同老师，或下节课信息为空，或放学，则启动倒计时
    /// 3. 倒计时结束时检查页面操作：
    ///    - 无操作 → 立即冻结
    ///    - 有操作 → 等待 1 分钟，1 分钟内无操作则冻结
    /// 4. 如果在此期间进入下一节课（上课事件），立即冻结
    /// 
    /// 时间偏移处理：
    /// - ClassIsland 的 TimeState 已经考虑了时间偏移（学校广播时间与 NTP 时间的差异）
    /// - 我们只需要监听 TimeState 的变化即可，无需手动计算时间
    /// </summary>
    public sealed class ClassIslandAutoFreezeManager : IDisposable
    {
        private static ClassIslandAutoFreezeManager _instance;
        private static readonly object _instanceLock = new object();

        // 等待无操作的时长（秒）
        private const int WaitNoOperationSeconds = 60;

        public static ClassIslandAutoFreezeManager Instance
        {
            get
            {
                if (_instance == null)
                    lock (_instanceLock)
                        if (_instance == null)
                            _instance = new ClassIslandAutoFreezeManager();
                return _instance;
            }
        }

        private readonly DispatcherTimer _countdownTimer;
        private readonly DispatcherTimer _waitNoOperationTimer;
        
        // 当前上课的老师（上课时记录，课间休息时用于比较）
        private string _currentTeacher = "";
        private string _currentSubject = "";
        
        // 倒计时相关
        private int? _recordedPageIndex;
        private DateTime _recordedTimeUtc;
        private bool _disposed;
        
        // 冻结阶段状态
        private FreezeState _freezeState = FreezeState.None;

        private MainWindow _mainWindow;

        public ClassIslandAutoFreezeManager()
        {
            _countdownTimer = new DispatcherTimer();
            _countdownTimer.Tick += OnCountdownTimerTick;
            
            _waitNoOperationTimer = new DispatcherTimer();
            _waitNoOperationTimer.Tick += OnWaitNoOperationTimerTick;
        }

        /// <summary>
        /// 初始化并启动自动冻结功能
        /// </summary>
        public void Initialize(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;

            var ipcClient = ClassIslandIpcClient.Instance;
            ipcClient.ClassStarted += OnClassStarted;
            ipcClient.ClassEnded += OnClassEnded;

            ipcClient.StartListening();
        }

        /// <summary>
        /// 停止自动冻结功能
        /// </summary>
        public void Uninitialize()
        {
            var ipcClient = ClassIslandIpcClient.Instance;
            ipcClient.ClassStarted -= OnClassStarted;
            ipcClient.ClassEnded -= OnClassEnded;

            ipcClient.StopListening();
            _countdownTimer.Stop();
            _waitNoOperationTimer.Stop();
            _currentTeacher = "";
            _currentSubject = "";
            _freezeState = FreezeState.None;
        }

        /// <summary>
        /// 上课事件处理：记录老师信息，取消待执行的冻结
        /// </summary>
        private void OnClassStarted(ClassIslandClassEventInfo eventInfo)
        {
            try
            {
                string teacher = eventInfo.CurrentTeacher?.Trim() ?? "";
                string subject = eventInfo.CurrentSubject?.Trim() ?? "";

                LogHelper.WriteLogToFile(
                    $"[ClassIsland 冻结] 上课事件: 科目={subject}, " +
                    $"老师={teacher}, " +
                    $"时间区间={eventInfo.StartTime:hh\\:mm\\:ss}-{eventInfo.EndTime:hh\\:mm\\:ss}", 
                    LogHelper.LogType.Event);

                // 如果处于等待无操作状态或倒计时状态，说明在等待冻结期间进入了下一节课
                // 立即冻结旧课程
                if (_freezeState == FreezeState.WaitingNoOperation || _freezeState == FreezeState.Countdown)
                {
                    LogHelper.WriteLogToFile(
                        $"[ClassIsland 冻结] 进入下一节课，立即冻结旧课程 (当前状态: {_freezeState})", 
                        LogHelper.LogType.Event);
                    ExecuteFreezeImmediately();
                }

                // 如果之前有老师记录，且与当前老师不同，说明换老师了
                if (!string.IsNullOrEmpty(_currentTeacher) && 
                    !string.IsNullOrEmpty(teacher) && 
                    !string.Equals(_currentTeacher, teacher, StringComparison.OrdinalIgnoreCase))
                {
                    LogHelper.WriteLogToFile(
                        $"[ClassIsland 冻结] 换老师了: {_currentTeacher} -> {teacher}，检查是否冻结旧课程", 
                        LogHelper.LogType.Event);
                }

                // 更新当前老师信息
                _currentTeacher = teacher;
                _currentSubject = subject;
                
                // 上课时取消任何待执行的冻结（老师开始上课了，页面会被使用）
                CancelPendingFreeze();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland 冻结] 处理上课事件失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 下课事件处理：比较老师，决定是否冻结
        /// </summary>
        private void OnClassEnded(ClassIslandClassEventInfo eventInfo)
        {
            try
            {
                LogHelper.WriteLogToFile(
                    $"[ClassIsland 冻结] 下课事件: 类型={eventInfo.EventType}, " +
                    $"记录的老师={_currentTeacher}, " +
                    $"下节科目={eventInfo.NextSubject}, " +
                    $"下节老师={eventInfo.NextTeacher}, " +
                    $"时间区间={eventInfo.StartTime:hh\\:mm\\:ss}-{eventInfo.EndTime:hh\\:mm\\:ss}", 
                    LogHelper.LogType.Event);

                CheckAndScheduleFreeze(eventInfo);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland 冻结] 处理下课事件失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 检查是否应该执行冻结，并启动倒计时
        /// </summary>
        private void CheckAndScheduleFreeze(ClassIslandClassEventInfo eventInfo)
        {
            try
            {
                if (_mainWindow == null)
                {
                    LogHelper.WriteLogToFile("[ClassIsland 冻结] 主窗口为空，跳过冻结", LogHelper.LogType.Warning);
                    return;
                }

                if (MainWindow.Settings != null && !MainWindow.Settings.Automation.IsEnableClassIslandAutoFreeze)
                {
                    LogHelper.WriteLogToFile("[ClassIsland 冻结] 功能未启用，跳过冻结", LogHelper.LogType.Trace);
                    return;
                }

                // 放学事件，直接执行冻结
                if (eventInfo.EventType == ClassIslandClassEventType.AfterSchool)
                {
                    LogHelper.WriteLogToFile("[ClassIsland 冻结] 放学事件，执行冻结", LogHelper.LogType.Event);
                    ScheduleFreeze();
                    // 清空老师信息（放学了）
                    _currentTeacher = "";
                    _currentSubject = "";
                    return;
                }

                // 课间休息事件，使用之前记录的老师判断
                string lastTeacher = _currentTeacher?.Trim() ?? "";
                string nextTeacher = eventInfo.NextTeacher?.Trim() ?? "";

                // 下节课信息为空，执行冻结
                if (string.IsNullOrEmpty(nextTeacher))
                {
                    LogHelper.WriteLogToFile("[ClassIsland 冻结] 下节课老师信息为空，执行冻结", LogHelper.LogType.Event);
                    ScheduleFreeze();
                    return;
                }

                // 之前没有老师记录，不执行冻结（可能是课程表刚开始）
                if (string.IsNullOrEmpty(lastTeacher))
                {
                    LogHelper.WriteLogToFile("[ClassIsland 冻结] 无当前老师记录，不执行冻结", LogHelper.LogType.Trace);
                    return;
                }

                // 记录的老师与下节课老师相同，不执行冻结（同一位老师连堂）
                if (string.Equals(lastTeacher, nextTeacher, StringComparison.OrdinalIgnoreCase))
                {
                    LogHelper.WriteLogToFile(
                        $"[ClassIsland 冻结] 当前老师({lastTeacher})与下节课老师({nextTeacher})相同，不执行冻结", 
                        LogHelper.LogType.Event);
                    return;
                }

                // 记录的老师与下节课老师不同，准备冻结
                LogHelper.WriteLogToFile(
                    $"[ClassIsland 冻结] 当前老师({lastTeacher})与下节课老师({nextTeacher})不同，准备冻结", 
                    LogHelper.LogType.Event);
                ScheduleFreeze();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland 冻结] 检查冻结条件失败: {ex.Message}", LogHelper.LogType.Error);
            }
        }

        /// <summary>
        /// 启动冻结倒计时
        /// </summary>
        private void ScheduleFreeze()
        {
            try
            {
                if (_mainWindow == null) return;

                int delaySeconds = MainWindow.Settings.Automation.ClassIslandAutoFreezeDelaySeconds;
                var delay = TimeSpan.FromSeconds(Math.Max(1, delaySeconds));

                _recordedPageIndex = _mainWindow.GetCurrentFreezePageIndex();
                _recordedTimeUtc = DateTime.UtcNow;
                _freezeState = FreezeState.Countdown;

                _countdownTimer.Interval = delay;
                _countdownTimer.Stop();
                _countdownTimer.Start();

                LogHelper.WriteLogToFile(
                    $"[ClassIsland 冻结] 倒计时开始: {delaySeconds}秒, 页面={_recordedPageIndex}, " +
                    $"记录时间={_recordedTimeUtc:HH:mm:ss.fff}, 状态={_freezeState}", 
                    LogHelper.LogType.Event);

                _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    string message;
                    if (delaySeconds >= 60)
                    {
                        var minutes = delaySeconds / 60;
                        message = $"ClassIsland：将在 {minutes} 分钟后检查并自动冻结页面";
                    }
                    else
                    {
                        message = $"ClassIsland：将在 {delaySeconds} 秒后检查并自动冻结页面";
                    }

                    LogHelper.WriteLogToFile($"[ClassIsland 冻结] 显示通知: {message}", LogHelper.LogType.Trace);
                    _mainWindow.ShowNotification(message);
                }));
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland 冻结] 启动倒计时失败: {ex.Message}", LogHelper.LogType.Error);
            }
        }

        /// <summary>
        /// 取消待执行的冻结
        /// </summary>
        public void CancelPendingFreeze()
        {
            try
            {
                bool wasActive = _countdownTimer.IsEnabled;
                bool wasWaiting = _waitNoOperationTimer.IsEnabled;
                _countdownTimer.Stop();
                _waitNoOperationTimer.Stop();
                _recordedPageIndex = null;
                _freezeState = FreezeState.None;

                if (wasActive || wasWaiting)
                {
                    LogHelper.WriteLogToFile("[ClassIsland 冻结] 倒计时/等待已取消", LogHelper.LogType.Event);
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland 冻结] 取消倒计时失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 立即执行冻结（用于换老师或进入下一节课时）
        /// </summary>
        private void ExecuteFreezeImmediately()
        {
            try
            {
                _countdownTimer.Stop();
                _waitNoOperationTimer.Stop();

                if (!_recordedPageIndex.HasValue || _mainWindow == null)
                {
                    LogHelper.WriteLogToFile("[ClassIsland 冻结] 立即冻结：无记录页面或主窗口为空", LogHelper.LogType.Warning);
                    _recordedPageIndex = null;
                    _freezeState = FreezeState.None;
                    return;
                }

                int pageIndex = _recordedPageIndex.Value;
                _recordedPageIndex = null;
                _freezeState = FreezeState.None;

                LogHelper.WriteLogToFile($"[ClassIsland 冻结] 立即冻结页面 {pageIndex}", LogHelper.LogType.Event);

                _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _mainWindow.FreezePage(pageIndex, true);
                    _mainWindow.ShowNotification($"ClassIsland：检测到进入下一节课，页面 {pageIndex} 已自动冻结");
                    LogHelper.WriteLogToFile($"[ClassIsland 冻结] 页面 {pageIndex} 已立即冻结", LogHelper.LogType.Event);
                }));
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland 冻结] 立即冻结失败: {ex.Message}", LogHelper.LogType.Error);
            }
        }

        /// <summary>
        /// 倒计时结束时的处理
        /// </summary>
        private void OnCountdownTimerTick(object sender, EventArgs e)
        {
            try
            {
                _countdownTimer.Stop();

                if (!_recordedPageIndex.HasValue)
                {
                    LogHelper.WriteLogToFile("[ClassIsland 冻结] 倒计时结束，但无记录页面", LogHelper.LogType.Warning);
                    _freezeState = FreezeState.None;
                    return;
                }

                if (_mainWindow == null)
                {
                    LogHelper.WriteLogToFile("[ClassIsland 冻结] 倒计时结束，但主窗口为空", LogHelper.LogType.Warning);
                    _freezeState = FreezeState.None;
                    return;
                }

                var elapsed = DateTime.UtcNow - _recordedTimeUtc;
                LogHelper.WriteLogToFile(
                    $"[ClassIsland 冻结] 倒计时结束，检查页面墨迹变化: 页面={_recordedPageIndex}, " +
                    $"记录时间={_recordedTimeUtc:HH:mm:ss.fff}, 已过时间={elapsed.TotalSeconds:F1}秒", 
                    LogHelper.LogType.Event);

                // 捕获页面索引到局部变量，避免异步回调时状态被其他操作改变
                int pageIndex = _recordedPageIndex.Value;

                bool hasInkMutation = _mainWindow.HasPageInkMutationSince(pageIndex, _recordedTimeUtc);

                if (!hasInkMutation)
                {
                    // 无操作，立即冻结
                    LogHelper.WriteLogToFile($"[ClassIsland 冻结] 页面 {pageIndex} 无操作，执行冻结", LogHelper.LogType.Event);
                    _recordedPageIndex = null;
                    _freezeState = FreezeState.None;

                    _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _mainWindow.FreezePage(pageIndex, true);
                        _mainWindow.ShowNotification("ClassIsland：页面已自动冻结");
                        LogHelper.WriteLogToFile($"[ClassIsland 冻结] 页面 {pageIndex} 已冻结", LogHelper.LogType.Event);
                    }));
                }
                else
                {
                    // 有操作，等待 1 分钟
                    LogHelper.WriteLogToFile($"[ClassIsland 冻结] 页面 {pageIndex} 有操作，等待 {WaitNoOperationSeconds} 秒后检查", LogHelper.LogType.Event);
                    _freezeState = FreezeState.WaitingNoOperation;
                    _recordedTimeUtc = DateTime.UtcNow;  // 更新记录时间

                    _waitNoOperationTimer.Interval = TimeSpan.FromSeconds(WaitNoOperationSeconds);
                    _waitNoOperationTimer.Stop();
                    _waitNoOperationTimer.Start();

                    _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _mainWindow.ShowNotification($"ClassIsland：检测到页面操作，将在 {WaitNoOperationSeconds} 秒后检查");
                    }));
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland 冻结] 执行冻结失败: {ex.Message}", LogHelper.LogType.Error);
                _freezeState = FreezeState.None;
            }
        }

        /// <summary>
        /// 等待无操作倒计时结束时的处理
        /// </summary>
        private void OnWaitNoOperationTimerTick(object sender, EventArgs e)
        {
            try
            {
                _waitNoOperationTimer.Stop();

                if (!_recordedPageIndex.HasValue || _mainWindow == null)
                {
                    LogHelper.WriteLogToFile("[ClassIsland 冻结] 等待无操作倒计时结束，但无记录页面或主窗口为空", LogHelper.LogType.Warning);
                    _freezeState = FreezeState.None;
                    return;
                }

                int pageIndex = _recordedPageIndex.Value;
                
                LogHelper.WriteLogToFile(
                    $"[ClassIsland 冻结] 等待无操作倒计时结束，检查页面墨迹变化: 页面={pageIndex}", 
                    LogHelper.LogType.Event);

                bool hasInkMutation = _mainWindow.HasPageInkMutationSince(pageIndex, _recordedTimeUtc);

                if (!hasInkMutation)
                {
                    // 无操作，冻结
                    LogHelper.WriteLogToFile($"[ClassIsland 冻结] 页面 {pageIndex} 无操作，执行冻结", LogHelper.LogType.Event);
                    _recordedPageIndex = null;
                    _freezeState = FreezeState.None;

                    _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _mainWindow.FreezePage(pageIndex, true);
                        _mainWindow.ShowNotification("ClassIsland：页面已自动冻结");
                        LogHelper.WriteLogToFile($"[ClassIsland 冻结] 页面 {pageIndex} 已冻结", LogHelper.LogType.Event);
                    }));
                }
                else
                {
                    // 仍有操作，继续等待（再等 1 分钟）
                    LogHelper.WriteLogToFile($"[ClassIsland 冻结] 页面 {pageIndex} 仍有操作，继续等待 {WaitNoOperationSeconds} 秒", LogHelper.LogType.Event);
                    _recordedTimeUtc = DateTime.UtcNow;

                    _waitNoOperationTimer.Interval = TimeSpan.FromSeconds(WaitNoOperationSeconds);
                    _waitNoOperationTimer.Stop();
                    _waitNoOperationTimer.Start();

                    _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _mainWindow.ShowNotification($"ClassIsland：页面仍有操作，将在 {WaitNoOperationSeconds} 秒后再次检查");
                    }));
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland 冻结] 等待无操作倒计时失败: {ex.Message}", LogHelper.LogType.Error);
                _freezeState = FreezeState.None;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            LogHelper.WriteLogToFile("[ClassIsland 冻结] 管理器释放", LogHelper.LogType.Event);

            Uninitialize();
            _countdownTimer.Stop();
            _waitNoOperationTimer.Stop();
        }
    }

    /// <summary>
    /// 冻结状态
    /// </summary>
    internal enum FreezeState
    {
        None,                // 无操作
        Countdown,           // 倒计时中
        WaitingNoOperation   // 等待页面无操作
    }
}
