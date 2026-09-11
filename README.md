# TrueNorth TradeFlow

One Hermes browser-only skill for proposing and opening one current **Market or Limit** order through the TrueNorth UI after one current user approval. From a verified current position, it can also use the observed **Close → Market** control for a user-approved full-close attempt.

## Install in Hermes

```bash
hermes skills install yarinka-yyy/truenorth_tradeflow/skills/truenorth-tradeflow
```

Start a new Hermes session after installation.

## What it does

A normal Market request can be as short as:

> Open ETH at market.

For a current limit setup:

> Open an ETH limit entry from the level TrueNorth recommends.

For an explicit lifecycle test, the user can say:

> Run a real ETH market lifecycle test. The setup does not need to be a good trade.

For either opening request, the skill sends one matching current English analysis request to TrueNorth, returns a short source-attributed proposal in the user's language, renders the current ticket, and obtains one opening approval. That approval covers the same-intent rendered platform confirmation when one appears; it does not create a series of approval prompts from normal repricing. The skill then reads rendered **Positions** and **Open Orders** before reporting the result. A `Current Position` string inside an opening ticket is contextual only, not proof by itself.

For a Limit request, the provider response must supply an exact limit price and exact TP/SL values when protection is requested. The ticket explicitly uses **Limit**, the provider direction, the requested target rendered **Margin Required** (or explicitly requested Size/Order Value), **GTC**, **Reduce only off**, **Skip Open Order Confirmation off**, and **Attach an agent off**. A resting limit is reported as **Open order** until a rendered position proves that it filled.

When a user gives an amount with leverage — for example, `10 USDC at 3x` — the amount means capital to use: target rendered **Margin Required = 10 USDC**. The visible Size control is adjusted only until the ticket shows that target; the skill never assumes a conversion. The rendered Order Value will usually be about `30 USDC`, but the UI is authoritative. A user can explicitly request ticket Size or Order Value instead.

For a verified current position whose row visibly renders **Close → Market**, the skill may show one full-close card. One user approval covers that close control and a same-intent on-origin final confirmation. It always reads back the resulting position count/row and open orders; it never assumes that TP/SL orders were cancelled. A contextual ticket field cannot override that rendered read-back.

For the current exact-market Open Orders view, the skill can also use the observed **Cancel All** control when the user explicitly approves cancellation of the full rendered set. Cancellation is separate from closing a position: it reads Positions and Open Orders afterward and never treats cancelled orders as a closed position.

A normal `NO_MARKET_SETUP` or `NO_LIMIT_SETUP` ends that normal attempt. A user-requested lifecycle test instead uses `MARKET_TEST_SETUP`, so ordinary timing, confidence, or quality filters do not end the test. It is not trading advice and still requires the one current opening approval.

## Current scope

- **Supported now:** one user-approved Market or Limit opening, including exact TP/SL attached at entry; one user-approved full Market close from a verified current position whose rendered row exposes **Close → Market**; and one user-approved batch cancellation through the observed exact-market **Cancel All** control.
- **Not supported yet:** limit closes, resizing, reversing, partial cancellation, order editing, margin adjustment, TP/SL editing, or watcher controls.
- **Never automated:** wallet connection, signatures, passwords, permissions, and 2FA. The user handles those prompts directly.
- **Never used:** direct Hyperliquid routes, APIs, backend automation, watcher controls, or attached agents.

## Files

- `skills/truenorth-tradeflow/SKILL.md` — compact router and universal safety constraints.
- `skills/truenorth-tradeflow/references/` — market/limit proposals and tickets, lifecycle-test, exact-market cancellation, and observed market-close procedures.
- `skills/truenorth-tradeflow/templates/order-card.md` — localized proposal, approval, and result format.

## Status

`0.7.0` — Hermes-only Market and Limit opening workflow with one approval per opening, full-close, or exact-market batch-cancellation action, explicit margin-before-leverage amount semantics, exact entry protection, and observed rendered-state read-backs. Position rows/counts and Open Orders are the state proof; a ticket's contextual position text is not.

## License

MIT. This project is independent and is not affiliated with TrueNorth or Hyperliquid.
