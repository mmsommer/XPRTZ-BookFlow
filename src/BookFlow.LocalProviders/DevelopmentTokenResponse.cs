namespace BookFlow.LocalProviders;

/// <summary>A short-lived JWT for a known development participant and role.</summary>
public sealed record DevelopmentTokenResponse(string Token);
