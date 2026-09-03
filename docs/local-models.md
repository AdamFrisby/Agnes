# Local models

Agnes runs local models through the **GitHub Copilot CLI's BYOK mode**: point Copilot at any
OpenAI-compatible endpoint and it uses that instead of GitHub's model routing, with no GitHub account
involved.

Everything here was established against **copilot v1.0.81** by capturing what it puts on the wire. There
are three things you need to know, and all three fail in ways that look like something else.

## Configuring it

**Settings → Local models**, on the desktop. Enter the endpoint, press **Fetch models** to see what it
serves, pick one, and save. Two switches are on by default and both should stay on unless you know
otherwise — they are the difference between a local model working and not starting at all, and each is
explained below.

The endpoint and key are stored **on the host**, not on the device you configured them from, and the key
is never sent back to a client — a settings screen is told only that one exists. Discovery is proxied
through the host too, so the model server can live on the host's network and a paired phone never holds
the credential.

Settings are read when a session **launches**, so a change applies to the next session; a running one
keeps the model it started with. No host restart is needed.

The same thing in host config, for an unattended setup:

## The short version

```jsonc
// appsettings.json
{
  "Agnes": {
    "Copilot": {
      "Offline": true,                      // no GitHub at all
      "Provider": {
        "BaseUrl": "http://10.0.0.36:13305/v1",
        "Type": "OpenAi",
        "ApiKey": "…",                      // omit for servers that need none
        "ModelId":   "gpt-5.4",             // see "reasoning effort" below
        "WireModel": "Qwen38-27B-Q5XL"      // your model's real name
      }
    }
  }
}
```

`ExcludedTools` defaults to the recommended set when a provider is configured, so it is not shown above.

## 1. BYOK works over ACP, not over `-p`

Copilot honours BYOK in `--acp` mode — the mode Agnes uses. It does **not** honour it in `-p`
(programmatic) mode: a `-p` run with `COPILOT_PROVIDER_BASE_URL` pointed at a **dead port** still answered,
billed as `claude-haiku-4.5` against GitHub.

That matters if you test by hand: `copilot -p` will tell you your local setup works when it is not being
used at all. Instrumenting the endpoint is the only reliable check — over ACP you see
`GET /v1/models` followed by `POST /v1/chat/completions`.

## 2. `apply_patch` breaks strict servers

Copilot offers `apply_patch` as an OpenAI **custom tool with a Lark grammar** — `"type": "custom"`
rather than `"type": "function"`. A server implementing only function tools rejects the **whole** tools
array:

```
Failed to parse tools: Unsupported tool type: {"custom":{…},"type":"custom"}
```

No turn can start. So when a provider is configured, Agnes defaults `ExcludedTools` to
`["apply_patch"]`. Copilot keeps its ordinary file tools; you lose one editing path.

Set `"Agnes:Copilot:ExcludedTools": []` to opt out.

## 3. Reasoning effort, and why the model id is not cosmetic

Copilot derives `reasoning_effort` from the model **id**, and for an id it does not recognise it sends
`"max"` — which is not an OpenAI-standard value (the standard set is low / medium / high). A server that
validates the field rejects the request:

```
Jinja Exception: Unexpected reasoning effort max. Supported types are xhigh (default), medium, and low.
```

Captured directly, the same request differs only by model id:

| `ModelId` | `reasoning_effort` sent |
| --- | --- |
| `gpt-4.1` | `max` |
| `claude-sonnet-4` | `max` |
| **`gpt-5.4`** | **`medium`** |

So set `ModelId` to a well-known model whose profile your server accepts, and `WireModel` to your local
model's actual name. Copilot documents this split for Azure deployments; it is also the fix here.

**Agnes does not choose one for you.** The model id also selects prompting strategy and token limits, so
picking it is a decision about how the agent behaves, not a compatibility detail to hide.

## Offline mode

`"Offline": true` sets `COPILOT_OFFLINE`, which disables GitHub authentication, telemetry, web tools, the
GitHub MCP server and auto-update. This is what "no GitHub credentials" actually means — BYOK alone still
leaves Copilot reaching GitHub for everything that is not inference.

Copilot requires a provider for offline mode, so Agnes ignores the flag unless one is configured rather
than passing it through to fail at launch.

## Discovery

`CopilotLocalModels.ListAsync` calls `GET /v1/models` — which every OpenAI-compatible server implements,
and which Copilot itself calls at startup. Agnes calls it first so a wrong URL or key fails in settings
with a readable message instead of inside a session as a failed turn. The base URL may be given with or
without a trailing `/v1`.

"Could not reach it" and "it serves no models" are reported differently: null versus an empty list.

## Verified

End to end against a Lemonade server on local hardware — `Qwen38-27B-Q5XL`, one prompt through the real
ACP adapter, 575 events over 149 seconds, file written to the working directory with exactly the
requested content.

Tool calling is required and worth checking before blaming Agnes:

```bash
curl -s "$BASE/v1/chat/completions" -H "Authorization: Bearer $KEY" \
  -d '{"model":"…","messages":[{"role":"user","content":"weather in Paris?"}],
       "tools":[{"type":"function","function":{"name":"get_weather",
       "parameters":{"type":"object","properties":{"city":{"type":"string"}}}}}]}'
```

A model that cannot call tools cannot drive a coding agent, however good its prose.
