namespace Sample.WebClient.Pages.Live;

/// <summary>
/// The shape the live pages want, declared here rather than anywhere the server knows about. One
/// answer of the live query is a list of these — the whole current result, every time.
/// </summary>
public record OrderRow(int Id, string Region, decimal Amount);
