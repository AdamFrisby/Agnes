namespace Agnes.Host.Channels;

/// <summary>
/// An explicit link between an external chat id (on a given bridge) and an Agnes device/identity, the
/// result of the linking step. The presence of a link is the whole authorization: an inbound message from a
/// chat with no link is treated as anonymous and can neither approve a permission nor steer a session.
/// <see cref="DeviceId"/> reuses the device-pairing identity model (a <c>DeviceRegistry</c> device id), so a
/// bridge reply is scoped to a real, revocable identity exactly like a paired client.
/// </summary>
public sealed record ChannelLink(string BridgeId, string ExternalChatId, string DeviceId, DateTimeOffset LinkedAt);
