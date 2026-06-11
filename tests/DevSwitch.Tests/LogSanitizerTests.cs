// 文件用途：验证 LogSanitizer 日志行脱敏行为（复用 DiagnosticSanitizer + PATH 截断）。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class LogSanitizerTests
{
    [Theory]
    [InlineData("token=abcdef123456", "abcdef123456")]
    [InlineData("\"password\": \"hunter2value\"", "hunter2value")]
    [InlineData("api_key = sk-secretkeyvalue", "sk-secretkeyvalue")]
    public void SanitizeMasksSecretKeyValues(string line, string secret)
    {
        var sanitized = LogSanitizer.Sanitize(line);

        Assert.Contains("***", sanitized);
        Assert.DoesNotContain(secret, sanitized);
    }

    [Fact]
    public void SanitizeMasksBearerCredential()
    {
        var sanitized = LogSanitizer.Sanitize("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload");

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", sanitized);
        Assert.Contains("Bearer ***", sanitized);
    }

    [Fact]
    public void SanitizeHidesWindowsUserName()
    {
        var sanitized = LogSanitizer.Sanitize(@"loaded C:\Users\alice\AppData\Local\DevSwitch\config");

        Assert.DoesNotContain("alice", sanitized);
        Assert.Contains(@"C:\Users\<user>", sanitized);
    }

    [Fact]
    public void SanitizeReturnsEmptyForNullOrEmpty()
    {
        Assert.Equal(string.Empty, LogSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, LogSanitizer.Sanitize(""));
    }

    // ---------- PATH 超长行截断 ----------

    [Fact]
    public void SanitizeKeepsOnlyDevSwitchPathEntries()
    {
        var line = @"PATH=C:\Windows\System32;C:\Users\bob\bin;C:\Users\bob\DevSwitch\current\java\bin;C:\Tools";

        var sanitized = LogSanitizer.Sanitize(line);

        // 仅保留 DevSwitch 相关条目，其余系统 / 用户条目被丢弃。
        Assert.Contains("DevSwitch", sanitized);
        Assert.DoesNotContain(@"C:\Windows\System32", sanitized);
        Assert.DoesNotContain(@"C:\Tools", sanitized);
        Assert.Contains("<truncated>", sanitized);
        // 用户名段仍被脱敏。
        Assert.DoesNotContain(@"\bob\bin", sanitized);
    }

    [Fact]
    public void SanitizeCollapsesPathWithNoDevSwitchEntriesToPlaceholder()
    {
        var line = @"Path=C:\Windows;C:\Windows\System32;C:\Tools";

        var sanitized = LogSanitizer.Sanitize(line);

        Assert.DoesNotContain(@"C:\Windows\System32", sanitized);
        Assert.DoesNotContain(@"C:\Tools", sanitized);
        Assert.Contains("<truncated>", sanitized);
    }

    [Fact]
    public void CollapsePathAssignmentsLeavesNonPathLinesUntouched()
    {
        var line = "switched java to 17.0.10";

        Assert.Equal(line, LogSanitizer.CollapsePathAssignments(line));
    }

    [Fact]
    public void CollapsePathAssignmentsPreservesLeadingTimestampPrefix()
    {
        // 真实日志行常带时间戳前缀；PATH= 出现在中间时不应被误判为 PATH 赋值行。
        var line = @"2026-06-09T10:00:00 message PATH=C:\Tools";

        // 该行不以 PATH= 开头，应原样返回（PATH= 在中段不触发整体截断）。
        Assert.Equal(line, LogSanitizer.CollapsePathAssignments(line));
    }

    [Fact]
    public void CollapsePathAssignmentsHandlesLeadingWhitespace()
    {
        var line = @"   PATH=C:\Tools;C:\Users\bob\DevSwitch\bin";

        var collapsed = LogSanitizer.CollapsePathAssignments(line);

        Assert.StartsWith("   PATH=", collapsed);
        Assert.Contains("DevSwitch", collapsed);
        Assert.DoesNotContain(@"C:\Tools", collapsed);
    }
}
