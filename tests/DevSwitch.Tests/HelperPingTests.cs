// 文件用途：通过公开进程接口验证 DevSwitch.Helper 的基础 stdin/stdout JSON 协议行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.Text.Json
// NOTE: 合法授权学习使用，仅限本地环境。本测试只启动本仓库构建出的 helper 进程。

using System.Text.Json;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class HelperPingTests
{
    [Fact]
    public async Task HelperProcessPingReturnsProtocolDetails()
    {
        // NOTE: 测试只关心公开行为：给 helper 发 ping JSON，应该收到带协议版本 details 的 pong JSON。
        var request = new HelperRequest("test-request-id", "ping", new { });

        var result = await HelperProcessTestSupport.InvokeHelperAsync(request);
        var response = result.DeserializeResponse();

        Assert.True(result.Exited, "helper process should exit after handling one JSON request.");
        Assert.True(result.ExitCode == 0, $"helper should exit successfully. stderr: {result.Error}");
        Assert.Equal("test-request-id", response.RequestId);
        Assert.True(response.Success);
        Assert.Null(response.ErrorCode);
        Assert.Equal("pong", response.Message);
        Assert.NotNull(response.Details);
        Assert.Equal(1, response.Details.Value.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public async Task HelperProcessUnknownOperationReturnsDetails()
    {
        // NOTE: 未知 operation 也必须返回统一 JSON shape，并把原 operation 放进 details 便于诊断。
        var request = new HelperRequest("unknown-request-id", "does-not-exist", new { });

        var result = await HelperProcessTestSupport.InvokeHelperAsync(request);
        var response = result.DeserializeResponse();

        Assert.True(result.Exited, "helper process should exit after handling one JSON request.");
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("unknown-request-id", response.RequestId);
        Assert.False(response.Success);
        Assert.Equal("unknown-operation", response.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(response.Message));
        Assert.NotNull(response.Details);
        Assert.Equal("does-not-exist", response.Details.Value.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task HelperProcessMalformedJsonReturnsInvalidJsonError()
    {
        // NOTE: 非法 JSON 是协议解析错误，应与“字段缺失”的 invalid-request 区分开。
        var result = await HelperProcessTestSupport.InvokeHelperRawAsync("{\"requestId\":");
        var response = result.DeserializeResponse();

        Assert.True(result.Exited);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, response.RequestId);
        Assert.False(response.Success);
        Assert.Equal("invalid-json", response.ErrorCode);
        Assert.NotNull(response.Details);
        Assert.Equal("parse-error", response.Details.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task HelperProcessMissingRequestIdReturnsInvalidRequest()
    {
        // NOTE: 合法 JSON 但缺少必需字段时，helper 应返回结构化 invalid-request。
        var result = await HelperProcessTestSupport.InvokeHelperRawAsync("{\"operation\":\"ping\",\"payload\":{}}");
        var response = result.DeserializeResponse();

        Assert.True(result.Exited);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, response.RequestId);
        Assert.False(response.Success);
        Assert.Equal("invalid-request", response.ErrorCode);
        Assert.NotNull(response.Details);
        Assert.Contains(response.Details.Value.GetProperty("missing").EnumerateArray(), item => item.GetString() == "requestId");
    }

    [Fact]
    public async Task HelperProcessMissingOperationReturnsInvalidRequestWithRequestId()
    {
        // NOTE: requestId 已可解析时，即使 operation 缺失也要原样回传，方便调用方关联失败请求。
        var result = await HelperProcessTestSupport.InvokeHelperRawAsync("{\"requestId\":\"req-missing-op\",\"payload\":{}}");
        var response = result.DeserializeResponse();

        Assert.True(result.Exited);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("req-missing-op", response.RequestId);
        Assert.False(response.Success);
        Assert.Equal("invalid-request", response.ErrorCode);
        Assert.NotNull(response.Details);
        Assert.Contains(response.Details.Value.GetProperty("missing").EnumerateArray(), item => item.GetString() == "operation");
    }

    [Fact]
    public async Task HelperProcessEscapesJsonStringFields()
    {
        // NOTE: Windows 路径和 requestId 都可能含反斜杠，helper 必须正确处理 JSON 字符串转义。
        var requestId = "req-\"quoted\"-\\-line";
        var request = new HelperRequest(requestId, "ping", new { });

        var result = await HelperProcessTestSupport.InvokeHelperAsync(request);

        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(requestId, document.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task HelperProcessIgnoresUnknownTopLevelRequestFields()
    {
        // NOTE: 协议必须向前兼容，未知顶层字段不能破坏已支持的 operation。
        var result = await HelperProcessTestSupport.InvokeHelperRawAsync("{\"requestId\":\"req-extra\",\"operation\":\"ping\",\"payload\":{},\"unexpected\":{\"nested\":true}}");
        var response = result.DeserializeResponse();

        Assert.Equal(0, result.ExitCode);
        Assert.True(response.Success);
        Assert.Equal("req-extra", response.RequestId);
        Assert.Equal("pong", response.Message);
    }
}
