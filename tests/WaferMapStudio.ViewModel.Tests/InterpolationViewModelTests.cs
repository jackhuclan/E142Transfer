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
}
