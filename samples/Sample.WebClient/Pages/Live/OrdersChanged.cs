namespace Sample.WebClient.Pages.Live;

/// <summary>
/// What the MessagePipe page publishes: the live query's latest answer. A message type of the app's
/// own, so that what subscribes to it depends on the app's vocabulary and not on where the rows came
/// from.
/// </summary>
public record OrdersChanged(IReadOnlyList<OrderRow> Orders);
