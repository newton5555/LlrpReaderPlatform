using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;

namespace LlrpReaderPlatform.Services.Extensions;

/// <summary>
/// ADR-0011/0012 的双轴门控规则（纯函数，便于单测）：
/// - 毕业门控：厂商特性在设备协商版本 >= StandardizedSince 时让位给标准轴；
/// - 标准优先仲裁：同一语义键同时出现标准与厂商贡献时，只保留标准。
/// </summary>
public static class FeatureGating
{
    /// <summary>
    /// 对已收集的标准 + 厂商特征执行毕业门控与标准优先仲裁，返回最终能力列表。
    /// </summary>
    public static IReadOnlyList<Feature> Arbitrate(
        IEnumerable<Feature> contributions,
        LlrpProtocolVersion? negotiatedVersion)
    {
        var features = contributions.ToArray();

        var graduatedSemantics = new HashSet<string>(StringComparer.Ordinal);
        if (negotiatedVersion is { } version)
        {
            foreach (Feature f in features)
            {
                if (f.IsVendor && f.StandardizedSince is { } since && version >= since)
                {
                    graduatedSemantics.Add(f.SemanticId);
                }
            }
        }

        var bySemantic = new Dictionary<string, List<Feature>>(StringComparer.Ordinal);
        foreach (Feature f in features)
        {
            if (graduatedSemantics.Contains(f.SemanticId) && f.IsVendor)
            {
                continue;
            }

            if (!bySemantic.TryGetValue(f.SemanticId, out List<Feature>? list))
            {
                list = new List<Feature>();
                bySemantic[f.SemanticId] = list;
            }

            list.Add(f);
        }

        var arbitrated = new List<Feature>(bySemantic.Count);
        foreach (KeyValuePair<string, List<Feature>> kvp in bySemantic)
        {
            Feature[] same = kvp.Value.ToArray();
            // Feature 是 struct：FirstOrDefault 无匹配时返回 default(Feature)，
            // 必须用 Id 是否为 null 来判定是否存在标准贡献，避免把空 Feature 加入结果。
            Feature standard = same.FirstOrDefault(static f => !f.IsVendor);
            if (standard.Id is not null && same.Any(static f => f.IsVendor))
            {
                arbitrated.Add(standard);
            }
            else
            {
                arbitrated.AddRange(same);
            }
        }

        return arbitrated.Distinct().ToArray();
    }
}
