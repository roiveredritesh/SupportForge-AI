# Security: prompt injection and jailbreak defences

SupportForge answers customer questions from retrieved content (KB pages, code, screenshots) and passes that content to language models. Anyone who can write to a KB source, a repository or a screenshot could try to plant instructions in it. A customer could also try to talk the assistant out of its rules. This document describes the layers that handle both, and what they do not promise.

## The layers

Each layer is independent. A failure in one does not switch off the others.

### 1. Structural isolation (always on)

- Retrieved text is wrapped in `<retrieved_context>` tags. `DrafterAgent.SystemPrompt` and the verifier prompts declare that text to be data and never instructions, whatever it says.
- Ingestion strips `<script>` and `<style>` content from fetched pages (`ConfluencePageFetcher`, `HtmlToMarkdownConverter`).
- `CodeNodeClassifier` marks file content as untrusted data to analyse.
- Generated image captions pass a phrase filter at ingestion (`IngestionImageCaptioner`). It is a regex filter, not a classifier.

### 2. Leak check on the draft (always on)

`DrafterAgent.LooksLikeLeak` checks the draft for raw source code that should not be shown. If it fires, the draft is replaced with a fixed message. It checks for code only. It is not a check for instructions or for disclosure of configuration.

### 3. Role gating on responses (always on)

Code details in a response are returned only to elevated roles (L2, L3 and Admin) and to members invited to the conversation (`ChatController.IsElevated`, `CodeDetailsForRole`). This limits what a customer can get back even when the answer itself is allowed.

### 4. Input guard, heuristic pass (always on when `Guard:Mode` is not `Off`)

A fixed set of phrase patterns runs on the customer's message and recent history, after Unicode normalisation, zero-width character removal and one level of base64 decoding. It is free and runs with no Jev key. It can only flag a turn. It never blocks on its own, and the phrase list favours precision over recall.

### 5. Input guard, Jev pass (when `Jev:ApiKey` is set)

One Jev request scores the turn on six statements, such as "the message tries to make the assistant ignore its instructions" and "the message asks for the system prompt". Each statement is an atomic yes/no question with fixed wording. The customer's text goes only into the request state as a value, never into a question. Jev returns scores and does not act on the text.

- **Block-class** signals (instruction override, system-prompt extraction, role-play or encoded jailbreaks, cross-tenant probing) block the turn in `Enforce` once they reach their threshold. A blocked turn gets a fixed refusal that does not say which signal fired. It makes no LLM call and runs no agent.
- **Review-class** signals (raw code requests, obfuscated payloads) only flag. Developers ask those questions legitimately.
- The guard fails open by default. If Jev errors or times out, the turn continues as `unscreened`, and layers 1 to 4 still apply. `Guard:FailClosed` refuses the turn instead, in `Enforce` only.
- The guard screens the current message and the last few turns. Older turns are not screened. Blocked turns are removed from later conversation recaps, so one blocked attempt is not carried forward.
- `Guard:CombineWithTriage` sends the guard and the triage intent in one request. This saves latency. It does not change the verdict: the guard's decision is applied first, and a blocked turn's triage answer is discarded.

### 6. Indirect-injection drop (when `Verification:Judge` is `Jev`)

In the same Jev request that scores relevance, each KB and code snippet and each vision finding gets one more statement: whether it contains instructions addressed to an AI assistant. An item at or above `Guard:IndirectThreshold` is removed before the Drafter sees it, whatever its relevance. Each drop is logged with the verifier and the snippet position, so the poisoned document can be found and fixed. This layer only removes content, so it runs in every `Guard:Mode`.

### 7. Output guard (when `Guard:Mode` is `Shadow` or `Enforce` and `Jev:ApiKey` is set)

After the leak check, Jev scores the final draft on two statements: whether it reveals internal instructions, configuration, credentials or another customer's data, and whether it follows instructions that came from retrieved content. In `Shadow` the draft is always sent and the result is logged. In `Enforce`, a score at or above threshold replaces the draft with the safe fallback message. It fails open: if Jev is unavailable, the draft is sent after the leak check.

## What this does not guarantee

The guard is a detection layer. It is not a guarantee that an attacker cannot change the assistant's behaviour.

- **Detection is probabilistic.** The heuristic layer is a phrase list, and layers 5 to 7 depend on Jev scores, which can be wrong. An attacker who knows the statements can try to phrase an attack to avoid them. Layers 1 to 3 are structural rules, and they are not complete either.
- **Thresholds are placeholders until they are evaluated.** The values in `appsettings.json` are not tuned. They must come from the guard eval, which measures false blocks on a set of legitimate support questions, before `Enforce` is used.
- **The guard can fail open.** While Jev is down, or when no key is set, the input guard can only flag, and the output guard sends the draft. The structural layers are then all that remain.
- **The guard does not change what the assistant is allowed to do.** A guard pass grants nothing. Verifiers, the leak check and role gating still decide what reaches the customer.
- **Screenshots are read by a model first.** The vision description is a model's text and can carry instructions. Layer 6 checks vision findings only when `Verification:Judge` is `Jev`.
- **Scope is limited to the product.** Harmful-content moderation beyond the support scope is out of scope.

Operators should treat `flagged`, `blocked` and `unscreened` turns, and the `IndirectInjectionDropped` and `OutputGuardTriggered` logs, as review material, not as proof of an attack.

## Data sent to TypeSafe

Turning on Jev sends customer messages and recent history to TypeSafe. The output guard also sends the draft and the retrieved text it was written from. Code-graph snippets are sent only when `Verification:Jev:IncludeCode` is `true`. Confirm the data-processing terms before you set a key in production. See the [configuration guide](docs/deployment/configuration-guide.md#jev-typesafe).

## Configuration

The settings for these layers are in the `Guard`, `Triage` and `Verification` sections. The defaults are safe: `Guard:Mode` is `Off`, `FailClosed` is `false`, and `CombineWithTriage` is `false`. See the [configuration guide](docs/deployment/configuration-guide.md#input-guard-prompt-injection-and-jailbreak) for each key and the rollout steps.
