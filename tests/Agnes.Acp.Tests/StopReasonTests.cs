using Agnes.Abstractions;
using Agnes.Acp;

namespace Agnes.Acp.Tests;

/// <summary>
/// Narrowing an ACP stop reason is lossy, and the loss matters: an unrecognised reason maps to
/// <see cref="StopReason.EndTurn"/>, which otherwise reads as a clean completion. These pin the rule that
/// the caller can always tell a real completion from a fallback.
/// </summary>
public sealed class StopReasonTests
{
    [Theory]
    [InlineData("end_turn", StopReason.EndTurn)]
    [InlineData("max_tokens", StopReason.MaxTokens)]
    [InlineData("max_turn_requests", StopReason.MaxTurnRequests)]
    [InlineData("refusal", StopReason.Refusal)]
    [InlineData("cancelled", StopReason.Cancelled)]
    public void Known_reasons_map_and_are_recognised(string raw, StopReason expected)
    {
        Assert.Equal(expected, AcpMap.ToStopReason(raw));
        Assert.True(AcpMap.IsKnownStopReason(raw));
    }

    [Theory]
    [InlineData("endTurn")]   // camelCase variant
    [InlineData("error")]
    [InlineData("timeout")]
    [InlineData("length")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_reasons_fall_back_to_end_turn_but_are_flagged(string? raw)
    {
        // The fallback keeps a turn terminating; the flag is what stops it passing silently as success.
        Assert.Equal(StopReason.EndTurn, AcpMap.ToStopReason(raw));
        Assert.False(AcpMap.IsKnownStopReason(raw));
    }

    [Fact]
    public void Turn_ended_preserves_the_raw_wire_value()
    {
        var ended = new TurnEndedEvent(AcpMap.ToStopReason("error"), "error");

        Assert.Equal(StopReason.EndTurn, ended.Reason);
        Assert.Equal("error", ended.RawReason); // without this the fallback is undiagnosable after the fact
    }

    [Fact]
    public void Turn_ended_raw_reason_is_optional()
        => Assert.Null(new TurnEndedEvent(StopReason.EndTurn).RawReason);
}
