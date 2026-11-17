namespace WaferMapStudio.Lighting;

/// <summary>通信链路状态。命名中立，与具体总线（串口/网口/USB）无关。</summary>
public enum LightConnectionState
{
    /// <summary>未连接。</summary>
    Disconnected,

    /// <summary>正在建立连接。</summary>
    Connecting,

    /// <summary>已连接，可收发指令。</summary>
    Connected,

    /// <summary>链路故障（断线、超时、设备无响应）。</summary>
    Faulted,
}

/// <summary>光源通道的开关状态。</summary>
public enum LightChannelState
{
    /// <summary>关闭。</summary>
    Off,

    /// <summary>点亮。</summary>
    On,

    /// <summary>通道故障（过流/短路/过温等）。</summary>
    Fault,

    /// <summary>该通道在设备上不存在或被禁用。</summary>
    NotAvailable,
}

/// <summary>
/// 亮度设定所使用的"量纲"。控制器对外统一归一化为 0–100 的百分比，
/// 但保留原始单位信息，便于适配器做 mA ↔ 百分比换算时溯源。
/// </summary>
public enum LightIntensityUnit
{
    /// <summary>额定电流的百分比（0–100）。</summary>
    PercentOfNominal,

    /// <summary>绝对电流，毫安。适配器按通道额定值换算。</summary>
    Milliampere,

    /// <summary>设备原始码值（如 0–255 的 DAC 码）。仅供调试，不推荐业务层使用。</summary>
    RawCode,
}

/// <summary>亮度变化的过渡方式。用于避免突变对图像一致性造成冲击。</summary>
public enum LightRampMode
{
    /// <summary>立即生效。</summary>
    Immediate,

    /// <summary>在指定时长内线性过渡到目标亮度。</summary>
    Linear,
}

/// <summary>底层传输链路类型。仅用于连接参数描述，不含任何 SDK 类型。</summary>
public enum LightTransportKind
{
    /// <summary>串口（RS-232/485）。</summary>
    Serial,

    /// <summary>以太网 TCP。</summary>
    Tcp,

    /// <summary>USB  HID / 虚拟串口。</summary>
    Usb,

    /// <summary>纯软件模拟器，无需硬件。</summary>
    Simulator,
}
