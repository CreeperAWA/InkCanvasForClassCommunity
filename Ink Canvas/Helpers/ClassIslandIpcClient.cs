using dotnetCampus.Ipc.CompilerServices.GeneratedProxies;
using dotnetCampus.Ipc.IpcRouteds.DirectRouteds;
using dotnetCampus.Ipc.Pipes;
using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// ClassIsland IPC 客户端，用于获取课程信息和监听课程事件
    /// 管道名称：ClassIsland.IPC.v2.Server
    /// 底层库：dotnetCampus.Ipc
    /// 
    /// 时间偏移处理：
    /// - ClassIsland 的 TimeState 已经考虑了时间偏移（学校广播时间与 NTP 时间的差异）
    /// - 我们只需要监听 TimeState 的变化即可，无需手动计算时间
    /// </summary>
    public sealed class ClassIslandIpcClient : IDisposable
    {
        private static ClassIslandIpcClient _instance;
        private static readonly object _instanceLock = new object();

        public static ClassIslandIpcClient Instance
        {
            get
            {
                if (_instance == null)
                    lock (_instanceLock)
                        if (_instance == null)
                            _instance = new ClassIslandIpcClient();
                return _instance;
            }
        }

        // IPC 服务端管道名称
        private const string PipeName = "ClassIsland.IPC.v2.Server";

        // 通知 ID
        private const string OnClassNotifyId = "classisland.lessonsService.onClass";
        private const string OnBreakingTimeNotifyId = "classisland.lessonsService.onBreakingTime";
        private const string OnAfterSchoolNotifyId = "classisland.lessonsService.onAfterSchool";
        private const string CurrentTimeStateChangedNotifyId = "classisland.lessonsService.currentTimeStateChanged";

        private ClassIsland.Shared.IPC.IpcClient _ipcClient;
        private ClassIsland.Shared.IPC.Abstractions.Services.IPublicLessonsService _lessonsServiceProxy;
        private bool _isConnected;
        private bool _disposed;
        private readonly object _connectionLock = new object();
        private readonly DispatcherTimer _reconnectTimer;
        private readonly DispatcherTimer _stateCheckTimer;

        // 上一次触发的状态，用于避免重复触发
        private ClassIsland.Shared.Enums.TimeState _lastTriggeredState = ClassIsland.Shared.Enums.TimeState.None;

        public event Action<ClassIslandClassEventInfo> ClassStarted;
        public event Action<ClassIslandClassEventInfo> ClassEnded;
        public event Action Connected;
        public event Action Disconnected;

        public bool IsConnected => _isConnected;

        private const int ReconnectIntervalMs = 5000;
        private const int StateCheckIntervalMs = 1000;

        private ClassIslandIpcClient()
        {
            _reconnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ReconnectIntervalMs)
            };
            _reconnectTimer.Tick += async (s, e) =>
            {
                if (!_isConnected && !_disposed)
                {
                    await TryConnectAsync();
                }
            };

            _stateCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(StateCheckIntervalMs)
            };
            _stateCheckTimer.Tick += (s, e) => CheckTimeState();
        }

        /// <summary>
        /// 开始连接 ClassIsland 并持续监听
        /// </summary>
        public void StartListening()
        {
            if (_disposed) return;
            if (_ipcClient != null) return;

            LogHelper.WriteLogToFile("[ClassIsland IPC] 开始监听", LogHelper.LogType.Event);

            _ipcClient = new ClassIsland.Shared.IPC.IpcClient();

            // 注册事件处理器
            _ipcClient.JsonIpcProvider.AddNotifyHandler(OnClassNotifyId, () => OnNotifyReceived("上课事件通知"));
            _ipcClient.JsonIpcProvider.AddNotifyHandler(OnBreakingTimeNotifyId, () => OnNotifyReceived("课间休息事件通知"));
            _ipcClient.JsonIpcProvider.AddNotifyHandler(OnAfterSchoolNotifyId, () => OnNotifyReceived("放学事件通知"));
            _ipcClient.JsonIpcProvider.AddNotifyHandler(CurrentTimeStateChangedNotifyId, () => OnNotifyReceived("时间状态变化通知"));

            _ = TryConnectAsync();
            _reconnectTimer.Start();
        }

        /// <summary>
        /// 停止监听并断开连接
        /// </summary>
        public void StopListening()
        {
            LogHelper.WriteLogToFile("[ClassIsland IPC] 停止监听", LogHelper.LogType.Event);
            _reconnectTimer.Stop();
            _stateCheckTimer.Stop();
            Disconnect();
        }

        private async Task TryConnectAsync()
        {
            if (_disposed || _isConnected || _ipcClient == null) return;

            try
            {
                LogHelper.WriteLogToFile("[ClassIsland IPC] 正在连接...", LogHelper.LogType.Trace);
                await _ipcClient.Connect();

                // 创建课程服务代理
                _lessonsServiceProxy = _ipcClient.Provider.CreateIpcProxy<ClassIsland.Shared.IPC.Abstractions.Services.IPublicLessonsService>(_ipcClient.PeerProxy!);

                lock (_connectionLock)
                {
                    _isConnected = true;
                }

                LogHelper.WriteLogToFile("[ClassIsland IPC] 连接成功", LogHelper.LogType.Event);

                // 启动状态检查定时器
                _stateCheckTimer.Start();

                Connected?.Invoke();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland IPC] 连接失败: {ex.Message}", LogHelper.LogType.Trace);
                Disconnect();
            }
        }

        private void OnNotifyReceived(string notifyType)
        {
            LogHelper.WriteLogToFile($"[ClassIsland IPC] 收到通知: {notifyType}", LogHelper.LogType.Event);
            // 通知到达后，立即触发一次状态检查
            CheckTimeState();
        }

        private void CheckTimeState()
        {
            if (!_isConnected || _lessonsServiceProxy == null) return;

            try
            {
                var currentState = _lessonsServiceProxy.CurrentState;
                var currentTimeLayoutItem = _lessonsServiceProxy.CurrentTimeLayoutItem;
                var currentSubject = _lessonsServiceProxy.CurrentSubject;
                var nextClassSubject = _lessonsServiceProxy.NextClassSubject;

                // 只在状态变化时触发事件
                if (currentState == _lastTriggeredState) return;

                LogHelper.WriteLogToFile(
                    $"[ClassIsland IPC] 状态变化: {_lastTriggeredState} -> {currentState}, " +
                    $"当前科目={currentSubject?.Name ?? "无"}, " +
                    $"当前老师={currentSubject?.TeacherName ?? "无"}, " +
                    $"下节科目={nextClassSubject?.Name ?? "无"}, " +
                    $"下节老师={nextClassSubject?.TeacherName ?? "无"}, " +
                    $"时间区间={currentTimeLayoutItem?.StartTime:hh\\:mm\\:ss}-{currentTimeLayoutItem?.EndTime:hh\\:mm\\:ss}",
                    LogHelper.LogType.Event);

                _lastTriggeredState = currentState;

                // 根据状态触发事件
                switch (currentState)
                {
                    case ClassIsland.Shared.Enums.TimeState.OnClass:
                        LogHelper.WriteLogToFile($"[ClassIsland IPC] 触发事件: 上课 - {currentSubject?.Name} (老师: {currentSubject?.TeacherName})", LogHelper.LogType.Event);
                        ClassStarted?.Invoke(new ClassIslandClassEventInfo
                        {
                            EventType = ClassIslandClassEventType.ClassStarted,
                            CurrentSubject = currentSubject?.Name ?? "",
                            CurrentTeacher = currentSubject?.TeacherName ?? "",
                            NextSubject = nextClassSubject?.Name ?? "",
                            NextTeacher = nextClassSubject?.TeacherName ?? "",
                            TimeState = currentState,
                            StartTime = currentTimeLayoutItem?.StartTime ?? TimeSpan.Zero,
                            EndTime = currentTimeLayoutItem?.EndTime ?? TimeSpan.Zero
                        });
                        break;

                    case ClassIsland.Shared.Enums.TimeState.Breaking:
                        LogHelper.WriteLogToFile($"[ClassIsland IPC] 触发事件: 课间休息 - 当前老师: {currentSubject?.TeacherName}, 下节老师: {nextClassSubject?.TeacherName}", LogHelper.LogType.Event);
                        ClassEnded?.Invoke(new ClassIslandClassEventInfo
                        {
                            EventType = ClassIslandClassEventType.ClassEnded,
                            CurrentSubject = currentSubject?.Name ?? "",
                            CurrentTeacher = currentSubject?.TeacherName ?? "",
                            NextSubject = nextClassSubject?.Name ?? "",
                            NextTeacher = nextClassSubject?.TeacherName ?? "",
                            TimeState = currentState,
                            StartTime = currentTimeLayoutItem?.StartTime ?? TimeSpan.Zero,
                            EndTime = currentTimeLayoutItem?.EndTime ?? TimeSpan.Zero
                        });
                        break;

                    case ClassIsland.Shared.Enums.TimeState.AfterSchool:
                        LogHelper.WriteLogToFile($"[ClassIsland IPC] 触发事件: 放学 - 当前科目: {currentSubject?.Name}", LogHelper.LogType.Event);
                        ClassEnded?.Invoke(new ClassIslandClassEventInfo
                        {
                            EventType = ClassIslandClassEventType.AfterSchool,
                            CurrentSubject = currentSubject?.Name ?? "",
                            CurrentTeacher = currentSubject?.TeacherName ?? "",
                            NextSubject = "",
                            NextTeacher = "",
                            TimeState = currentState,
                            StartTime = currentTimeLayoutItem?.StartTime ?? TimeSpan.Zero,
                            EndTime = currentTimeLayoutItem?.EndTime ?? TimeSpan.Zero
                        });
                        break;

                    case ClassIsland.Shared.Enums.TimeState.None:
                        LogHelper.WriteLogToFile($"[ClassIsland IPC] 触发事件: 无状态", LogHelper.LogType.Trace);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland IPC] 状态检查失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 获取当前课程信息
        /// </summary>
        public LessonInfo GetCurrentLessonInfo()
        {
            if (!_isConnected || _lessonsServiceProxy == null) return null;

            try
            {
                var subject = _lessonsServiceProxy.CurrentSubject;
                if (subject == null) return null;

                return new LessonInfo
                {
                    Name = subject.Name,
                    Teacher = subject.TeacherName
                };
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland IPC] 获取当前课程信息失败: {ex.Message}", LogHelper.LogType.Warning);
            }

            return null;
        }

        /// <summary>
        /// 获取下一节课信息
        /// </summary>
        public LessonInfo GetNextLessonInfo()
        {
            if (!_isConnected || _lessonsServiceProxy == null) return null;

            try
            {
                var subject = _lessonsServiceProxy.NextClassSubject;
                if (subject == null) return null;

                return new LessonInfo
                {
                    Name = subject.Name,
                    Teacher = subject.TeacherName
                };
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"[ClassIsland IPC] 获取下节课信息失败: {ex.Message}", LogHelper.LogType.Warning);
            }

            return null;
        }

        private void HandleConnectionLost()
        {
            if (!_isConnected) return;

            LogHelper.WriteLogToFile("[ClassIsland IPC] 连接丢失", LogHelper.LogType.Warning);
            Disconnect();
            Disconnected?.Invoke();
        }

        private void Disconnect()
        {
            lock (_connectionLock)
            {
                _isConnected = false;
            }

            _stateCheckTimer.Stop();

            try
            {
                _ipcClient?.Provider?.Dispose();
            }
            catch { }

            _ipcClient = null;
            _lessonsServiceProxy = null;
            _lastTriggeredState = ClassIsland.Shared.Enums.TimeState.None;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopListening();
        }
    }

    /// <summary>
    /// 课程信息
    /// </summary>
    public class LessonInfo
    {
        public string Name { get; set; }
        public string Teacher { get; set; }
    }

    /// <summary>
    /// ClassIsland 课程事件信息
    /// </summary>
    public class ClassIslandClassEventInfo
    {
        public ClassIslandClassEventType EventType { get; set; }
        public string CurrentSubject { get; set; } = "";
        public string CurrentTeacher { get; set; } = "";
        public string NextSubject { get; set; } = "";
        public string NextTeacher { get; set; } = "";
        public ClassIsland.Shared.Enums.TimeState TimeState { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
    }

    /// <summary>
    /// ClassIsland 课程事件类型
    /// </summary>
    public enum ClassIslandClassEventType
    {
        ClassStarted,    // 上课
        ClassEnded,      // 课间休息（下课）
        AfterSchool      // 放学
    }
}
