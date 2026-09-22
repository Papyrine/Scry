namespace Sample.Model;

/// <summary>
/// Asks whichever endpoint handles it to reprice one order: a message the two NServiceBus sample
/// endpoints already shared, exposed to clients as a command by annotating it. Here because every end
/// needs the one type, and this is the assembly they already share.
/// </summary>
/// <remarks>
/// <para>
/// The existing-message case: the class was a bus message before it was a command, and becoming one
/// took an attribute and nothing else. It does not reference NServiceBus — the endpoints recognise it
/// by convention — so the model stays free of a dependency only two of the samples have.
/// </para>
/// <para>
/// Targeted, so the order's key binds by name and <c>Order</c> gains a <c>CanRepriceOrder</c> member.
/// A host with commands on handles it in-process with the sample's handler, unless — as the NServiceBus
/// server does — a bus adapter claims it and a worker handles it instead.
/// </para>
/// </remarks>
[Command(typeof(Order))]
public sealed class RepriceOrder
{
    public int Id { get; set; }
}
