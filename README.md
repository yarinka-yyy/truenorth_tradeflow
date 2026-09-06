# TrueNorth TradeFlow

An unofficial, approval-first [Hermes Agent](https://hermes-agent.nousresearch.com/) skill for turning a TrueNorth trade analysis into a reviewed execution flow inside the already connected TrueNorth interface.

> TrueNorth TradeFlow automates repetitive browser work. It is not financial advice, does not guarantee outcomes, and never removes the user's final decision.

## What it does

1. Opens TrueNorth and asks its agent for a structured setup.
2. Returns a compact review card in Hermes: side, entry, stop-loss, take-profit, invalidation, and source rationale.
3. Waits for a fresh, explicit approval.
4. Returns to the same TrueNorth chat and uses its native trade path for the approved setup only.
5. Reads back the resulting order or position before reporting success.

## What it never does

- Does not navigate directly to Hyperliquid or use a Hyperliquid API.
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

The skill starts in `review_only` mode. Before enabling live execution, configure personal limits locally:

```bash
hermes config migrate
```

Set the allowed tokens, maximum leverage, maximum margin, allowed entry modes, and setup freshness window. Live execution requires positive finite leverage and an exact positive margin in USDC; a notional-only setup is review-only and cannot be opened. These settings live in the user's Hermes profile and are not part of this repository.

## Use

```text
/truenorth-tradeflow Find a swing setup for $ETH.
```

Hermes will show a review card. Choose **Open this exact setup** only after reading it. If TrueNorth or a wallet shows a password, signature, permission, or 2FA prompt, the user handles it directly.

## Prerequisites

- Hermes with browser automation available.
- An authenticated TrueNorth browser session.
- A connected TrueNorth trading account if live execution is enabled.
- User approval for any Chrome remote-debugging or wallet prompt.

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

`0.1.1` — skill-first browser workflow with fail-closed live leverage, margin, and expiry requirements. A future version may add a narrow local MCP guard only after the live TrueNorth UI flow is independently mapped and tested.

## License

MIT. This project is independent and is not affiliated with TrueNorth or Hyperliquid.
