namespace WaferMapStudio.Motion;

/// <summary>
/// 逻辑轴标识。与具体控制器无关：ACS 的 AXIS 枚举、雷赛/固高的轴号都在适配层映射到这里。
/// 值刻意从 0 连续编号，便于与控制器原生轴号做直接映射。
/// </summary>
public enum MotionAxis
{
    /// <summary>X 轴（通常是 scanning 主扫描轴）。</summary>
    X = 0,

    /// <summary>Y 轴（通常是步进/跨步轴）。</summary>
    Y = 1,

    /// <summary>Z 轴（对焦 / 升降轴）。</summary>
    Z = 2,

    /// <summary>θ 轴（旋转轴）。</summary>
    Theta = 3,

    /// <summary>预留的第 5 轴，扩展机型时使用。</summary>
    Aux1 = 4,

    /// <summary>预留的第 6 轴，扩展机型时使用。</summary>
    Aux2 = 5,
}

/// <summary>控制器通信链路状态。</summary>
public enum MotionConnectionState
{
    /// <summary>未连接。</summary>
    Disconnected,

    /// <summary>正在建立通信。</summary>
    Connecting,

    /// <summary>已连接且可下发指令。</summary>
    Connected,

    /// <summary>通信中断，适配器会按配置决定是否重连。</summary>
    Faulted,
}

/// <summary>轴使能状态（对应 ACS 的 motor enabled / disabled）。</summary>
public enum AxisEnableState
{
    /// <summary>电机未使能，不响应运动指令。</summary>
    Disabled,

    /// <summary>电机已使能，可运动。</summary>
    Enabled,
}

/// <summary>轴的当前运动状态。</summary>
public enum AxisMotionState
{
    /// <summary>静止。</summary>
    Idle,

    /// <summary>点到点运动中。</summary>
    Moving,

    /// <summary>回零运动中。</summary>
    Homing,

    /// <summary>点动（JOG）中。</summary>
    Jogging,

    /// <summary>因故障停止。</summary>
    Faulted,
}

/// <summary>点到点运动的位置模式。</summary>
public enum MoveMode
{
    /// <summary>绝对坐标，单位见 <see cref="MotionUnit"/>。</summary>
    Absolute,

    /// <summary>相对当前位置的增量。</summary>
    Relative,
}

/// <summary>位置单位。统一在接口层声明，屏蔽控制器内部的脉冲/工程单位换算。</summary>
public enum MotionUnit
{
    /// <summary>毫米。</summary>
    Millimeter,

    /// <summary>微米（半导体设备常用）。</summary>
    Micrometer,

    /// <summary>度（旋转轴）。</summary>
    Degree,

    /// <summary>控制器原始脉冲数。</summary>
    Pulse,
}

/// <summary>回零方式。</summary>
public enum HomingMode
{
    /// <summary>向负方向寻找原点后回零。</summary>
    Negative,

    /// <summary>向正方向寻找原点后回零。</summary>
    Positive,

    /// <summary>直接把当前位置设为零（不改变机械位置）。</summary>
    Here,
}

/// <summary>停止方式。</summary>
public enum StopMode
{
    /// <summary>按减速曲线平滑停止。</summary>
    Decelerated,

    /// <summary>立即停止，可能丢步。</summary>
    Immediate,

    /// <summary>紧急停止（对应控制器 ESTOP / KILL）。</summary>
    Emergency,
}
