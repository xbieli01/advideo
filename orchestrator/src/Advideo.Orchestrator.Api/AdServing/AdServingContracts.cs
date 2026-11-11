using Advideo.AdServing.Matching.Model;

namespace Advideo.Orchestrator.Api.AdServing;

public sealed record AdDecisionResponse(
    string DecisionId,
    string PlatformId,
    string VideoId,
    double T,
    SegmentRef Segment,
    AdPayload Ad,
    IReadOnlyList<MatchHit> MatchedOn,
    TrackingInfo Tracking);

/// <summary>The snippet POSTs {decisionId, type, t} to <see cref="EventsUrl"/> for impression / viewable / click / close.</summary>
public sealed record TrackingInfo(string EventsUrl, IReadOnlyList<AdEventType> Events);

public sealed record AdEventRequest(string DecisionId, AdEventType Type, double? T = null);

public sealed record AdEventResponse(bool Accepted, bool Duplicate, decimal Cost);

public sealed record SegmentRef(double StartSec, double EndSec);

public sealed record AdPayload(
    string CampaignId,
    string AdvertiserId,
    Creative Creative,
    AdSlot Slot,
    double ShowFromSec,
    double ShowToSec,
    PricingModel PricingModel);

public sealed record ExplainResponse(
    string PlatformId,
    string VideoId,
    double T,
    SegmentRef? Segment,
    string? WinnerCampaignId,
    string? NoAdReason,
    IReadOnlyList<ExplainCandidate> Candidates);

public sealed record ExplainCandidate(
    string CampaignId,
    double Relevance,
    decimal Ecpm,
    double Rank,
    IReadOnlyList<MatchHit> MatchedOn,
    string? CreativeId,
    string? SlotId,
    string? RejectedBecause);

public sealed record ValidationErrorResponse(IReadOnlyList<string> Errors);
