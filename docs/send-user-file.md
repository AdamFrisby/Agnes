# Sending the user a file

An agent running in an Agnes session can hand the person a file: a screenshot, a report, a diagram, a built
artifact. Every connected client receives it — the phone first, since the phone is where "look at this" is
most useful and least served by a transcript.

## Why it exists

Everything else an agent produces is either text in the transcript or a path in a message. Both fail the same
case: the agent finished something you are supposed to *look at*, and you are not at the machine. A path is
useless on a phone. Pasting a base64 image into the transcript is worse — it bloats a log that is replayed in
full to every client that ever joins.

So the file itself is the unit. The agent says "send this", the host copies it somewhere stable and appends
one small event naming it, and each client fetches the bytes on demand through the download path it already
has. The transcript stays cheap, and a 20 MB PDF is downloaded once by the one client that opens it.

## The tool

Agnes exposes its own MCP server to sessions; `send_user_file` is a tool on it.

| Parameter | | |
|---|---|---|
| `path` | required | Absolute path, or a path relative to the working directory, or the `/work/…` path as seen inside a sandbox. |
| `caption` | optional | One line of context shown with the file, e.g. "before vs after". |
| `sessionId` | optional | Omit when called by the agent itself; required with a device token. |

It returns a short confirmation — `Sent report.md (12 KB) to the user.` — and on refusal throws, so the reason
arrives as the tool's error text and the model can act on it instead of assuming delivery.

Authorization is the same rule the goal tools use, and it matters more here because this tool writes into a
transcript: **the token is the identity, never the argument**. A sandboxed agent authenticates with a
per-session token that *is* its session, so any `sessionId` it passes is ignored — it cannot drop a file into
somebody else's session. A paired device has no session of its own and must name one.

The three path forms exist because the agent doesn't necessarily know where it is. Inside a sandbox its
working directory is mounted at `/work`, so `/work/out/report.md` and `out/report.md` name the same host file;
stripping the `/work` prefix is a rename, not a trust decision — whatever is left still goes through the same
containment guard as a path typed by a client (`Files.WorkspacePaths.ResolveWithin`).

## The storage rule, and why

At the moment of sending, the host **copies** the file to:

```
<workspace>/.agnes/shared/<FileId>/<FileName>
```

and appends a `FileSharedEvent(FileId, FileName, RelativePath, Size, MimeType, Caption)` to the session log.
Clients fetch it by `RelativePath` through the ordinary guarded workspace download path
(`IAgnesHub.DownloadFile` / `ReadFile`) — no new endpoint, no new authorization surface.

Three deliberate choices:

- **Copy, not reference.** The agent keeps working. If the event only pointed at `out/report.md`, the file you
  open tomorrow would be whatever that path has become — or nothing. What was sent has to stay what was sent.
- **Copy, not move.** The original is still the agent's working file; taking it away would break the next step
  of its own task.
- **A fresh `FileId` per send.** Sending the same path twice produces two artifacts, so a later send can never
  silently rewrite an earlier one under someone's feet.

`.agnes/` self-ignores: a `.gitignore` containing `*` is written inside it rather than a line appended to the
project's own. The workspace is somebody's repository and Agnes must not author changes in it; a session may
not even be a git checkout; and a self-ignoring directory needs no knowledge of what is already ignored. The
same directory holds client-uploaded attachments, which are this feature's mirror image.

## The veto point

`BeforeFileSharedEvent` is dispatched on the event spine *before* anything is copied. An interceptor may
rewrite the `Caption` or `Cancel()` the send. The motivating case is a secrets scanner: a file that carries a
credential should never reach a phone, and a veto that happens after the copy would leave the bytes sitting in
the workspace for anyone with the link.

A veto is **reported**, not swallowed: the reason becomes the tool's error text, so the agent learns "that
file has a credential in it" and can do something else, rather than telling the user it sent something they
never received.

The appended `FileSharedEvent` is the observe-only fact after the action, and it rides the spine like every
other session event — a plugin can observe `FileSharedEvent` directly.

## How it is appended

Through the same path an agent's own events take (`HostSession.RecordFileSharedAsync` →
`AppendAndPublishAsync`): the `BeforeAgentEventEvent` redaction gate, then append to the event store, then
broadcast, then dispatch on the spine. That is what makes a sent file persisted, sequenced, replayed to a
client that joins an hour later, and visible to the push dispatcher — one code path, not a parallel one that
can drift.

A session with no live agent handle (a dormant session, shared from a paired device) takes the same three
steps inline rather than waking an agent process purely to write one row.

## Push

`FileSharedEvent` maps to `NotificationTrigger.FileShared` with the hint `Sent you a file: <FileName>`.

The **caption is not in the hint**. A push commonly lands on a lock screen; a caption is free text the model
wrote *about the contents* and is exactly the sort of thing that shouldn't be readable over someone's
shoulder. The file name is a name the agent chose for something it is deliberately handing over, which is a
different risk.

Devices toggle it independently of the other triggers (`PushNotificationPrefs.FileShared`, default on).

## Size cap

| Key | Default | |
|---|---|---|
| `Agnes:Sharing:MaxBytes` | 26214400 (25 MB) | Largest file an agent may send. |

Sending copies, and then every connected client pulls the file down — phones on mobile data included. A model
that decides to "send you the build" can name a multi-gigabyte artifact as easily as a screenshot. The cap
turns that into a clear refusal the agent can act on ("send something smaller, or tell the user where it is")
instead of a silent, very slow success.

## How each client receives it

Each head renders `FileSharedEvent` from the shared transcript model and downloads by `RelativePath` through
its existing workspace-file path.

- **Desktop** — a card in the transcript with a preview for image types and a save action.
- **Android** — the same card, plus the arrival is what the `FileShared` push trigger pages you about.
- **Web** — the same card; the browser handles the download.

## Availability

The tool is discovered by attribute from `AgnesMcpTools`, so no adapter carries a list of tool names that
needs extending. What *does* gate availability is whether the `agnes` MCP server is offered to a given session
at all — see `SessionManager.AddSandboxModel` and `Agnes:Sandbox:GuestMcpUrl`. Today that injection reaches
sandboxed sessions whose adapter implements `IModelEnvironmentAdapter`; widening it to every adapter (and to
unsandboxed sessions) is a separate piece of work on the MCP-config materialization, not on this feature.
