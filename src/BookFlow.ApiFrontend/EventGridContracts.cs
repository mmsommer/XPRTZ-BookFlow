namespace BookFlow.ApiFrontend;

/// <summary>Event Grid's synchronous subscription-validation response.</summary>
/// <param name="ValidationResponse">The Event Grid subscription validation code to echo.</param>
public sealed record SubscriptionValidationResponse(string ValidationResponse);
