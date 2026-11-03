using PlantOps.AI;
using PlantOps.Application.AI;
using PlantOps.Application.Telemetry;
using Xunit;

namespace PlantOps.Tests;

public sealed class InvestigationAssessmentTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    private static InvestigationPointSnapshot Point(double value = 12) => new()
    {
        PointId = Guid.NewGuid(),
        Code = "VIBRATION",
        Unit = "mm/s",
        AsOf = At,
        RetrievedAt = At,
        PointVersion = 1,
        SourceId = Guid.Empty,
        SourceVersion = 1,
        MappingId = Guid.Empty,
        MappingVersion = 1,
        SourceKind = "Simulation",
        SamplingMode = "Periodic",
        StaleAfterMs = 5000,
        ConfigurationCurrent = true,
        LatestStateTrusted = true,
        Observation = new(Guid.NewGuid(), At, At, value, "Good", false, Guid.Empty, Guid.Empty, 1, "Simulation", "Source", At, null, null, null, 1, 1),
        Rules = [new(Guid.NewGuid(), 1, 10, 8)]
    };

    private static InvestigationEvidence Evidence(InvestigationPointSnapshot point, string id) =>
        new(id, "PointStatus", point.Code, ["历史事实不能代替当前确认。"], new string('a', 64), At, point.PointId, PointStatus: InvestigationAssessmentPolicy.Evaluate(point));

    [Fact]
    public void BadTemperatureCannotEraseTrustedHighVibration()
    {
        var vibration = Point();
        var temperature = Point(90) with
        {
            Code = "TEMPERATURE",
            Unit = "degC"
        };
        temperature = temperature with
        {
            Observation = temperature.Observation! with
            {
                Quality = "Bad",
                Value = null
            }
        };
        var result = InvestigationAssessmentPolicy.FromEvidence([Evidence(vibration, "E1"), Evidence(temperature, "E2")])!;
        Assert.Equal("Partial", result.DataTrust);
        Assert.Equal("Present", result.AnomalyEvidence);
        Assert.Equal("Unverifiable", result.Points[1].CurrentTrust);
        Assert.Equal(new[] { "E1", "E2" }, result.EvidenceIds);
    }

    [Fact]
    public void FreshLowPointsHaveOnlyBoundedNoAnomalyConclusion()
    {
        var result = InvestigationAssessmentPolicy.FromEvidence([Evidence(Point(6), "E1"), Evidence(Point(6), "E2"), Evidence(Point(6), "E3")])!;
        Assert.Equal("Trusted", result.DataTrust);
        Assert.Equal("NotFound", result.AnomalyEvidence);
        Assert.Equal("CurrentReadingsAtAsOf", result.Scope);
        Assert.Contains("NoRootCauseOrSafetyConclusion", result.Reasons);
    }

    [Fact]
    public void StaleAllPointsAndAnActiveHistoricalAlarmCannotEstablishCurrentAnomaly()
    {
        var point = Point();
        point = point with
        {
            Observation = point.Observation! with
            {
                EffectiveAt = At.AddMinutes(-1)
            }
        };
        var alarm = new InvestigationEvidence("E2", "Alarm", "活动历史告警", ["历史告警仍为 Active。"], new string('b', 64), At, Guid.NewGuid());
        var result = InvestigationAssessmentPolicy.FromEvidence([Evidence(point, "E1"), alarm])!;
        Assert.Equal("Unverifiable", result.DataTrust);
        Assert.Equal("Insufficient", result.AnomalyEvidence);
    }

    [Fact]
    public void ConnectedPeriodicOpcUaStillNeedsMatchingFreshObservationIdentity()
    {
        var point = Point() with
        {
            SourceKind = "OpcUa",
            Health = new(Guid.Empty, 1, "Connected", At, At, At, 0, null)
        };
        point = point with
        {
            Observation = point.Observation! with
            {
                SourceKind = "OpcUa"
            }
        };
        Assert.Equal("Trusted", InvestigationAssessmentPolicy.Evaluate(point).CurrentTrust);
        Assert.Equal("Present", InvestigationAssessmentPolicy.Evaluate(point).CurrentAnomaly);
        Assert.Equal("Unverifiable", InvestigationAssessmentPolicy.Evaluate(point with
        {
            Observation = null
        }).CurrentTrust);
    }

    [Fact]
    public void ChangesSilenceAndFreshKeepAliveDoNotProveFreshMeasurementOrSensorFailure()
    {
        var point = Point(6) with
        {
            SourceKind = "OpcUa",
            SamplingMode = "Changes",
            Health = new(Guid.Empty, 1, "Connected", At, At, At, 0, null)
        };
        point = point with
        {
            Observation = point.Observation! with
            {
                EffectiveAt = At.AddMinutes(-1),
                SourceKind = "OpcUa"
            }
        };
        var evaluated = InvestigationAssessmentPolicy.Evaluate(point);
        var result = InvestigationAssessmentPolicy.FromEvidence([Evidence(evaluated, "E1"), Evidence(Point(6), "E2")])!;
        Assert.Equal("Partial", result.DataTrust);
        Assert.Equal("Insufficient", result.AnomalyEvidence);
        Assert.Contains("StaleObservation", evaluated.Reasons);
        Assert.Contains("SilenceDoesNotProveFailure", evaluated.Reasons);
    }

    [Theory]
    [InlineData("NoReading")]
    [InlineData("ReceiveFallback")]
    [InlineData("MappingChanged")]
    [InlineData("ConfigurationChanged")]
    [InlineData("SourceVersionChanged")]
    [InlineData("LegacyVersionUnknown")]
    [InlineData("Bad")]
    [InlineData("TimeAnomaly")]
    [InlineData("AfterAsOf")]
    [InlineData("Overflow")]
    [InlineData("Disconnected")]
    [InlineData("HealthExpired")]
    [InlineData("HealthVersionChanged")]
    [InlineData("SamplingUnknown")]
    public void UnverifiedInputsCannotSupportCurrentAnomaly(string scenario)
    {
        var point = Point();
        var reading = point.Observation!;
        point = scenario switch
        {
            "NoReading" => point with { Observation = null },
            "ReceiveFallback" => point with { Observation = reading with { TimestampOrigin = "Receive" } },
            "MappingChanged" => point with { Observation = reading with { MappingVersion = 2 } },
            "ConfigurationChanged" => point with { ConfigurationCurrent = false },
            "SourceVersionChanged" => point with { SourceVersion = 2 },
            "LegacyVersionUnknown" => point with { Observation = reading with { SourceVersion = null, PointVersion = null } },
            "Bad" => point with { Observation = reading with { Quality = "Bad" } },
            "TimeAnomaly" => point with { Observation = reading with { TimeAnomaly = true } },
            "AfterAsOf" => point with { Observation = reading with { ReceivedAt = At.AddSeconds(1) } },
            "Overflow" => point with { Observation = reading with { RawStatusCode = 0x480 } },
            "Disconnected" => point with { SourceKind = "OpcUa", Health = new(Guid.Empty, 1, "Disconnected", At, At, At, 0, null) },
            "HealthExpired" => point with { SourceKind = "OpcUa", Health = new(Guid.Empty, 1, "Connected", At.AddSeconds(-11), At, At, 0, null) },
            "HealthVersionChanged" => point with { SourceKind = "OpcUa", Health = new(Guid.Empty, 2, "Connected", At, At, At, 0, null) },
            _ => point with { SourceKind = "OpcUa", SamplingMode = "Unknown", Health = new(Guid.Empty, 1, "Connected", At, At, At, 0, null) }
        };
        if (point.SourceKind == "OpcUa" && point.Observation != null)
        {
            point = point with
            {
                Observation = point.Observation with
                {
                    SourceKind = "OpcUa"
                }
            };
        }
        var result = InvestigationAssessmentPolicy.Evaluate(point);
        Assert.Equal("Unverifiable", result.CurrentTrust);
        Assert.Equal("Insufficient", result.CurrentAnomaly);
        if (scenario == "Disconnected")
        {
            Assert.Contains("SourceNotConnected", result.Reasons);
        }
        if (scenario is "HealthExpired" or "HealthVersionChanged")
        {
            Assert.Contains("SourceHealthUnverified", result.Reasons);
        }
        if (scenario == "SamplingUnknown")
        {
            Assert.Contains("SamplingContractUnknown", result.Reasons);
        }
    }

    [Theory]
    [InlineData(6, "Insufficient")]
    [InlineData(12, "Present")]
    public void UninspectedPointsPreventGlobalAllClearWithoutErasingObservedAnomaly(double value, string expected)
    {
        var result = InvestigationAssessmentPolicy.FromEvidence([Evidence(Point(value) with { UninspectedPointCount = 1 }, "E1")])!;
        Assert.Equal("Partial", result.DataTrust);
        Assert.Equal(expected, result.AnomalyEvidence);
    }

    [Fact]
    public void MissingThresholdAndHysteresisAreNotAnAllClear()
    {
        Assert.Equal("Insufficient", InvestigationAssessmentPolicy.Evaluate(Point(6) with
        {
            Rules = []
        }).CurrentAnomaly);
        Assert.Equal("Insufficient", InvestigationAssessmentPolicy.Evaluate(Point(9)).CurrentAnomaly);
        Assert.Equal("Insufficient", InvestigationAssessmentPolicy.Evaluate(Point(6) with
        {
            RulesTruncated = true
        }).CurrentAnomaly);
        Assert.Null(InvestigationAssessmentPolicy.FromEvidence([]));
    }

    [Theory]
    [InlineData("Periodic", false, 1, "Partial")]
    [InlineData("Periodic", true, 1, "Partial")]
    [InlineData("Periodic", false, 0.5, "Partial")]
    [InlineData("Changes", false, 1, "Partial")]
    public void WindowCoverageDoesNotRewriteIndependentCurrentReading(string mode, bool gap, double coverage, string expected)
    {
        var trend = new MeasurementTrend(Guid.NewGuid(), "mm/s", At.AddMinutes(-1), At, "1m", At, [],
            [new(At.AddMinutes(-1), 1, 2, 1.5, 60, 0, 0, 0, gap, coverage, mode)], false, false);
        var window = InvestigationAssessmentPolicy.Window("EventTrend", trend);
        Assert.Equal(expected, window.Trust);
        Assert.Equal("Trusted", InvestigationAssessmentPolicy.Evaluate(Point() with
        {
            Windows = [window]
        }).CurrentTrust);
    }

    [Fact]
    public void LegacyTrustMetadataAloneCannotBecomeACauseHypothesis()
    {
        var point = Evidence(Point(), "E1");
        var answer = new InvestigationResult("1", LegacyInvestigationEngine.SafeSummary,
            [new(point.Facts[0], ["E1"])],
            [new("可能传感器异常。", "weak", ["E1"], "核对现场记录。", point.Facts[0], "尚未核实现场情况。")], [], [], [], ["E1"]);
        Assert.Throws<LlmException>(() => LegacyInvestigationEngine.ValidateFacts(InvestigationContract.Serialize(answer), [point]));
    }

    [Theory]
    [InlineData("Trusted", "NotFound")]
    [InlineData("Unverifiable", "Present")]
    public void LegacyModelCannotRewriteStructuredServerLabels(string trust, string anomaly)
    {
        var point = Evidence(Point(), "E1") with
        {
            State = new("Available", "Trusted", "Present")
        };
        var answer = new InvestigationResult("1", LegacyInvestigationEngine.SafeSummary,
            [new(point.Facts[0], ["E1"])], [], [], [], [], ["E1"],
            StateClaims: [new("E1", "Available", trust, anomaly)]);
        Assert.Throws<LlmException>(() => LegacyInvestigationEngine.ValidateFacts(InvestigationContract.Serialize(answer), [point]));
    }
}
