# Attachments and file browser

| | |
|---|---|
| **Category** | Git & files |
| **Plugin surface** | Core host/protocol + `Agnes.Ui.Core` — no new plugin interface needed |
| **Priority** | P2 |
| **Rough effort** | M |

## Background

Two related but distinct needs come up constantly when working with a coding agent remotely. First, a user often wants to point the agent at a file that's already in the workspace — "look at `src/Foo.cs`" — without re-uploading anything, since the file already exists on the host's disk. Second, a user working from a phone or a browser, away from the machine running the agent, needs to get files that only exist locally on *their* device — a screenshot of a bug, a design mockup, a log file — into the session at all. These are different operations (referencing vs. transferring) and conflating them into one "attach a file" concept tends to produce a UI that's confusing about what's actually happening to the file.

Beyond attaching things into a conversation, users also need to browse and manage the workspace itself from a remote client: see what files exist, open one to read it, make a quick edit without spinning up a whole agent turn, or clean up a file it's easier to just delete than to ask the agent to delete. Without any file-browser surface, a remote client can only see the workspace through whatever the agent happens to show it — which is a real gap for a product where the client is very often not physically at the machine holding the files.

## Current state in Agnes

The protocol layer already anticipates multi-modal prompt content: `AgentCapabilities` models `PromptImage`/`PromptAudio` as per-agent negotiated capability flags, and `IAgentSession.PromptAsync(IReadOnlyList<ContentBlock> content, ...)` already accepts `ImageContent` and `ResourceLinkContent` blocks (defined in `Agnes.Abstractions/Content.cs`). What's missing is everything client-facing: no upload/attach UX, no host-side step that turns a client-uploaded file into a stable on-disk path the agent can read, and no file-browser UI at all.

## Proposed design

**Referencing an existing file** ("link file") needs no new plumbing beyond what `ContentBlock` already supports: a `ResourceLinkContent` pointing at a path already in the workspace, included directly in a `PromptAsync` call.

**Attachments** (uploading a file from the client device) are mostly protocol plumbing on top of what already exists:

```csharp
// Agnes.Protocol — new hub method
Task<string> UploadAttachment(string sessionId, string fileName, byte[] content);   // returns a materialized workspace-relative path
```

Design notes:

- **Materialize uploads to a real on-disk path, not an inline blob passed to the agent.** Every ACP-connected coding agent already knows how to read a file from disk — that's its primary mode of operation — but not every agent CLI has the same support for inline binary content embedded in a prompt. Writing the upload to disk first and passing a `ResourceLinkContent` path means attachments work uniformly across every current and future agent adapter without each one needing its own inline-binary handling. `Agnes.Host` writes the upload under a configured location inside the session's working directory (workspace-relative, gitignored by default so uploads don't silently get committed), then returns that path for the client to include in its next `PromptAsync` call.
- **Attachments are ephemeral drafts before send.** A file picked or pasted but not yet sent should render as a preview (thumbnail for images, a filename chip otherwise) and should be discarded if the client reloads without sending — treating it as part of the in-progress draft, not as committed session content, avoids leaving orphaned uploads on disk for messages that were never actually sent.
- **Post-send rendering** reuses the same image-preview component used for pre-send drafts; an image that's too large or fails to load falls back to a clear placeholder rather than a broken-looking blank space.

**File browser** is a separate, larger surface: new `IAgnesHub` methods for list/create/rename/delete/download, operating directly on the filesystem at the session's working directory.

- **Path safety is the load-bearing concern here.** Every one of these operations takes a client-supplied relative path, and the host must resolve and validate it stays within the session's working directory before touching disk — a `../../etc/passwd`-style path must be rejected, not merely worked around by convention. This validation is also needed by workspace transfer (`../connectivity/03-session-handoff.md`); building one shared "resolve and validate a path is within the workspace root" helper and using it in both places is both less code and less risk than two independently-written path-safety checks, where a bug in either one is a directory-traversal vulnerability.
- Conflict handling on upload (a file already exists at the target path) needs an explicit policy — skip, replace, or keep-both with a renamed copy — surfaced to the user rather than silently picking one.
- An in-app text editor for browsing/editing files is a client-side (`Agnes.Ui.Core`) concern once read/write endpoints exist server-side; it doesn't need new host-side design.
- File operations should go through a dedicated structured RPC surface rather than being layered onto `ICliFallback`'s raw terminal fallback — mixing "structured file operations with typed results" and "arbitrary shell command passthrough" would make both harder to reason about and secure independently.

## Acceptance criteria

- Given a file already in the workspace, when the user references it by path in a message, then the agent receives a `ResourceLinkContent` pointing at that path with no upload occurring.
- Given a file picked or pasted on a client device, when the user sends the message, then the file is uploaded, materialized to a workspace-relative on-disk path, and the agent receives a prompt referencing that path — not inline binary data.
- Given an attachment that has been picked but not yet sent, when the client reloads before sending, then the draft attachment is gone and no orphaned file was written to the workspace.
- Given a file-browser delete/rename/download request with a path that attempts to escape the session's working directory (e.g. via `..` segments), then the host rejects the request without touching any file outside the workspace root.
- Given an upload whose target filename already exists, when the conflict policy is set to each of skip/replace/keep-both, then the resulting file state matches that policy exactly (original preserved / original overwritten / both present under distinct names).
- Given an oversized or corrupt image sent as an attachment, when it's rendered in the transcript, then a clear placeholder is shown instead of a broken image or a silent failure.
- The shared path-validation helper used by the file browser is exercised by an automated test asserting directory-traversal attempts are rejected, and the same helper (or provably equivalent logic) is used by session-handoff's workspace transfer.

## Open questions

- Exact conflict-policy default (skip vs. prompt-every-time) — a UX decision with no architectural implications, can be decided at implementation time.
- Should the file browser support binary preview (e.g. images, PDFs) beyond text, or defer that to a later pass? Leaning toward text + image only for a first version, given those cover the overwhelming majority of real use.
