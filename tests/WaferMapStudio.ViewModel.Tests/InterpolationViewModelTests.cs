using Xunit;
using WaferMapStudio.ViewModel;

namespace WaferMapStudio.ViewModel.Tests;

/// <summary>插补演示视图模型：默认参数执行成功、非法参数给出可读错误。</summary>
public class InterpolationViewModelTests
{
    [Fact]
    public void Execute_DefaultParameters_ProducesCompleteResults()
    {
        var vm = new InterpolationViewModel();

        vm.ExecuteCommand.Execute(null);

        Assert.False(vm.StatusIsError);
        Assert.Contains("完成", vm.StatusText);
        Assert.NotEmpty(vm.SampleRows);
        Assert.NotEmpty(vm.SegmentRows);
        Assert.NotEmpty(vm.Trajectory);
        Assert.NotEqual("—", vm.FollowErrorText);
    }

    [Fact]
    public void Execute_InvalidFeedRate_ShowsErrorStatusWithoutThrowing()
    {
        var vm = new InterpolationViewModel { FeedRateText = "abc" };

        vm.ExecuteCommand.Execute(null);

        Assert.True(vm.StatusIsError);
        Assert.StartsWith("参数错误", vm.StatusText);
        Assert.Empty(vm.SampleRows);
    }

    [Fact]
    public void SelectScenario_AppliesParameterSetAndAutoExecutes()
    {
        var vm = new InterpolationViewModel();

        // 场景选项与参数表对齐：索引 0 为自定义，1..N 为工业场景
        Assert.Equal(6, vm.ScenarioOptions.Count);

        vm.SelectedScenarioIndex = 2; // 半导体贴片机取放

        // 参数被场景覆盖：贴片机 F=300 A=1500 J=20000 T=1ms 弦高=0.02 前瞻=2
        Assert.Equal(0, vm.SelectedPresetIndex); // 单轴直线
        Assert.Equal("300", vm.FeedRateText);
        Assert.Equal("1500", vm.AccelText);
        Assert.Equal("20000", vm.JerkText);
        Assert.Equal("1", vm.PeriodText);
        Assert.Equal("0.02", vm.ChordTolText);
        Assert.Equal("2", vm.LookaheadText);

        // 套用后自动执行，结果完整且状态提示带场景名
        Assert.False(vm.StatusIsError);
        Assert.Contains("半导体贴片机取放", vm.StatusText);
        Assert.Contains("完成", vm.StatusText);
        Assert.NotEmpty(vm.SampleRows);
        Assert.NotEmpty(vm.SegmentRows);
        Assert.NotEmpty(vm.Trajectory);
    }
}
