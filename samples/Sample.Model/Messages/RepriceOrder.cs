namespace Sample.Model;

/// <summary>
/// A message, for the NServiceBus sample: asks whichever endpoint handles it to reprice one order.
/// Here because both ends need the one type, and this is the assembly they already share.
/// </summary>
/// <remarks>
/// Not part of the queryable model. It carries no Scry attribute, so the generator never sees it and
/// the schema stamp does not move — the allow-list is default-deny, and this was never on it. Nor does
/// it reference NServiceBus: the endpoints recognise it by convention, so the model stays free of a
/// dependency only two of the samples have.
/// </remarks>
public sealed class RepriceOrder
{
    public int Id { get; set; }
}
