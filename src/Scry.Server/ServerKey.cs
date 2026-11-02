/// <summary>
/// The key the server seals and signs with: <see cref="ScryOptions.CursorKey"/> where the host set
/// one, else a random key for this process. Cursors are sealed under it and ETags are signed with it,
/// so neither is valid on another process unless the host configured a key both share.
/// </summary>
static class ServerKey
{
    static byte[] ephemeral = RandomNumberGenerator.GetBytes(32);

    public static byte[] For(ScryOptions options) =>
        options.CursorKey ?? ephemeral;
}
