# TrueNorth TradeFlow

A small Hermes skill for opening one current **market** order through the TrueNorth browser UI after the user sees and confirms the exact ticket.

## Install

```bash
hermes skills install yarinka-yyy/truenorth_tradeflow/skills/truenorth-tradeflow
```

Start a new Hermes session after installation.

## What it does

A normal request can be as short as:

> Open ETH at market.

The skill then:

1. asks TrueNorth once for a current market setup;
2. returns a short source-attributed proposal in the user's current language; this is not an order approval;
3. asks only for any missing current choice, such as Size or leverage;
4. fills and re-reads the rendered TrueNorth order ticket;
5. shows that exact rendered ticket and waits for the user's approval;
6. submits once through its current rendered control;
7. if TrueNorth shows a separate confirmation screen, asks for a separate confirmation of that screen;
8. reads **Open Orders** and **Current Position** before reporting the result.

The user chooses size, leverage, and protection values in the current conversation. The skill has no built-in amount cap, token allowlist, or persistent trading mode.

## Example of a localized rendered-ticket confirmation

The labels below are English only as documentation. Hermes writes the actual card in the language of the user's current conversation, and only after those values have been read from the current ticket.

```markdown
**Confirm the TrueNorth ticket**

- Market: `ETH-USDC`
- Direction: `Long`
- Entry: `Market`
- Leverage: `2x`
- Size: `10 USDC`
- Ticket: `Order Value …`, `Margin Required …`
- Protection: `SL …`, `TP …`
- Why: one short TrueNorth reason.

Confirm this exact market order?
```

The values in this example are placeholders. Hermes always reads the current rendered ticket instead of assuming a formula or reusing an old setup.

## Current scope

- **Supported now:** one user-confirmed market opening through `https://truenorth.xyz`.
- **Not supported yet:** closing a position, resizing, reversing, cancelling, or opening a limit order. Those paths will be added only from controls actually observed during the first market lifecycle.
- **Never automated:** wallet connection, signatures, passwords, permissions, and 2FA. The user handles those prompts directly.
- **Never used:** direct Hyperliquid routes, APIs, backend automation, watcher controls, or attached agents.

## Files

- `skills/truenorth-tradeflow/SKILL.md` — entry point and common rules.
- `skills/truenorth-tradeflow/references/market-entry.md` — one-request market workflow.
- `skills/truenorth-tradeflow/templates/order-card.md` — concise localized proposal, confirmation, and result format.

## Status

`0.3.0` — market-first MVP. It intentionally favors one clear opening path over a large policy engine or unverified position-management features.

## License

MIT. This project is independent and is not affiliated with TrueNorth or Hyperliquid.
