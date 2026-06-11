// 文件用途：定义 DevSwitch GUI/Core 与隐藏 helper 进程之间的最小 JSON 协议模型。
// 创建日期：2026-06-08
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevSwitch.Core;

/// <summary>
/// 表示发送给 helper 进程的公开请求。
/// </summary>
/// <param name="RequestId">调用方生成的请求标识，用于匹配响应。</param>
/// <param name="Operation">要执行的公开操作名称，例如 ping。</param>
/// <param name="Payload">操作参数；M0 阶段 ping 不需要参数。</param>
public sealed record HelperRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("payload")] object? Payload);

/// <summary>
/// 表示 helper 进程通过 stdout 返回给调用方的公开响应。
/// </summary>
/// <param name="RequestId">原样返回的请求标识。</param>
/// <param name="Success">操作是否成功。</param>
/// <param name="ErrorCode">失败时的稳定错误码；成功时为空。</param>
/// <param name="Message">面向调用方的简短结果消息。</param>
/// <param name="Details">操作返回的结构化细节；调用方不应从 message 中解析机器状态。</param>
public sealed record HelperResponse(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] JsonElement? Details);

/// <summary>
/// helper inspect/create/remove/switch link 操作使用的 payload。
/// </summary>
/// <param name="SdkType">SDK 类型 slug，例如 java、maven、node、go。</param>
/// <param name="CurrentPath">current 链接入口路径。</param>
/// <param name="TargetPath">目标 SDK 根目录路径。</param>
/// <param name="LinkPreference">链接偏好，例如 junction-first。</param>
public sealed record HelperLinkPayload(
    [property: JsonPropertyName("sdkType")] string? SdkType,
    [property: JsonPropertyName("currentPath")] string CurrentPath,
    [property: JsonPropertyName("targetPath")] string? TargetPath,
    [property: JsonPropertyName("linkPreference")] string? LinkPreference);
