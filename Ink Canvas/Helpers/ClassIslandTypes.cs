// This file contains local definitions of ClassIsland types for IPC proxy compatibility.
// These types must match ClassIsland.Shared types exactly (same name and namespace).

using dotnetCampus.Ipc.CompilerServices.Attributes;
using System;
using System.Linq;

namespace ClassIsland.Shared.Enums
{
    /// <summary>
    /// 当前所处的时间状态
    /// </summary>
    public enum TimeState
    {
        /// <summary>
        /// 无
        /// </summary>
        None,
        /// <summary>
        /// 上课
        /// </summary>
        OnClass,
        /// <summary>
        /// 准备上课（预留）
        /// </summary>
        PrepareOnClass,
        /// <summary>
        /// 课间休息
        /// </summary>
        Breaking,
        /// <summary>
        /// 放学
        /// </summary>
        AfterSchool,
    }
}

namespace ClassIsland.Shared.Models.Profile
{
    /// <summary>
    /// 代表一个科目
    /// </summary>
    public class Subject
    {
        private string _name = "";
        private string _initial = "";
        private string _teacherName = "";
        private bool _isOutDoor = false;

        /// <summary>
        /// 科目名
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                if (value == _name) return;
                _name = value;
                if (string.IsNullOrEmpty(Initial) && !string.IsNullOrWhiteSpace(Name))
                    Initial = Name.First().ToString();
            }
        }

        /// <summary>
        /// 科目简称
        /// </summary>
        public string Initial
        {
            get => _initial;
            set
            {
                if (value == _initial) return;
                _initial = value;
            }
        }

        /// <summary>
        /// 教师名
        /// </summary>
        public string TeacherName
        {
            get => _teacherName;
            set
            {
                if (value == _teacherName) return;
                _teacherName = value;
            }
        }

        /// <summary>
        /// 是否为户外课程
        /// </summary>
        public bool IsOutDoor
        {
            get => _isOutDoor;
            set
            {
                if (value == _isOutDoor) return;
                _isOutDoor = value;
            }
        }

        /// <summary>
        /// 代表后备科目。
        /// </summary>
        public static readonly Subject Fallback = new()
        {
            Initial = "?",
            Name = "???"
        };

        /// <summary>
        /// 代表一个空白科目。
        /// </summary>
        public static readonly Subject Empty = new()
        {
            Initial = "",
            Name = ""
        };

        /// <summary>
        /// 代表一个课间休息科目。
        /// </summary>
        public static readonly Subject Breaking = new()
        {
            Initial = "休",
            Name = "课间休息"
        };
    }

    /// <summary>
    /// 代表一个<see cref="TimeLayout"/>中的时间点。
    /// </summary>
    public class TimeLayoutItem
    {
        private TimeSpan _startTime = TimeSpan.Zero;
        private TimeSpan _endTime = TimeSpan.Zero;
        private int _timeType = 0;
        private string _breakName = "";

        /// <summary>
        /// 时间点在一天中开始的时间
        /// </summary>
        public TimeSpan StartTime
        {
            get => _startTime;
            set
            {
                if (value.Equals(_startTime)) return;
                _startTime = value;
            }
        }

        /// <summary>
        /// 时间点在一天中结束的时间
        /// </summary>
        public TimeSpan EndTime
        {
            get => _endTime;
            set
            {
                if (value.Equals(_endTime)) return;
                _endTime = value;
            }
        }

        /// <summary>
        /// 时间点类型
        /// 0 - 上课, 1 - 课间, 2 - 分割线, 3 - 行动
        /// </summary>
        public int TimeType
        {
            get => _timeType;
            set
            {
                if (value == _timeType) return;
                _timeType = value;
            }
        }

        /// <summary>
        /// 自定义课间名称，可能为空。
        /// </summary>
        public string BreakName
        {
            get => _breakName;
            set
            {
                if (_breakName == value) return;
                _breakName = value;
            }
        }

        /// <summary>
        /// 代表一个空时间点。
        /// </summary>
        public static readonly TimeLayoutItem Empty = new()
        {
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.Zero,
        };
    }
}

namespace ClassIsland.Shared.IPC.Abstractions.Services
{
    /// <summary>
    /// 向其它进程公开的课程服务，用于存储当前课表状态与信息。
    /// </summary>
    [IpcPublic(IgnoresIpcException = true)]
    public interface IPublicLessonsService
    {
        /// <summary>
        /// 主计时器是否正在工作。
        /// </summary>
        bool IsTimerRunning { get; }

        /// <summary>
        /// 当前所处时间点<see cref="ClassIsland.Shared.Models.Profile.TimeLayoutItem"/>的索引。如无，则为 -1。
        /// </summary>
        int CurrentSelectedIndex { get; set; }

        /// <summary>
        /// 当前或下一节课（下一个上课类型的时间点）的科目。如无，则为 <see cref="ClassIsland.Shared.Models.Profile.Subject.Fallback"/>。
        /// </summary>
        ClassIsland.Shared.Models.Profile.Subject NextClassSubject { get; set; }

        /// <summary>
        /// 当前或下一个课间休息类型的时间点。如无，则为 <see cref="ClassIsland.Shared.Models.Profile.TimeLayoutItem.Empty"/>。
        /// </summary>
        ClassIsland.Shared.Models.Profile.TimeLayoutItem NextBreakingTimeLayoutItem { get; set; }

        /// <summary>
        /// 当前或下一个上课类型的时间点。如无，则为 <see cref="ClassIsland.Shared.Models.Profile.TimeLayoutItem.Empty"/>。
        /// </summary>
        ClassIsland.Shared.Models.Profile.TimeLayoutItem NextClassTimeLayoutItem { get; set; }

        /// <summary>
        /// 距上课剩余时间。如果当前正在上课，或没有下一节课程，则为 <see cref="TimeSpan.Zero"/>。
        /// </summary>
        TimeSpan OnClassLeftTime { get; set; }

        /// <summary>
        /// 距下课剩余时间。如果当前不在上课，则为 <see cref="TimeSpan.Zero"/>。
        /// </summary>
        TimeSpan OnBreakingTimeLeftTime { get; set; }

        /// <summary>
        /// 当前时间点状态。
        /// </summary>
        ClassIsland.Shared.Enums.TimeState CurrentState { get; set; }

        /// <summary>
        /// 当前所处的时间点。如果当前没有时间点，则为 <see cref="ClassIsland.Shared.Models.Profile.TimeLayoutItem.Empty"/>。
        /// </summary>
        ClassIsland.Shared.Models.Profile.TimeLayoutItem CurrentTimeLayoutItem { get; set; }

        /// <summary>
        /// 当前所处时间点<see cref="ClassIsland.Shared.Models.Profile.TimeLayoutItem"/>的科目。<br/><br/>
        /// 如果当前是课间休息，则其中 <see cref="ClassIsland.Shared.Models.Profile.Subject.Name"/>(科目名) 为课间名称。<br/>
        /// 如果当前课程未定义，则为 <see cref="ClassIsland.Shared.Models.Profile.Subject.Fallback"/>。<br/>
        /// 如果当前没有时间点，或没有加载课表，则为 null。<br/>
        /// </summary>
        ClassIsland.Shared.Models.Profile.Subject? CurrentSubject { get; set; }

        /// <summary>
        /// 是否启用课表。
        /// </summary>
        bool IsClassPlanEnabled { get; set; }

        /// <summary>
        /// 是否已加载课表。
        /// </summary>
        bool IsClassPlanLoaded { get; set; }

        /// <summary>
        /// 是否已确定当前时间点。
        /// </summary>
        bool IsLessonConfirmed { get; set; }
    }
}

namespace ClassIsland.Shared.IPC
{
    /// <summary>
    /// 跨进程通信客户端，用于在其他进程中与 ClassIsland 本体进行通信。
    /// </summary>
    public class IpcClient
    {
        /// <summary>
        /// IPC 服务端管道名称
        /// </summary>
        public static string PipeName { get; } = "ClassIsland.IPC.v2.Server";

        /// <summary>
        /// IPC 提供方。
        /// </summary>
        public dotnetCampus.Ipc.Pipes.IpcProvider Provider { get; } = new dotnetCampus.Ipc.Pipes.IpcProvider();

        /// <summary>
        /// 远程的对方
        /// </summary>
        public dotnetCampus.Ipc.Pipes.PeerProxy? PeerProxy { get; private set; }

        /// <summary>
        /// JSON IPC 提供方。
        /// </summary>
        public dotnetCampus.Ipc.IpcRouteds.DirectRouteds.JsonIpcDirectRoutedProvider JsonIpcProvider { get; }

        /// <summary>
        /// 初始化一个 <see cref="IpcClient"/> 对象。
        /// </summary>
        public IpcClient()
        {
            JsonIpcProvider = new dotnetCampus.Ipc.IpcRouteds.DirectRouteds.JsonIpcDirectRoutedProvider(Provider);
        }

        /// <summary>
        /// 连接到 ClassIsland。
        /// </summary>
        public async System.Threading.Tasks.Task Connect()
        {
            Provider.StartServer();
            JsonIpcProvider.StartServer();
            PeerProxy = await Provider.GetAndConnectToPeerAsync(PipeName);
        }
    }
}
