# TrueNorth TradeFlow

One Hermes browser-only skill for proposing and opening one current **market** order through the TrueNorth UI after the user approves the exact rendered ticket. It supports an explicitly requested real lifecycle test, where strategy quality does not block testing the market-opening path.

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

For either request, the skill sends one matching current English analysis request to TrueNorth, returns a short source-attributed proposal in the user's language, renders the current ticket, and waits for the user's exact approval before every financial click. It then reads **Open Orders** and **Current Position** before reporting the result.

The user chooses current size, leverage, and protection values. Those choices override a provider recommendation, are never stored, and are never converted with an assumed formula. In particular, when the user specifies a target rendered `Margin Required`, the skill adjusts only the visible Size control and pauses if the exact target cannot be rendered.

A normal `NO_MARKET_SETUP` ends that normal attempt. A user-requested lifecycle test instead uses `MARKET_TEST_SETUP`, so ordinary timing, confidence, or quality filters do not end the test. It is not trading advice and still requires the exact rendered-ticket confirmation.

## Current scope

- **Supported now:** one user-confirmed market opening through `https://truenorth.xyz`, including a user-requested lifecycle-test opening.
- **Not supported yet:** closing a position, resizing, reversing, cancelling, or opening a limit order. Close support will be added only from controls actually observed on a real current position.
- **Never automated:** wallet connection, signatures, passwords, permissions, and 2FA. The user handles those prompts directly.
- **Never used:** direct Hyperliquid routes, APIs, backend automation, watcher controls, or attached agents.

## Files

- `skills/truenorth-tradeflow/SKILL.md` — compact router and universal safety constraints.
- `skills/truenorth-tradeflow/references/` — independent normal-proposal, lifecycle-test, and shared-ticket procedures.
- `skills/truenorth-tradeflow/templates/order-card.md` — localized proposal, confirmation, and result format.

## Status

`0.4.1` — Hermes-only packaging and progressively disclosed market workflow. It intentionally favors one verified opening path over unobserved position-management or limit features.

## License

MIT. This project is independent and is not affiliated with TrueNorth or Hyperliquid.
