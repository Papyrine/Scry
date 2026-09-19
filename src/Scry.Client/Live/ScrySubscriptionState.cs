namespace Scry;

/// <summary>Where a <see cref="ScrySubscription"/> is in its life.</summary>
public enum ScrySubscriptionState
{
    /// <summary>Asking for the first time. No answer has arrived yet.</summary>
    Connecting,

    /// <summary>Connected, and holding the server's latest answer.</summary>
    Live,

    /// <summary>
    /// The connection ended and is being asked for again. The last answer delivered is still the
    /// latest one known, and may by now be out of date.
    /// </summary>
    Reconnecting,

    /// <summary>Over, because it was disposed or the server ended it for good.</summary>
    Closed,

    /// <summary>Over, because of a failure asking again would not fix. <see cref="ScrySubscription.Error"/> says which.</summary>
    Faulted
}
