/// <summary>
/// Where a result's attachment handles come from: for each one, which member of the projected object
/// it fills, which source and member it fetches, and where in that same object its row's key values
/// landed. Built while the query is translated, since that is the only point at which the projection
/// and the model behind it are both in view.
/// </summary>
sealed record AttachmentPlan(IReadOnlyList<AttachmentBinding> Bindings);