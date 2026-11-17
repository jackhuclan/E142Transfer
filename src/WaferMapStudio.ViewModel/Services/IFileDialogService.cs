namespace WaferMapStudio.ViewModel.Services;

/// <summary>
/// 文件选择服务（宿主能力接口）。
///
/// ViewModel 层禁止依赖 WPF 类型（须可跨平台单测），所以打开文件对话框
/// 抽象成接口：接口在此层定义，实现在 App 层（<c>Services/FileDialogService</c>），
/// 测试注入假实现。
/// </summary>
public interface IFileDialogService
{
    /// <summary>弹出打开文件对话框；用户取消返回 null。</summary>
    string? PickWaferMapFile();
}
