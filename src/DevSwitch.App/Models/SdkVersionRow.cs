// 文件用途：定义 WinUI SDK 管理页表格行绑定模型。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。
//       Status / Operation / CanSwitch 在「检测当前」「命令验证」回写时需要立即触发徽章重渲染，
//       因此这三项实现 INotifyPropertyChanged，其它身份/路径字段保持只读 init。

using System.ComponentModel;
using System.Runtime.CompilerServices;
using DevSwitch.Core;

namespace DevSwitch.App.Models;

/// <summary>
/// SDK 版本列表行模型。
/// </summary>
/// <remarks>
/// 该模型是 GUI 绑定层 DTO，来源于真实 sdks.json 的投影，不直接代表持久化文件结构。
/// </remarks>
public sealed class SdkVersionRow : INotifyPropertyChanged
{
    private string status = string.Empty;
    private string operation = string.Empty;
    private bool canSwitch;

    /// <summary>
    /// 属性变更事件：仅 Status/Operation/CanSwitch 三个状态相关字段在运行期会变更，
    /// 其它字段在投影后视作不可变，避免无谓的通知开销。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// SDK 记录稳定 ID，用于后续切换、详情或刷新定位。
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// SDK 类型，供切换服务识别 Java/Maven/Node/Go。
    /// </summary>
    public SdkType Type { get; init; } = SdkType.Unknown;

    /// <summary>
    /// SDK 所属分类，例如 Java、Maven、Node.js、Go。
    /// ViewModel 会按该字段过滤右侧版本列表。
    /// </summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>
    /// 展示名称，例如 Temurin 17、Node.js 22 LTS。
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 展示版本号；本地导入未验证时可能是 unknown。
    /// </summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// 来源文案：托管或外部。
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// SDK 根目录路径。
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// 状态文案：使用中、可用、未验证、不可用。
    /// </summary>
    /// <remarks>
    /// 命令验证失败时会被回写为「不可用」，必须触发 PropertyChanged 让徽章 + 当前过滤器即时刷新。
    /// </remarks>
    public string Status
    {
        get => status;
        set => SetField(ref status, value ?? string.Empty);
    }

    /// <summary>
    /// 操作文案，例如 当前、切换、验证、查看原因。
    /// </summary>
    public string Operation
    {
        get => operation;
        set => SetField(ref operation, value ?? string.Empty);
    }

    /// <summary>
    /// 是否允许点击「切换」类操作。
    /// </summary>
    public bool CanSwitch
    {
        get => canSwitch;
        set => SetField(ref canSwitch, value);
    }

    /// <summary>
    /// 通用属性写入 + 变更通知；相等时短路避免重复触发。
    /// </summary>
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
