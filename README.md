# TrueNorth TradeFlow

An unofficial, review-first [Hermes Agent](https://hermes-agent.nousresearch.com/) skill for turning a TrueNorth trade analysis into a structured review flow inside the already connected TrueNorth interface.

> TrueNorth TradeFlow automates repetitive browser work. It is not financial advice, does not guarantee outcomes, and never removes the user's final decision.

## What it does

1. Opens TrueNorth and asks its agent for a structured setup.
2. Returns a compact review card in Hermes: side, entry, stop-loss, take-profit, invalidation, and source rationale.
3. Captures the complete response and inline setup-card expiry for a review card.
4. Keeps the result review-only in the current release; it does not click a TrueNorth order control.
5. Leaves future native execution gated on an independently mapped, verified UI flow.

## What it never does

- Does not navigate directly to Hyperliquid or use a Hyperliquid API.
- Opens only the exact TrueNorth origin, `https://truenorth.xyz`, during its browser workflow.
- Does not handle seed phrases, private keys, wallet passwords, API keys, signatures, or 2FA.
- Does not approve a wallet, browser permission, payment, or signing dialog.
- Does not execute a trade from a routine, alert, stale approval, or ambiguous message.
- Does not open, close, resize, average, reverse, or cancel a position outside the explicitly approved flow.

## Install

```bash
hermes skills install yarinka-yyy/truenorth_tradeflow/skills/truenorth-tradeflow
```

The installer scans the skill before installing it. Do not bypass a security warning with `--force` unless you have inspected and accepted the exact finding.

## First-time configuration

The skill starts in `review_only` mode. These local limits are reserved for a future live release: set allowed tokens, maximum leverage, maximum margin, allowed entry modes, and setup freshness window. They do **not** unlock order execution in `0.1.3`; setting `execution_mode` to `live` is unsupported and has no effect. A future live release will require positive finite leverage and an exact positive margin in USDC; a notional-only setup cannot be opened. These settings live in the user's Hermes profile and are not part of this repository.

## Use

```text
/truenorth-tradeflow Find a swing setup for $ETH.
```

Hermes will show a review card. In `0.1.3`, choose **Keep review-only** or **Ask TrueNorth a follow-up**; it will not place an order. If TrueNorth or a wallet shows a password, signature, permission, or 2FA prompt, the user handles it directly.

## Prerequisites

- Hermes with browser automation available.
- An authenticated TrueNorth browser session.
- No connected trading account is needed for `0.1.3` review-only use.
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

`0.1.3` — authenticated TrueNorth analysis/review flow tested in-browser. The test confirmed that a full agent response and an inline setup card can be captured without creating an order. It also showed that the separate order-panel defaults can differ from the AI recommendation, so **Edit**, **One-Click Setup**, and **Place Order & Launch Agent** remain intentionally disabled in this release. Setting `execution_mode` to `live` does not change that. A future version may add a narrow local MCP guard only after those controls are independently mapped and verified with a matching order/position read-back.

## License

MIT. This project is independent and is not affiliated with TrueNorth or Hyperliquid.
