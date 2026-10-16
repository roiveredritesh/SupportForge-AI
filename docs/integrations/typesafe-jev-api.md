# TypeSafe Jev API reference (as used by SupportForge)

This page is how SupportForge calls TypeSafe's Jev model. It was taken from the official TypeSafe SDKs: `typesafe-sdk` 0.7.5 on PyPI and `@typesafe-ai/sdk` 0.7.0 on npm. Their request and response models are generated from TypeSafe's OpenAPI document (`https://api.typesafe.ai/openapi.json`), so they're authoritative for the wire format. For product guidance (how to phrase questions, what confidence means) see [docs.typesafe.ai](https://docs.typesafe.ai/introduction).

SupportForge doesn't use an SDK, because none exists for .NET. `SupportForge.Agents.Jev.JevClient` implements the HTTP contract below directly.

## Endpoint and auth

| | |
|---|---|
| Base URL | `https://api.typesafe.ai` (the SDKs read `TYPESAFE_BASE_URL`) |
| Evaluate | `POST /v1/systemone` |
| List models | `GET /v1/models` |
| Auth | `Authorization: Bearer <api key>` (the SDKs read `TYPESAFE_API_KEY`; SupportForge reads `Jev:ApiKey`, env `Jev__ApiKey`) |
| Content type | `application/json` (multipart only when sending image assets, which SupportForge doesn't) |
| Default model | `jev-latest` |
| Request id | response header `x-typesafe-request-id` (log it; it's what TypeSafe support asks for) |

## Request

```json
{
  "model": "jev-latest",
  "state": "text" | { ... } | [ ... ],
  "questions": {
    "<name you choose>": { "type": "choice" | "noul" | "score" | "box", ... }
  }
}
```

- `state` is the content every question is evaluated against. It can be a string, a JSON object or a JSON array, and must be present.
- `questions` needs at least one entry. Each question is evaluated independently against the same state, and answers come back under the same names.

### Question types

| Type | Fields | Notes |
|---|---|---|
| `choice` | `instructions` (optional), `criteria` (required, non-empty object: label → description or `null`) | Picks one label. |
| `noul` | `instructions`, `criteria` (optional object `{ "true": "...", "false": "..." }`) | Yes/no or true/false. Needs `instructions` or at least one described outcome. |
| `score` | `instructions` (optional), `criteria` (required, non-empty ordered array; position = score level from 0) | Rubric rating. |
| `box` | `instructions` | Image localisation. Not used by SupportForge. |

`instructions` and criteria descriptions may be strings, JSON objects or arrays. SupportForge always sends strings. Optional fields that are unset are omitted rather than sent as `null`.

## Response (200)

```json
{
  "model": "jev-2026-09-15",
  "answers": {
    "<name>": { "type": "choice", "choice": "billing", "confidence": 0.9, "probabilities": { "billing": 0.8, "technical": 0.15, "other": 0.05 } },
    "<name>": { "type": "noul", "noul": 0.98 },
    "<name>": { "type": "score", "score": 1.7, "confidence": 0.9, "legend": { "0": "...", "1": "...", "2": "..." }, "probabilities": { "0": 0.1, "1": 0.1, "2": 0.8 } }
  },
  "usage": { "input_tokens": 120, "output_tokens": 12 }
}
```

- `model` is the resolved model, which may differ from the alias that was sent.
- `choice.confidence` is reported separately from `probabilities`, and the probabilities sum to about 1.
- `noul` is the probability of yes/true, from 0 to 1. Values near 0.5 mean uncertain.
- `score.score` is the probability-weighted average of the levels, so it can fall between levels. `probabilities` uses the level indices as string keys.
- `usage.input_tokens` is billable. Output tokens are currently free.

## Errors and retries

| Status | Meaning |
|---|---|
| 400 | Bad request |
| 401 | Authentication (missing or invalid key) |
| 403 | Permission denied |
| 404 | Not found (e.g. unknown model) |
| 422 | Validation error, body `{ "detail": [ { "loc": [...], "msg": "...", "type": "..." } ] }` |
| 429 | Rate limited; honours `retry-after` / `retry-after-ms` |
| 5xx | Server error |

The SDKs retry 408, 429 and 5xx up to 2 times with exponential backoff (0.5 s initial, 5 s max, 25% jitter), and send `X-TypeSafe-Retry-Count` on retries. Their default per-request timeout is 10 s.

SupportForge sits on a request's critical path, so it retries at most once. Each caller also enforces its own tighter time budget: the guard and triage fall back to the LLM path, failing open, when Jev doesn't answer in time.
