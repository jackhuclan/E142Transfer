namespace WaferMapStudio.Lighting;

/// <summary>
/// 集中式参数校验。控制器在执行任何指令前先走这里，把"调用方错误"挡在设备通信之前。
/// 设计取舍：校验规则集中在纯静态类中，便于单测，也便于不同机型复用同一套边界定义。
/// </summary>
public static class LightParameterValidator
{
    /// <summary>亮度百分比的下限（约定 0 = 熄灭，100 = 该通道额定电流）。</summary>
    public const double MinBrightnessPercent = 0.0;

    /// <summary>亮度百分比的上限。</summary>
    public const double MaxBrightnessPercent = 100.0;

    /// <summary>校验通道号是否落在设备能力范围内（1-based）。</summary>
    public static void ValidateChannel(int channel, LightDeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (channel < 1 || channel > capabilities.ChannelCount)
        {
            throw new LightValidationException(
                nameof(channel),
                $"通道 {channel} 越界；设备 {capabilities.Model} 仅提供 1..{capabilities.ChannelCount} 通道");
        }
    }

    /// <summary>校验亮度百分比，并拒绝 NaN / Infinity（这类值发给 DAC 会产生不可预期行为）。</summary>
    public static void ValidateBrightnessPercent(double percent, string parameterName = "brightnessPercent")
    {
        if (!double.IsFinite(percent) || percent < MinBrightnessPercent || percent > MaxBrightnessPercent)
        {
            throw new LightValidationException(
                parameterName,
                $"亮度必须为 {MinBrightnessPercent}..{MaxBrightnessPercent} 的有限数值，实际为 {percent}");
        }
    }

    /// <summary>校验电流值不超过单通道额定最大值。</summary>
    public static void ValidateCurrentMa(double currentMa, LightDeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!double.IsFinite(currentMa) || currentMa < 0 || currentMa > capabilities.MaxCurrentMa)
        {
            throw new LightValidationException(
                nameof(currentMa),
                $"电流必须在 0..{capabilities.MaxCurrentMa} mA 之间，实际为 {currentMa}");
        }
    }

    /// <summary>校验过渡时长；设备不支持线性过渡时，任何非零时长都视为非法。</summary>
    public static void ValidateRamp(LightChannelCommand command, LightDeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (command.RampMilliseconds < 0 || command.RampMilliseconds > 60_000)
        {
            throw new LightValidationException(
                nameof(command.RampMilliseconds),
                $"过渡时长必须在 0..60000 ms 之间，实际为 {command.RampMilliseconds}");
        }

        if (command.Ramp == LightRampMode.Linear && !capabilities.SupportsRamp)
        {
            throw new LightValidationException(
                nameof(command.Ramp),
                $"设备 {capabilities.Model} 不支持亮度线性过渡");
        }
    }

    /// <summary>校验完整通道指令。控制器在写设备前统一调用此方法。</summary>
    public static void ValidateCommand(int channel, LightChannelCommand command, LightDeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateChannel(channel, capabilities);
        ValidateBrightnessPercent(command.BrightnessPercent);
        ValidateRamp(command, capabilities);
    }

    /// <summary>把电流换算成归一化亮度百分比。换算比例来自设备能力中的额定电流。</summary>
    public static double CurrentMaToPercent(double currentMa, LightDeviceCapabilities capabilities)
    {
        ValidateCurrentMa(currentMa, capabilities);
        return capabilities.MaxCurrentMa <= 0
            ? throw new LightValidationException(nameof(capabilities.MaxCurrentMa), "额定电流必须大于 0")
            : currentMa / capabilities.MaxCurrentMa * MaxBrightnessPercent;
    }
}
