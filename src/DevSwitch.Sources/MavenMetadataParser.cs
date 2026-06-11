// 文件用途：解析 Apache Maven maven-metadata.xml 为统一版本模型。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Xml、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。

using System.Xml;
using DevSwitch.Core;

namespace DevSwitch.Sources;

/// <summary>
/// Apache Maven 版本元数据（maven-metadata.xml）解析器。
/// </summary>
/// <remarks>
/// 输入形如：
/// <code>
/// &lt;metadata&gt;
///   &lt;versioning&gt;
///     &lt;versions&gt;
///       &lt;version&gt;3.9.9&lt;/version&gt;
///       &lt;version&gt;3.8.8&lt;/version&gt;
///     &lt;/versions&gt;
///     &lt;lastUpdated&gt;20240901000000&lt;/lastUpdated&gt;
///   &lt;/versioning&gt;
/// &lt;/metadata&gt;
/// </code>
/// Maven 为跨平台 zip，架构记为 <see cref="SdkArchitecture.Any"/>；
/// 下载地址按官方分发约定拼接：
/// <c>https://archive.apache.org/dist/maven/maven-3/{ver}/binaries/apache-maven-{ver}-bin.zip</c>。
/// </remarks>
public static class MavenMetadataParser
{
    private const string DistributionId = "apache-maven";

    /// <summary>
    /// 解析 maven-metadata.xml 文本。
    /// </summary>
    /// <param name="xml">maven-metadata.xml 文本。</param>
    /// <param name="architecture">期望架构；Maven 与架构无关，仅 Unknown 之外的值视为兼容。</param>
    /// <returns>解析出的版本列表，未排序；非法版本被跳过。</returns>
    public static IReadOnlyList<SdkSourceVersion> Parse(string xml, SdkArchitecture architecture = SdkArchitecture.Any)
    {
        var result = new List<SdkSourceVersion>();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return result;
        }

        // Maven 是平台无关 zip：除非显式声明架构互斥，否则一律可用。
        // 这里不因传入 X64/Arm64 而过滤，因为同一 zip 适配所有架构。

        var settings = new XmlReaderSettings
        {
            // 关闭 DTD 处理，避免 XXE 等外部实体风险。
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);

            // 流式扫描，只关心 version 元素文本。
            // NOTE: ReadElementContentAsString 会把游标推进到该元素之后的下一个节点，
            // 若再让外层 while(reader.Read()) 推进一次，会吞掉中间的 version 元素，
            // 因此这里用标志位控制：刚读完元素内容时不再额外 Read。
            var advance = true;
            while (advance ? reader.Read() : !reader.EOF)
            {
                advance = true;

                if (reader.NodeType != XmlNodeType.Element
                    || !reader.Name.Equals("version", StringComparison.Ordinal))
                {
                    continue;
                }

                // 读取 version 元素的文本内容（游标已前移到下一节点）。
                var version = reader.ReadElementContentAsString();
                // 下一轮不再 Read，直接复用当前游标节点进行判断。
                advance = false;

                if (string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                version = version.Trim();
                result.Add(BuildVersion(version));
            }
        }
        catch (XmlException)
        {
            // 非法 XML：返回已成功解析的部分（通常为空），不抛出。
            return result;
        }

        return result;
    }

    /// <summary>
    /// 按 Maven 主版本号选择分发路径并构造版本模型。
    /// </summary>
    private static SdkSourceVersion BuildVersion(string version)
    {
        // maven-3 / maven-4 等顶层目录由主版本号决定。
        var majorSegment = version.Length > 0 && char.IsDigit(version[0])
            ? version[0]
            : '3';

        var url =
            $"https://archive.apache.org/dist/maven/maven-{majorSegment}/{version}/binaries/apache-maven-{version}-bin.zip";

        return new SdkSourceVersion(
            SdkType: SdkType.Maven,
            Version: version,
            Distribution: DistributionId,
            // Maven zip 与 CPU 架构无关。
            Architecture: SdkArchitecture.Any,
            DownloadUrl: url,
            Sha256: null,
            // Apache 为每个 zip 提供 .sha512/.sha1 校验文件；此处给出 sha512。
            ChecksumUrl: url + ".sha512",
            ReleaseDate: null,
            DisplayName: $"Apache Maven {version}");
    }
}
