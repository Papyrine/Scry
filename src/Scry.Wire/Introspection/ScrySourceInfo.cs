namespace Scry.Wire;

/// <summary>A queryable source (the root of a query): its name, kind, and the model type it yields.</summary>
public sealed record ScrySourceInfo(string Name, string Kind, string Model);