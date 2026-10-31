namespace BookFlow.LocalProviders;

/// <summary>A short-lived JWT for a known development participant and role.</summary>
/// <param name="Token">The short-lived development bearer JWT.</param>
public sealed record DevelopmentTokenResponse(string Token);
