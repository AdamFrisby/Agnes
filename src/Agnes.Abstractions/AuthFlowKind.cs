namespace Agnes.Abstractions;

/// <summary>
/// Which real-world situation a bootstrap auth method is meant to serve, so a client can group the methods a
/// host advertises into distinct UX buckets rather than one undifferentiated list
/// (see <c>.ideas/connectivity/04-device-linking-and-restore.md</c>). This is a product-level distinction,
/// not a difference in how tokens are minted — several methods can share a kind.
/// </summary>
public enum AuthFlowKind
{
    /// <summary>Add a human-facing device to an account already trusted elsewhere — e.g. scan a code the
    /// already-paired desktop shows on the new phone. The common case.</summary>
    NewDevice,

    /// <summary>Recover access with no already-trusted device to vouch for the new one — a credential set
    /// aside in advance or an identity check independent of any previously-paired device.</summary>
    RestoreAccount,

    /// <summary>Authorize a headless process (a daemon or terminal session), not a person "signing in" — the
    /// SSH <c>authorized_keys</c>-style flow where "scan this code with your phone" makes no sense.</summary>
    ConnectTerminal,
}
