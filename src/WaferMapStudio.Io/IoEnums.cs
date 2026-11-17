namespace WaferMapStudio.Io;

/// <summary>点位方向。与真实点表的 IOModule 前缀 IN_/OUT_ 对应。</summary>
public enum IoDirection
{
    /// <summary>输入（DI），只读。</summary>
    Input,

    /// <summary>输出（DO），可写。</summary>
    Output,
}

/// <summary>IO 链路状态。与运动/光源层状态机语义保持一致。</summary>
public enum IoConnectionState
{
    /// <summary>未连接。</summary>
    Disconnected,

    /// <summary>正在建链。</summary>
    Connecting,

    /// <summary>已连接、点表已加载、可读写。</summary>
    Ready,

    /// <summary>通信故障，需重连。</summary>
    Faulted,
}

/// <summary>
/// 把强类型 <see cref="IoPoint"/> 绑定到点表原始字段的元数据。
/// 绑定信息放在枚举成员上，使映射表与枚举定义同处一地，避免两处漂移。
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class IoPointBindingAttribute : Attribute
{
    /// <summary>创建绑定元数据。</summary>
    /// <param name="sourceName">点表中的 IOParameterName。</param>
    /// <param name="direction">方向；用于区分输入/输出同名点（如 DoorLock1）。</param>
    public IoPointBindingAttribute(string sourceName, IoDirection direction)
    {
        SourceName = sourceName;
        Direction = direction;
    }

    /// <summary>点表中的 IOParameterName。</summary>
    public string SourceName { get; }

    /// <summary>方向。</summary>
    public IoDirection Direction { get; }
}

/// <summary>
/// 规范逻辑点位集合，逐一对应现场点表中 38 个有效命名点。
/// 输入/输出同名的 DoorLock1/2/3、SafeDoor 申请等用 Status/Control、Request/Permit 后缀区分，
/// 保证枚举成员名唯一且语义可读。值为整数便于日志与序列化。
/// </summary>
public enum IoPoint
{
    // ---- 输入：门锁 / 动力 / 气压 ----
    [IoPointBinding("DoorLock1", IoDirection.Input)] DoorLock1Status = 1,
    [IoPointBinding("DoorLock2", IoDirection.Input)] DoorLock2Status = 2,
    [IoPointBinding("DoorLock3", IoDirection.Input)] DoorLock3Status = 3,
    [IoPointBinding("KMI", IoDirection.Input)] AcsPowerStatus = 4,
    [IoPointBinding("ZAxisBreak", IoDirection.Input)] ZAxisBrakeStatus = 5,
    [IoPointBinding("TotalPositivePressureDetection", IoDirection.Input)] TotalPositivePressureOk = 6,
    [IoPointBinding("TotalNegativePressureDetection", IoDirection.Input)] TotalNegativePressureOk = 7,
    [IoPointBinding("NegativePressure6", IoDirection.Input)] Vacuum6Ok = 8,
    [IoPointBinding("NegativePressure8", IoDirection.Input)] Vacuum8Ok = 9,
    [IoPointBinding("IronRod", IoDirection.Input)] IonizerFault = 10,

    // ---- 输入：安全继电器 / 安全门 / 联锁 ----
    [IoPointBinding("SK1State", IoDirection.Input)] SafetyRelay1Ok = 11,
    [IoPointBinding("SK2State", IoDirection.Input)] SafetyRelay2Ok = 12,
    [IoPointBinding("SafeDoorLock1", IoDirection.Input)] SafeDoor1Locked = 13,
    [IoPointBinding("SafeDoorLock2", IoDirection.Input)] SafeDoor2Locked = 14,
    [IoPointBinding("SafeDoorLock3", IoDirection.Input)] SafeDoor3Locked = 15,
    [IoPointBinding("GetApplyOpenSafeDoor", IoDirection.Input)] SafeDoorOpenRequest = 16,
    [IoPointBinding("INTERLOCK0", IoDirection.Input)] Interlock0 = 17,
    [IoPointBinding("INTERLOCK1", IoDirection.Input)] Interlock1 = 18,
    [IoPointBinding("INTERLOCK2", IoDirection.Input)] Interlock2 = 19,
    [IoPointBinding("INTERLOCK3", IoDirection.Input)] Interlock3 = 20,

    // ---- 输出：信号灯柱 / 蜂鸣器 / 相机触发 ----
    [IoPointBinding("RED", IoDirection.Output)] StackLightRed = 101,
    [IoPointBinding("YELLOW", IoDirection.Output)] StackLightYellow = 102,
    [IoPointBinding("GREEN", IoDirection.Output)] StackLightGreen = 103,
    [IoPointBinding("BLUE", IoDirection.Output)] StackLightBlue = 104,
    [IoPointBinding("BUZZER", IoDirection.Output)] Buzzer = 105,
    [IoPointBinding("CameraTrigger", IoDirection.Output)] CameraTrigger = 106,

    // ---- 输出：门锁 / 真空 /  peripheral ----
    [IoPointBinding("DoorLock1", IoDirection.Output)] DoorLock1Control = 107,
    [IoPointBinding("DoorLock2", IoDirection.Output)] DoorLock2Control = 108,
    [IoPointBinding("DoorLock3", IoDirection.Output)] DoorLock3Control = 109,
    [IoPointBinding("SetApplyOpenSafeDoor", IoDirection.Output)] SafeDoorOpenPermit = 110,
    [IoPointBinding("Vacuum6", IoDirection.Output)] Vacuum6On = 111,
    [IoPointBinding("Vacuum8", IoDirection.Output)] Vacuum8On = 112,
    [IoPointBinding("IronRodOn", IoDirection.Output)] IonizerOn = 113,
    [IoPointBinding("K16Rest", IoDirection.Output)] EstopReset = 114,
    [IoPointBinding("K5Light", IoDirection.Output)] EquipmentLight = 115,
    [IoPointBinding("TrackLaser", IoDirection.Output)] FocusLaser = 116,
    [IoPointBinding("ArmExtendEnable", IoDirection.Output)] ArmExtendEnable = 117,
    [IoPointBinding("DoorOpen", IoDirection.Output)] DoorOpen = 118,
}
