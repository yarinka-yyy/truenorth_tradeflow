# TrueNorth TradeFlow

An unofficial, review-first [Hermes Agent](https://hermes-agent.nousresearch.com/) skill for turning a TrueNorth trade analysis into a structured review flow inside the already connected TrueNorth interface.

> TrueNorth TradeFlow automates repetitive browser work. It is not financial advice, does not guarantee outcomes, and never removes the user's final decision.

## What it does

1. Distinguishes `research_only`, `market_now`, and `limit_level` requests.
2. Asks one question when a direct trade request does not state market-now or limit-level intent.
3. Opens TrueNorth and uses the matching analysis-only prompt.
4. Returns a compact review card with side, entry, stop-loss, take-profit, invalidation, expiry, and requested intent.
5. Keeps the result review-only in the current release; it does not click a TrueNorth order control.

## What it never does

- Does not navigate directly to Hyperliquid or use a Hyperliquid API.
- Opens only the exact TrueNorth origin, `https://truenorth.xyz`, during its browser workflow.
- Does not handle seed phrases, private keys, wallet passwords, API keys, signatures, or 2FA.
- Does not approve a wallet, browser permission, payment, or signing dialog.
- Does not execute a trade in version `0.1.5`; a future flow must never execute from a routine, alert, stale approval, or ambiguous message.
- Does not open, close, resize, average, reverse, or cancel a position in version `0.1.5`; a future flow must never do so outside an explicitly approved action.

## Install

```bash
hermes skills install yarinka-yyy/truenorth_tradeflow/skills/truenorth-tradeflow
```

The installer scans the skill before installing it. Do not bypass a security warning with `--force` unless you have inspected and accepted the exact finding.

## First-time configuration

The skill starts in `review_only` mode. These local limits are reserved for a future live release: set allowed tokens, maximum leverage, maximum margin, allowed entry modes, and setup freshness window. They do **not** unlock order execution in `0.1.5`; setting `execution_mode` to `live` is unsupported and has no effect. A future live release will require positive finite leverage and an exact positive margin in USDC; a notional-only setup cannot be opened. These settings live in the user's Hermes profile and are not part of this repository.

## Use

```text
/truenorth-tradeflow Analyze $ETH.
/truenorth-tradeflow Find a market-now setup for $ETH.
/truenorth-tradeflow Find a limit-level setup for $ETH.
```

For a direct but ambiguous request such as “Open ETH,” Hermes asks whether the intent is **market now** or **limit level**. It never chooses one silently. In `0.1.5`, choose **Keep review-only** or **Ask TrueNorth a follow-up**; it will not place an order. If TrueNorth or a wallet shows a password, signature, permission, or 2FA prompt, the user handles it directly.

## Prerequisites

- Hermes with browser automation available.
- An authenticated TrueNorth browser session.
- No connected trading account is needed for `0.1.5` review-only use.
- User approval for any Chrome remote-debugging or wallet prompt.

## Browser boundary

The skill starts at `https://truenorth.xyz/` and permits only paths whose parsed origin remains exactly `https://truenorth.xyz`. It stops on every off-origin redirect, popup, or link, including direct Hyperliquid and wallet routes.

This is a documented skill rule, not a technical browser sandbox. A future local guard must enforce it in code before live trading is supported.

## Repository layout

```text
skills/truenorth-tradeflow/
├── SKILL.md
├── references/
│   ├── policy-template.md
│   ├── prompt-template.md
│   └── workflow.md
└── templates/
    └── review-card.md
```

## Status

`0.1.5` — review-only intent routing verified with one `market_now` analysis and one `limit_level` analysis. The market-now path can correctly return no trade rather than inventing a limit substitute; the limit-level path can return an exact resting limit, expiry/cancel condition, and an inline setup card. The order panel still differs from the AI setup and remains off-limits. **Edit**, **One-Click Setup**, **Place Order & Launch Agent**, and **Skip Open Order Confirmation** remain intentionally disabled. Setting `execution_mode` to `live` does not change that.

## License

MIT. This project is independent and is not affiliated with TrueNorth or Hyperliquid.
