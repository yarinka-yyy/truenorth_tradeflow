# TrueNorth TradeFlow

An unofficial [Hermes Agent](https://hermes-agent.nousresearch.com/) skill for researching a TrueNorth setup and, in a deliberately small `live_mvp` mode, attempting one user-confirmed order through the already connected TrueNorth interface.

> This is browser automation, not financial advice. It does not predict profit or remove the user's final decision.

## What it does

1. Distinguishes `research_only`, `market_now`, `limit_level`, and explicit `functional_test` requests.
2. Uses one analysis-only TrueNorth request for a research, market-now, or limit-level intent; a separately explicit `functional_test` tests the order lifecycle without presenting stale or absent analysis data as a trade signal.
3. Returns a review card with the setup and a separate live-ticket snapshot.
4. In `live_mvp`, fills one TrueNorth ticket, shows its displayed order value, margin, fees, slippage, and protection settings, then waits for an exact final confirmation.
5. Clicks one current ticket submit control and, only if TrueNorth then renders a separate order-confirmation screen, obtains a new exact confirmation before its one final action click. It reads back Open Orders or Positions before reporting the outcome.

## MVP boundary

`live_mvp` is intentionally narrow:

- It opens at most one current, user-confirmed order attempt.
- It stays on the exact browser origin `https://truenorth.xyz`; it never opens a direct Hyperliquid page or API.
- It never handles seed phrases, private keys, passwords, signatures, permissions, 2FA, or wallet approval. The user performs any wallet action themselves.
- It does not retry an uncertain submission.
- It does **not** yet close, resize, reverse, average, or cancel a position. Those controls will be added only after a live pilot position exposes and validates the real close UI.

## Install

```bash
hermes skills install yarinka-yyy/truenorth_tradeflow/skills/truenorth-tradeflow
```

The installer scans the skill. Do not use `--force` as a routine install path.

## Configuration

The default remains `review_only`. Set `execution_mode` to `live_mvp` only for one present, user-confirmed ticket. `allowed_tokens`, maximum leverage, maximum margin, allowed entry modes, and setup lifetime are local preferences stored in the user's Hermes profile, not in this repository.

A configured token/mode allowlist or positive leverage/margin limit is a hard execution check. The matching live-ticket field must be exact and, where numeric, a single value within the limit. Leave an optional limit unset (`0`) or an allowlist blank if the user wants to decide from the current ticket snapshot instead. The ticket's displayed **Order Value**, **Margin Required**, fees, and slippage are always shown immediately before submission.

## Use

```text
/truenorth-tradeflow Analyze $ETH.
/truenorth-tradeflow Find a market-now setup for $ETH.
/truenorth-tradeflow Find a limit-level setup for $ETH.
/truenorth-tradeflow Run a functional market-order lifecycle test for $ETH.
```

For an ambiguous request such as “Open ETH,” Hermes asks whether the intent is **market now** or **limit level**, unless the user explicitly delegates that choice in the current request. `functional_test` is available only when the user explicitly asks to test the lifecycle; it is market-only and does not turn an AI response into a trading recommendation. A `NO_TRADE_NOW` or `NO_LIMIT_SETUP` result ends that strategy attempt; it never silently substitutes the other mode.

## Live MVP sequence

1. For a research or strategy intent, ask TrueNorth once for a fresh analysis-only setup. For an explicit `functional_test`, record the current user request instead.
2. Show the strategy setup card or functional-test request card and, if the user chooses `live_mvp`, fill the current ticket explicitly.
3. Show the ticket snapshot: market, side, type, leverage, size unit/value, displayed order value, displayed margin, TP/SL, fee, slippage, current submit-button label, and the exact-market Open Orders/Positions baseline.
4. Require the user to confirm that exact snapshot.
5. Click only that current rendered ticket-submit control once if its immediate re-read matches the confirmed snapshot exactly. If it opens a TrueNorth order-confirmation screen rather than executing, read that screen as a new snapshot. Any difference in its action, token size, estimated execution, value, margin, liquidation, slippage, protections, fees, or final button label cancels the earlier authorization. Show the confirmation snapshot and obtain a new exact approval, then immediately re-read every bound confirmation field before one final action click; any difference cancels that approval. The user handles any external wallet prompt.
6. Read back Open Orders or Positions and compare them with the exact-market baseline. A ticket click, confirmation screen, toast, wallet prompt, or review panel alone is not proof of execution.

### Functional-test variant

An explicitly user-requested `functional_test` may go straight to a current **Market** ticket only in `live_mvp`, without relying on an analysis setup. It is a lifecycle test, not a trading recommendation: stale, missing, or weak AI data is never described as a valid signal. The ticket, and any rendered platform confirmation, remain fully snapshot-bound and separately approved. Without that screen, compare the mandatory post-click read-back to the exact-market baseline: no change is `NOT_EXECUTED`; any new or changed exact-market order or position is `FAILED`, reported, and never retried. TP/SL stays off unless the user separately asks for exact levels, so a verified position can expose its actual close controls for later mapping.

## Browser boundary

The skill begins at `https://truenorth.xyz/` and permits only paths whose parsed origin remains exactly `https://truenorth.xyz`. It stops on every off-origin redirect, popup, or link, including wallet and direct Hyperliquid routes.

This is an MVP workflow rule, not a technical browser sandbox.

## Status

`0.2.2` — adds an explicit user-directed `functional_test` path for a market-order lifecycle check. It is not a strategy mode, does not rely on stale analysis as a signal, and retains the ticket approval plus any rendered platform-confirmation approval. The observed confirmation-layer rule remains unchanged.

## License

MIT. This project is independent and is not affiliated with TrueNorth or Hyperliquid.
