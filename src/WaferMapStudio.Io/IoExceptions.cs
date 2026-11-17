namespace WaferMapStudio.Io;

/// <summary>
/// IO 控制层异常基类。本层抛出的异常都派生自此类型，上层可一次 catch 并区分处理，
/// 避免泄漏设备侧原始异常类型（参照 LightControllerException 的设计）。
/// </summary>
public class IoControllerException : Exception
{
    /// <summary>创建异常。</summary>
    public IoControllerException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>参数/点位校验失败：未定义的点、方向不匹配、索引越界。属于调用方错误。</summary>
public sealed class IoValidationException : IoControllerException
{
    /// <summary>创建校验异常。</summary>
    public IoValidationException(string message) : base(message)
    {
    }
}

/// <summary>点表异常：JSON 结构非法、IOPin 非 2 的幂、点位重复、字节组冲突、型号长度不符。</summary>
public sealed class IoPointTableException : IoControllerException
{
    /// <summary>创建点表异常。</summary>
    public IoPointTableException(string message) : base(message)
    {
    }

    /// <summary>创建点表异常并保留原始解析异常。</summary>
    public IoPointTableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>链路异常：未就绪、建链失败、通信超时。</summary>
public sealed class IoConnectionException : IoControllerException
{
    /// <summary>创建链路异常。</summary>
    public IoConnectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>写校验异常：写出后回读不一致，说明输出点未真正动作。</summary>
public sealed class IoWriteVerifyException : IoControllerException
{
    /// <summary>创建写校验异常。</summary>
    public IoWriteVerifyException(IoPoint point, bool expected, bool actual)
        : base($"点位 {point} 写后回读不一致：期望 {expected}，实际 {actual}")
    {
        Point = point;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>写失败的点位。</summary>
    public IoPoint Point { get; }

    /// <summary>期望值。</summary>
    public bool Expected { get; }

    /// <summary>实际回读值。</summary>
    public bool Actual { get; }
}

/// <summary>型号不符：读到的硬件型号与配置期望值不一致。</summary>
public sealed class IoModelMismatchException : IoControllerException
{
    /// <summary>创建型号不符异常。</summary>
    public IoModelMismatchException(string actual, string expected)
        : base($"IO 模块型号不符：期望 {expected}，实际 {actual}")
    {
        Actual = actual;
        Expected = expected;
    }

    /// <summary>实际型号。</summary>
    public string Actual { get; }

    /// <summary>期望型号。</summary>
    public string Expected { get; }
}
