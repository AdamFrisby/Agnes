namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// A display projection of one host-side <see cref="Agnes.Abstractions.PendingMessage"/> — its stable id
/// (so reorder/remove/send-now target the right entry host-side) plus a flattened text preview for the
/// pending-queue strip. Rebuilt from each <see cref="Agnes.Abstractions.PendingQueueEvent"/> snapshot, so
/// the strip stays consistent across every client on the session. See <c>sessions/03</c>.
/// </summary>
public sealed record PendingMessageView(string Id, string Text);
