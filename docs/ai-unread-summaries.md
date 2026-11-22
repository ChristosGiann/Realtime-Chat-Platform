# On-demand unread group summaries (#88)

This optional feature summarizes the current member's unread messages in a **Group** conversation. It does not summarize Direct conversations and does not run in the background. PostgreSQL remains the sole durable authority. No AI summary, cache, embedding, model file or receipt is stored in PostgreSQL or Redis.

## HTTP contract and snapshot

`POST /api/conversations/{conversationId}/unread-summary` uses the existing authenticated cookie, CSRF token and conversation membership checks. It returns `404` for a non-member, `409` for a Direct conversation, `204` when there are no unread messages, `422` when the unread backlog exceeds the configured hard limit, and a non-fatal `503` when disabled, misconfigured or inference fails. A successful `200` response contains:

```json
{
  "fromReadMessageId": null,
  "throughMessageId": "00000000-0000-0000-0000-000000000001",
  "throughCreatedAt": "2026-11-20T10:00:00Z",
  "unreadMessagesSummarized": 1,
  "overview": "Η ομάδα συζήτησε το πλάνο.",
  "keyPoints": [],
  "actionItems": [],
  "unresolvedQuestions": []
}
```

The backend takes a PostgreSQL repeatable-read snapshot. It checks current membership, verifies Group type, reads `LastReadMessageId`, and selects **other senders'** messages after that anchor in `(CreatedAt, Id)` order, matching the durable unread-count semantics. The last selected message defines `throughMessageId`/`throughCreatedAt`. Its transaction ends **before** any generator call. An integration test blocks the fake generator while a concurrent message send, which needs the conversation `FOR UPDATE` lock, completes. A later message is outside the captured snapshot. Before returning a generated result, the API checks authorization and the exact `MembershipId` again; revocation or rejoin discards the result.

Generating or displaying a summary does **not** update `LastReadMessageId` or `LastDeliveredMessageId`, send a receipt, change unread count, or touch SignalR. The UI shows the snapshot boundary and, when the read anchor is unchanged, the difference between current unread count and summarized count as newer messages. This is an on-demand HTTP result, not a durable replay.

## Local Ollama configuration

The feature is off by default. Normal chat, PostgreSQL readiness and SignalR do not depend on Ollama. Install/start Ollama separately and explicitly pull a model, for example `ollama pull qwen3:4b`; no model is downloaded by the application. Set the following in the API environment (PowerShell examples):

```powershell
$env:UnreadSummaries__Enabled = 'true'
$env:UnreadSummaries__BaseUrl = 'http://localhost:11434'
$env:UnreadSummaries__Model = 'qwen3:4b'
$env:UnreadSummaries__TimeoutSeconds = '60'
$env:UnreadSummaries__ChunkSize = '50'
$env:UnreadSummaries__MaxMessages = '200'
```

Allowed ranges: timeout 1–300 seconds, chunk size 1–100, max messages 1–500, with chunk size no greater than max. A request over `MaxMessages` returns `422` without truncation or an LLM call. Each chunk receives at most `ChunkSize` messages. Multiple chunk summaries are combined by one final synthesis call, so the default request uses at most five model calls. These limits bound work; model latency, context capacity and summary quality depend on the locally selected model/hardware. The timeout covers all chunk and synthesis calls together.

The adapter uses Ollama's [generate API and JSON schema `format`](https://github.com/ollama/ollama/blob/main/docs/api.md) through `HttpClient`, with non-streaming output. `IUnreadSummaryGenerator` separates the conversation snapshot from the provider, allowing deterministic tests without Ollama. Input contains only message ordinal, per-request sender pseudonym and message text. It contains no Identity/profile IDs, email/username metadata, membership IDs, cookies, CSRF tokens, connection IDs, Redis state or receipts. **Message text itself can contain personal data supplied by participants**; administrators should configure only an approved local model endpoint. Message content is untrusted data. The system instructions explicitly tell the model to ignore instructions inside chat messages, avoid invented facts and respond concisely in Greek; no prompt can guarantee semantic obedience by every model. Structured output is parsed into a typed DTO, with required non-empty overview, up to eight items per list and bounded text lengths. Malformed or unavailable responses become a local summary error; chat continues.

## Verification and limits

PostgreSQL/Testcontainers integration tests cover membership, read anchors, fixed boundaries, concurrent send outside the LLM wait, revocation and rejoin, chunk limits, timeout, and read/delivery invariants. Adapter tests inspect the exact prompt fields and malformed output. Playwright UI tests cover group-only visibility, loading, failure/retry, no mark-read mutation, snapshot display, mobile/desktop widths and overflow. The real browser/API/PostgreSQL E2E uses a deterministic loopback Ollama HTTP stub and does not download a model or contact an external AI service. The ordinary Stage 1–6 E2E and distributed semantics remain separate.

No production Ollama deployment, model quality evaluation or AI security guarantee is claimed here. The app does not provide automatic summaries, Direct summaries, persistent summary history, embeddings, vector search or paid-provider integration.
