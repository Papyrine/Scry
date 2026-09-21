/// <summary>
/// <see cref="IScryChangeBackplane"/> over MessagePipe's distributed publisher and subscriber. Which
/// transport carries it — Redis, NATS, a named pipe, memory — is whichever the host registered with
/// MessagePipe, and nothing here knows.
/// </summary>
/// <remarks>
/// A change travels as the text <see cref="ScryChange.Serialize"/> writes, keyed by one topic. A
/// string because MessagePipe's transports serialize what they carry, and a string is something every
/// one of them carries with no resolver, attribute or contract asked of this library's types.
/// </remarks>
sealed class MessagePipeChangeBackplane(
    IDistributedPublisher<string, string> publisher,
    IDistributedSubscriber<string, string> subscriber,
    ScryMessagePipeOptions options) :
    IScryChangeBackplane
{
    long dropped;

    /// <summary>How many messages under the topic were not changes, and were ignored.</summary>
    public long Dropped => Interlocked.Read(ref dropped);

    public ValueTask PublishAsync(ScryChange change, Cancel cancel) =>
        publisher.PublishAsync(options.Topic, change.Serialize(), cancel);

    public ValueTask<IAsyncDisposable> SubscribeAsync(Func<ScryChange, Cancel, ValueTask> handler, Cancel cancel) =>
        subscriber.SubscribeAsync(options.Topic, new Handler(this, handler), cancel);

    sealed class Handler(MessagePipeChangeBackplane owner, Func<ScryChange, Cancel, ValueTask> handler) :
        IAsyncMessageHandler<string>
    {
        public ValueTask HandleAsync(string message, Cancel cancellationToken)
        {
            // A topic is shared infrastructure. What is not one of these is somebody else's message,
            // or a newer node's, and is no reason to fault.
            if (!ScryChange.TryParse(message, out var change))
            {
                Interlocked.Increment(ref owner.dropped);
                return ValueTask.CompletedTask;
            }

            return handler(change, cancellationToken);
        }
    }
}
