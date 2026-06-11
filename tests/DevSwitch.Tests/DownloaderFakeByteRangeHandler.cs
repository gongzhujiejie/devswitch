// 文件用途：测试用假 HttpMessageHandler，模拟支持/不支持 HTTP Range 的服务器，不真实联网。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Net.Http
// NOTE: 合法授权学习使用，仅限本地环境。仅供 Downloader* 测试驱动下载引擎，模拟字节流与 Range 响应。

using System.Net;
using System.Net.Http.Headers;

namespace DevSwitch.Tests;

/// <summary>
/// 内存字节服务器假 handler。可配置是否支持 Range，并能统计每个 Range 请求，
/// 用于验证多线程分块下载、断点续传与单线程回退路径。
/// </summary>
internal sealed class FakeByteRangeHandler : HttpMessageHandler
{
    private readonly byte[] _content;
    private readonly bool _supportsRange;
    private readonly object _gate = new();

    /// <summary>
    /// 记录收到的所有 Range 请求区间（from, to），to 为 null 表示开放区间。
    /// </summary>
    public List<(long? From, long? To)> RangeRequests { get; } = new();

    /// <summary>
    /// 收到的请求总数。
    /// </summary>
    public int RequestCount { get; private set; }

    public FakeByteRangeHandler(byte[] content, bool supportsRange)
    {
        _content = content;
        _supportsRange = supportsRange;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            RequestCount++;
        }

        var range = request.Headers.Range?.Ranges.FirstOrDefault();

        // 服务器支持 Range 且请求带 Range：返回 206 Partial Content。
        if (_supportsRange && range is not null)
        {
            lock (_gate)
            {
                RangeRequests.Add((range.From, range.To));
            }

            var from = (int)(range.From ?? 0);
            // to 为空表示到文件末尾。
            var to = (int)(range.To ?? (_content.Length - 1));
            to = Math.Min(to, _content.Length - 1);
            var length = to - from + 1;

            var slice = new byte[length];
            Array.Copy(_content, from, slice, 0, length);

            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice),
            };
            partial.Content.Headers.ContentLength = length;
            // Content-Range: bytes from-to/total，引擎据此读取总长度。
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, _content.Length);
            partial.Headers.AcceptRanges.Add("bytes");
            return Task.FromResult(partial);
        }

        // 不支持 Range 或无 Range 头：返回 200 OK 整包。
        var full = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(_content),
        };
        full.Content.Headers.ContentLength = _content.Length;
        if (_supportsRange)
        {
            full.Headers.AcceptRanges.Add("bytes");
        }

        return Task.FromResult(full);
    }
}
