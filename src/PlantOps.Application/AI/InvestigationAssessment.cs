using PlantOps.Application.Telemetry;

namespace PlantOps.Application.AI;

public sealed record InvestigationRuleSnapshot(Guid Id, long Version, double RaiseAbove, double ClearBelow);

public sealed record InvestigationObservedReading(Guid Id, DateTimeOffset EffectiveAt, DateTimeOffset ReceivedAt,
    double? Value, string Quality, bool TimeAnomaly, Guid DataSourceId, Guid SourceMappingId, long MappingVersion,
    string SourceKind, string TimestampOrigin, DateTimeOffset? SourceTimestamp, DateTimeOffset? ServerTimestamp,
    uint? RawStatusCode, string? StatusSymbol, long? SourceVersion = null, long? PointVersion = null);

public sealed record InvestigationWindowAssessment(string Kind, DateTimeOffset From, DateTimeOffset To,
    DateTimeOffset RetrievedAt, string Trust, int GoodCount, int BadCount, int UncertainCount, bool HasGap,
    string[] Reasons);

public sealed record InvestigationPointSnapshot
{
    public Guid PointId
    {
        get; init;
    }

    public string Code { get; init; } = "";

    public string Unit { get; init; } = "";

    public DateTimeOffset AsOf
    {
        get; init;
    }

    public DateTimeOffset RetrievedAt
    {
        get; init;
    }

    public long PointVersion
    {
        get; init;
    }

    public Guid? SourceId
    {
        get; init;
    }

    public long? SourceVersion
    {
        get; init;
    }

    public Guid? MappingId
    {
        get; init;
    }

    public long? MappingVersion
    {
        get; init;
    }

    public string? SourceKind
    {
        get; init;
    }

    public string SamplingMode { get; init; } = "Unknown";

    public int StaleAfterMs
    {
        get; init;
    }

    public bool ConfigurationCurrent
    {
        get; init;
    }

    public bool LatestStateTrusted
    {
        get; init;
    }

    public InvestigationObservedReading? Observation
    {
        get; init;
    }

    public SourceHealth? Health
    {
        get; init;
    }

    public int UninspectedPointCount
    {
        get; init;
    }

    public string CurrentTrust { get; init; } = "Unverifiable";

    public string CurrentAnomaly { get; init; } = "Insufficient";

    public string[] Reasons { get; init; } = [];

    public InvestigationRuleSnapshot[] Rules { get; init; } = [];

    public bool RulesTruncated
    {
        get; init;
    }

    public InvestigationWindowAssessment[] Windows { get; init; } = [];
}

public sealed record InvestigationAssessment(string PolicyVersion, DateTimeOffset AsOf, string Scope,
    string DataTrust, string AnomalyEvidence, int UninspectedPointCount, string[] Reasons,
    InvestigationPointSnapshot[] Points, string[] EvidenceIds);

// Labels describe bounded evidence, never a diagnosis or a control instruction.
public static class InvestigationAssessmentPolicy
{
    public const string Version = "point-current-window-trust-v1";

    public static InvestigationPointSnapshot Evaluate(InvestigationPointSnapshot point)
    {
        var reasons = new List<string>();
        var sample = point.Observation;
        if (!point.ConfigurationCurrent)
        {
            reasons.Add("ConfigurationUnverified");
        }
        if (sample == null)
        {
            reasons.Add("ObservationUnavailable");
        }
        else
        {
            if (sample.Quality != "Good" || !sample.Value.HasValue || !double.IsFinite(sample.Value.Value))
            {
                reasons.Add("QualityUntrusted");
            }
            if (!point.LatestStateTrusted || sample.TimeAnomaly || SamplePolicy.HasOverflow(sample.RawStatusCode))
            {
                reasons.Add("ObservationUntrusted");
            }
            if (sample.EffectiveAt > point.AsOf || sample.ReceivedAt > point.AsOf)
            {
                reasons.Add("AfterAsOf");
            }
            if (point.AsOf - sample.EffectiveAt > TimeSpan.FromMilliseconds(point.StaleAfterMs))
            {
                reasons.Add("StaleObservation");
            }
            if (sample.TimestampOrigin == "Receive")
            {
                reasons.Add("ObservationTimeUnknown");
            }
            if (sample.DataSourceId != point.SourceId || sample.SourceMappingId != point.MappingId || sample.MappingVersion != point.MappingVersion
                || sample.SourceKind != point.SourceKind)
            {
                reasons.Add("MappingChanged");
            }
            if (!sample.SourceVersion.HasValue || !sample.PointVersion.HasValue || sample.SourceVersion != point.SourceVersion || sample.PointVersion != point.PointVersion)
            {
                reasons.Add("ConfigurationVersionUnverified");
            }
        }
        if (point.SourceKind == "OpcUa")
        {
            var health = point.Health;
            if (health == null || !health.IsCurrent || health.SourceVersion != point.SourceVersion
                || health.UpdatedAt > point.AsOf || point.AsOf - health.UpdatedAt > TimeSpan.FromSeconds(10))
            {
                reasons.Add("SourceHealthUnverified");
            }
            else if (health.State != "Connected")
            {
                reasons.Add("SourceNotConnected");
            }
            if (point.SamplingMode == "Unknown")
            {
                reasons.Add("SamplingContractUnknown");
            }
        }
        if (reasons.Count > 0 && point.SamplingMode == "Changes")
        {
            reasons.Add("SilenceDoesNotProveFailure");
        }
        var trusted = reasons.Count == 0;
        var anomaly = !trusted || point.Rules.Length == 0 ? "Insufficient"
            : point.Rules.Any(x => sample!.Value > x.RaiseAbove) ? "Present"
            : !point.RulesTruncated && point.Rules.All(x => sample!.Value < x.ClearBelow) ? "NotFound" : "Insufficient";
        return point with
        {
            CurrentTrust = trusted ? "Trusted" : "Unverifiable",
            CurrentAnomaly = anomaly,
            Reasons = reasons.Append(point.Rules.Length == 0 ? "NoApplicableThreshold" : "ThresholdComparisonOnly")
                .Concat(point.RulesTruncated ? ["ThresholdScopeTruncated"] : Array.Empty<string>()).ToArray()
        };
    }

    public static InvestigationWindowAssessment Window(string kind, MeasurementTrend trend)
    {
        var good = trend.Buckets.Sum(x => x.GoodCount);
        var bad = trend.Buckets.Sum(x => x.BadCount);
        var uncertain = trend.Buckets.Sum(x => x.UncertainCount);
        var gap = trend.LeadingGap || trend.TrailingGap || trend.Buckets.Any(x => x.HasGap);
        var completeQuality = good > 0 && bad == 0 && uncertain == 0 && !gap
            && trend.Buckets.All(x => x.AnomalyCount == 0 && x.SamplingMode == "Periodic" && x.CoverageRatio >= 0.95);
        // Existing minute projections do not retain per-reading source/config identity.
        // Good aggregate coverage alone cannot prove a fully trusted historical window.
        return new(kind, trend.From, trend.To, trend.CheckedAt,
            good == 0 ? "Unverifiable" : "Partial", good, bad, uncertain, gap,
            good == 0 ? ["NoUsableCompletedMinutes"] : completeQuality ? ["MinuteSourceIdentityUnverified"] : ["IncompleteQualityOrCoverage", "MinuteSourceIdentityUnverified"]);
    }

    public static InvestigationAssessment? FromEvidence(IReadOnlyList<InvestigationEvidence> evidence)
    {
        var items = evidence.Where(x => x.PointStatus != null).GroupBy(x => x.PointStatus!.PointId).Select(x => x.Last()).ToArray();
        if (items.Length == 0)
        {
            return null;
        }
        var points = items.Select(x => x.PointStatus!).ToArray();
        var missing = points.Max(x => x.UninspectedPointCount);
        var trusted = points.Count(x => x.CurrentTrust == "Trusted");
        var trust = trusted == 0 ? "Unverifiable" : trusted == points.Length && missing == 0 ? "Trusted" : "Partial";
        var anomaly = points.Any(x => x.CurrentAnomaly == "Present") ? "Present"
            : trust == "Trusted" && points.All(x => x.CurrentAnomaly == "NotFound") ? "NotFound" : "Insufficient";
        return new(Version, points[0].AsOf, "CurrentReadingsAtAsOf", trust, anomaly, missing,
            ["LimitedToInspectedPointsAndEnabledThresholds", "HistoricalAlarmDoesNotEstablishCurrentAnomaly", "NoRootCauseOrSafetyConclusion"],
            points, items.Select(x => x.Id).ToArray());
    }

    public static string TrustLabel(string value) => value switch { "Trusted" => "可信", "Partial" => "部分可信", _ => "不可验证" };

    public static string AnomalyLabel(string value) => value switch { "Present" => "存在", "NotFound" => "未发现", _ => "证据不足" };

    public static string Summary(InvestigationAssessment assessment) =>
        $"截至 {assessment.AsOf:O}，当前数据可信度：{TrustLabel(assessment.DataTrust)}；当前设备异常证据：{AnomalyLabel(assessment.AnomalyEvidence)}。"
        + $"仅限已检查的 {assessment.Points.Length} 个测点及其启用阈值，未检查启用测点 {assessment.UninspectedPointCount} 个；历史窗口另列，不能据此确诊根因或认定设备安全。";
}
