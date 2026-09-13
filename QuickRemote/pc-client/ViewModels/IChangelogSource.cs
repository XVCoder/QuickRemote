namespace QuickRemote.PCClient.ViewModels;

/// <summary>
/// 「更新记录」数据源：设置中心「版本更新」页只需读取 CHANGELOG.md 地址，
/// 用最小接口解耦视图与具体 ViewModel（也便于离屏渲染/单元测试注入桩数据）。
/// </summary>
public interface IChangelogSource
{
    /// <summary>更新记录（CHANGELOG.md）地址；为空表示未配置。</summary>
    string ChangelogUrl { get; }
}
