namespace WaferMapStudio.Lighting;

/// <summary>
/// 光源控制器异常基类。所有本层抛出的异常都派生自此类型，
/// 上层可一次性 catch 并区分处理，避免泄漏设备侧的原始异常类型。
/// </summary>
public class LightControllerException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="message">可读描述。</param>
    /// <param name="innerException">原始异常，保留排障线索。</param>
    public LightControllerException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>参数校验失败：通道越界、亮度超范围、时长非法等。属于调用方错误，重试无意义。</summary>
public sealed class LightValidationException : LightControllerException
{
    /// <summary>创建校验异常。</summary>
    /// <param name="parameterName">非法参数名，便于定位。</param>
    /// <param name="message">可读描述。</param>
    public LightValidationException(string parameterName, string message)
        : base($"[参数 {parameterName}] {message}")
    {
        ParameterName = parameterName;
    }

    /// <summary>非法参数名。</summary>
    public string ParameterName { get; }
}

/// <summary>链路异常：未连接、连接失败、通信超时、链路断开。</summary>
public sealed class LightConnectionException : LightControllerException
{
    /// <summary>创建链路异常。</summary>
    public LightConnectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>设备执行异常：设备拒绝指令、返回错误码、状态查询失败。</summary>
public sealed class LightDeviceException : LightControllerException
{
    /// <summary>创建设备异常。</summary>
    /// <param name="code">设备原始错误码；无则为空串。</param>
    /// <param name="message">可读描述。</param>
    /// <param name="innerException">原始异常。</param>
    public LightDeviceException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>设备原始错误码。</summary>
    public string Code { get; }
}
