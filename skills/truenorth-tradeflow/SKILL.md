---
name: truenorth-tradeflow
description: Propose and open one user-confirmed TrueNorth market order through the browser UI; use for current market entries or explicit lifecycle tests, not limit orders, closing, or direct Hyperliquid actions.
version: 0.4.2
author: yarinka-yyy, Hermes Agent
license: MIT
platforms: [windows, macos, linux]
metadata:
  hermes:
    tags: [truenorth, trading, browser, market-order]
    category: crypto
    related_skills: []
    requires_toolsets: [browser]
---

# TrueNorth TradeFlow

Use this skill when the user wants a current market order through the authenticated TrueNorth UI. It turns one current TrueNorth request into a compact proposal, then opens the current rendered ticket after one user approval. A user can explicitly request a real market lifecycle test, where proving the UI path matters more than ordinary setup quality.

## When to use

- “Open ETH at market.”
- “Ask TrueNorth whether SOL has a current market setup.”
- “Run a real ETH market lifecycle test; it does not need to be a good trade.”
- “Show the current market ticket, then open after I confirm.”

Do not use this skill for direct Hyperliquid navigation, exchange APIs, wallet signing, background trading, position closing, or limit orders. Closing and limit paths are intentionally not claimed until their real controls have been observed.

## Common rules

1. Begin at `https://truenorth.xyz` and stop on every off-origin redirect, popup, or link.
2. Send one consolidated current TrueNorth request. A normal request uses the normal-proposal route; an explicit lifecycle test uses `MARKET_TEST_SETUP`, even when ordinary quality is weak. This choice and every user parameter apply only to the current request.
3. Keep market and limit distinct. If the user asks for a limit order, say that this market-first release does not support it; never silently substitute a market order.
4. Current user choices override a provider recommendation. An amount supplied with leverage means the user's target rendered **Margin Required**, unless the user explicitly calls it ticket Size or Order Value. Adjust only the visible Size control until the target is rendered; do not assume a leverage formula or choose a nearby amount. Do not store a cap, leverage, size, protection, test mode, or other personal policy.
5. Show a compact card in the user's language, never the full TrueNorth chat response. One current approval authorizes the current opening intent: market, side, Market type, requested leverage and margin target, protection, and opening-control states.
6. Re-read every current ticket and final-confirmation field for visibility. Normal price movement, base-size rounding, calculated Order Value or Margin Required, fees, slippage, liquidation, and estimated execution do not create another approval. Ask again only if the current user intent cannot be honored, such as a changed market, side, order type, requested leverage or margin target, protection, opening control, a failed submission that needs a new user choice, or an uncertain/off-origin state.
7. A separately rendered on-origin final confirmation with the same opening intent is covered by the one approval and is clicked once after re-read. The user alone handles wallet, signature, password, permission, and 2FA prompts.
8. After every submit click, read rendered **Open Orders** and **Current Position** before claiming an order or position exists.

## Routes

- **Normal market proposal:** read `references/market-proposal.md`, then `references/market-ticket.md`.
- **Explicit lifecycle test:** read `references/market-lifecycle-test.md`, then `references/market-ticket.md`.
- Use `templates/order-card.md` for every user-facing proposal, approval, and result.

Read only the route selected by the current request. Both routes use the shared ticket procedure.
