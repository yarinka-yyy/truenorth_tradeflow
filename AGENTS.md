# TrueNorth TradeFlow

## Project rules

- Keep this repository public, minimal, and installable as one Hermes skill.
- Commit and push every verified logical change to `origin/main`.
- Never add account data, addresses, screenshots, chat history, credentials, API keys, private keys, seed phrases, wallet passwords, signatures, or personal risk settings.
- Keep the product TrueNorth-only: do not add direct Hyperliquid browsing, APIs, or signing.
- Keep the browser origin exact: `https://truenorth.xyz` only; stop on off-origin redirects, popups, and links.
- Preserve the approval-first workflow. No routine, alert, prior approval, or model inference can authorize a trade.
- Never switch a user request between market-now and limit-level entry without a fresh explicit choice, unless the user explicitly delegates that choice in the current request.
- A `functional_test` is separate from a trading setup and exists only after the user explicitly asks to test the order lifecycle now and chooses `live_mvp`. It is market-only, makes no claim about signal quality, and requires an exact ticket approval plus a separate platform-confirmation approval only when that confirmation is rendered.
- A live MVP may click the current ticket submit control once only after a fresh setup or explicit `functional_test` and an exact, per-action user confirmation. Retain an exact-market Open Orders/Positions baseline before that click and compare the mandatory post-click read-back with it. If the click opens a rendered TrueNorth confirmation instead of submitting, capture that confirmation and require a separate exact user confirmation before its one final action click. Immediately re-read every confirmation-bound field before that final click; any difference cancels it. Never retry an uncertain submission.
- Do not add a backend, MCP server, database, package dependency, or build system unless the user explicitly requests a later version.
- Keep the direct install path valid: `owner/repo/skills/truenorth-tradeflow`.
