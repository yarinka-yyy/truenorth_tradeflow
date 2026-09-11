# TrueNorth TradeFlow

One Hermes browser-only skill for proposing and opening one current **market** order through the TrueNorth UI after one current user approval. From a verified current position, it can also use the observed **Close → Market** control for a user-approved full-close attempt.

## Install in Hermes

```bash
hermes skills install yarinka-yyy/truenorth_tradeflow/skills/truenorth-tradeflow
```

Start a new Hermes session after installation.

## What it does

A normal request can be as short as:

> Open ETH at market.

For an explicit lifecycle test, the user can say:

> Run a real ETH market lifecycle test. The setup does not need to be a good trade.

For either opening request, the skill sends one matching current English analysis request to TrueNorth, returns a short source-attributed proposal in the user's language, renders the current ticket, and obtains one opening approval. That approval covers the same-intent rendered platform confirmation when one appears; it does not create a series of approval prompts from normal repricing. The skill then reads **Open Orders** and **Current Position** before reporting the result.

When a user gives an amount with leverage — for example, `10 USDC at 3x` — the amount means capital to use: target rendered **Margin Required = 10 USDC**. The visible Size control is adjusted only until the ticket shows that target; the skill never assumes a conversion. The rendered Order Value will usually be about `30 USDC`, but the UI is authoritative. A user can explicitly request ticket Size or Order Value instead.

For a verified current position whose row visibly renders **Close → Market**, the skill may show one full-close card. One user approval covers that close control and a same-intent on-origin final confirmation. It always reads back the resulting positions and open orders; it never assumes that TP/SL orders were cancelled.

A normal `NO_MARKET_SETUP` ends that normal attempt. A user-requested lifecycle test instead uses `MARKET_TEST_SETUP`, so ordinary timing, confidence, or quality filters do not end the test. It is not trading advice and still requires the one current opening approval.

## Current scope

- **Supported now:** one user-approved market opening, plus a user-approved full Market close only from a verified current position whose rendered row exposes **Close → Market**.
- **Not supported yet:** limit orders, limit closes, resizing, reversing, cancelling, margin adjustment, TP/SL editing, or watcher controls.
- **Never automated:** wallet connection, signatures, passwords, permissions, and 2FA. The user handles those prompts directly.
- **Never used:** direct Hyperliquid routes, APIs, backend automation, watcher controls, or attached agents.

## Files

- `skills/truenorth-tradeflow/SKILL.md` — compact router and universal safety constraints.
- `skills/truenorth-tradeflow/references/` — normal-proposal, lifecycle-test, market-ticket, and observed market-close procedures.
- `skills/truenorth-tradeflow/templates/order-card.md` — localized proposal, approval, and result format.

## Status

`0.5.0` — Hermes-only market workflow with one approval per opening or full-close action, explicit margin-before-leverage amount semantics, and an observed-position Market-close route.

## License

MIT. This project is independent and is not affiliated with TrueNorth or Hyperliquid.
