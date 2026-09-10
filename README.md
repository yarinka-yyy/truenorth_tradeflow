# TrueNorth TradeFlow

A small Hermes skill for opening one current **market** order through the TrueNorth browser UI after the user sees and confirms the exact ticket. It also supports an explicitly requested real interface/lifecycle test: the goal is to exercise the market path, not to wait for an ideal strategy signal.

## Install

```bash
hermes skills install yarinka-yyy/truenorth_tradeflow/skills/truenorth-tradeflow
```

Start a new Hermes session after installation.

## What it does

A normal request can be as short as:

> Open ETH at market.

For an explicit lifecycle test, the user can say:

> Run a real ETH market lifecycle test. The setup does not need to be a good trade.

The skill then:

1. identifies the current request as either a normal market proposal or an explicit lifecycle test; this is a per-request instruction, never stored configuration;
2. asks TrueNorth once for the matching current English setup request;
3. returns a short source-attributed proposal in the user's current language; this is not an order approval;
4. asks only for any missing current choice, such as Size or leverage;
5. fills and re-reads the rendered TrueNorth order ticket;
6. shows that exact rendered ticket and waits for the user's approval;
7. submits once through its current rendered control;
8. if TrueNorth shows a separate confirmation screen, asks for a separate confirmation of that screen;
9. reads **Open Orders** and **Current Position** before reporting the result.

For a normal proposal, a completed `NO_MARKET_SETUP` ends that normal attempt. For an explicitly requested lifecycle test, Hermes sends the distinct `MARKET_TEST_SETUP` request instead of treating strategy quality as an execution gate. A lifecycle test is not trading advice and still requires the exact rendered-ticket confirmation.

The user chooses any missing size, leverage, and protection values in the current conversation. The skill has no built-in amount cap, token allowlist, or persistent trading mode.

## Example of a localized rendered-ticket confirmation

The labels below are English only as documentation. Hermes writes the actual card in the language of the user's current conversation, and only after those values have been read from the current ticket.

```markdown
**Confirm the TrueNorth ticket**

- Market: `[rendered market]`
- Direction: `[rendered direction]`
- Entry: `Market`
- Leverage: `[rendered leverage]`
- Size: `[rendered Size unit and value]`
- Ticket: `Order Value [rendered value]`, `Margin Required [rendered value]`
- Protection: `[rendered SL/TP or none]`
- Why: `[one short TrueNorth reason]`

Confirm this exact market order?
```

The values are always read from the current rendered ticket. Hermes does not assume a formula or reuse an old setup.

## Current scope

- **Supported now:** one user-confirmed market opening through `https://truenorth.xyz`, including a user-requested lifecycle-test opening.
- **Not supported yet:** closing a position, resizing, reversing, cancelling, or opening a limit order. Those paths will be added only from controls actually observed during the first market lifecycle.
- **Never automated:** wallet connection, signatures, passwords, permissions, and 2FA. The user handles those prompts directly.
- **Never used:** direct Hyperliquid routes, APIs, backend automation, watcher controls, or attached agents.

## Files

- `skills/truenorth-tradeflow/SKILL.md` — entry point and common rules.
- `skills/truenorth-tradeflow/references/market-entry.md` — normal and lifecycle-test market workflow.
- `skills/truenorth-tradeflow/templates/order-card.md` — concise localized proposal, confirmation, and result format.

## Status

`0.3.1` — market-first MVP with an explicit lifecycle-test setup path. It intentionally favors one clear opening path over a large policy engine or unverified position-management features.

## License

MIT. This project is independent and is not affiliated with TrueNorth or Hyperliquid.