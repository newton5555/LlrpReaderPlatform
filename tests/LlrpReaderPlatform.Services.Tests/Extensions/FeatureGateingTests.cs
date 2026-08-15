using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Extensions;
using Xunit;

namespace LlrpReaderPlatform.Services.Tests.Extensions;

/// <summary>
/// ADR-0011/0012 守护测试：双轴门控（毕业门控 + 标准优先仲裁）。
/// </summary>
public sealed class FeatureGateingTests
{
    [Fact]
    public void Graduation_drops_vendor_feature_when_device_matches_standardized_since_version()
    {
        var results = FeatureGating.Arbitrate(
            [ReaderFeatures.StandardSettings, new Feature("report-xpc", "zebra", semanticId: "xpc-report", standardizedSince: LlrpProtocolVersion.Version20)],
            LlrpProtocolVersion.Version20);
        Assert.Contains(ReaderFeatures.StandardSettings, results);
        Assert.DoesNotContain(results, feature => feature.Vendor == "zebra" && feature.Id == "report-xpc");
    }

    [Fact]
    public void Graduation_keeps_vendor_feature_before_the_standardizing_version()
    {
        var results = FeatureGating.Arbitrate(
            [ReaderFeatures.StandardSettings, new Feature("report-xpc", "zebra", semanticId: "xpc-report", standardizedSince: LlrpProtocolVersion.Version20)],
            LlrpProtocolVersion.Version101);
        Assert.Contains(ReaderFeatures.StandardSettings, results);
        Assert.Contains(results, feature => feature.Vendor == "zebra" && feature.Id == "report-xpc");
    }

    [Fact]
    public void Graduation_is_noop_when_negotiated_version_is_unknown()
    {
        var feature = new Feature("report-xpc", "zebra", semanticId: "xpc-report", standardizedSince: LlrpProtocolVersion.Version20);
        var results = FeatureGating.Arbitrate([feature], negotiatedVersion: null);
        Assert.Contains(feature, results);
    }

    [Fact]
    public void Arbitration_prefers_standard_feature_over_vendor_when_same_semantic_key()
    {
        var vendorClaimsStandardSemantic = new Feature("vendor-settings", "vendor", semanticId: "standard.settings");
        var results = FeatureGating.Arbitrate(
            [ReaderFeatures.StandardSettings, vendorClaimsStandardSemantic],
            LlrpProtocolVersion.Version101);
        Assert.Contains(ReaderFeatures.StandardSettings, results);
        Assert.DoesNotContain(vendorClaimsStandardSemantic, results);
    }

    [Fact]
    public void Arbitration_keeps_vendor_feature_when_no_standard_claims_same_semantic()
    {
        var feature = new Feature("report-gps", "zebra", semanticId: "gps-report");
        var results = FeatureGating.Arbitrate([feature], LlrpProtocolVersion.Version101);
        Assert.Contains(feature, results);
        Assert.Single(results);
    }
}
