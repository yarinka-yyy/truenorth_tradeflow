# Shared market ticket procedure

Read this reference after either completed setup route. It governs one current market-opening attempt only.

## Interpret the current user amount

When the current user supplies an amount with leverage — for example, `10 USDC at 3x` — treat `10 USDC` as their target rendered **Margin Required** (their capital), not as the ticket's visible Size or Order Value. Set the requested leverage, then adjust only the visible **Size** control until the rendered Margin Required equals the target. Do not calculate a notional or infer a nearby Size from leverage. The user can explicitly state that an amount means ticket Size or Order Value instead.

If the exact requested target cannot be rendered, or fees and available balance make the ticket unavailable, show the current rendered values and ask the user for one new current choice. Do not submit or choose a fallback amount.

## Build the live ticket

On the rendered TrueNorth ticket, explicitly set the selected market, Long or Short direction, **Market** order type, current leverage, and the user-requested margin target or explicitly requested Size/Order Value.

For an opening order, ensure **Reduce only** is off, **Skip Open Order Confirmation** is off, and **Attach an agent** is off. Set TP/SL only to exact levels supplied by the completed setup or current user choice, or leave them off only when that source explicitly says `none`. Do not use **Customize** or **Start watching**.

Read the current ticket after setting it. Capture market, side, type, leverage, Size unit/value, Order Value, Margin Required, fees, maximum slippage, TP/SL, the three opening-control states, submit label, and any UI-labelled liquidation price or `Est` slippage.

## Get one opening approval

Show the rendered-ticket card from `templates/order-card.md`. The user approves the current opening intent once: market, side, Market type, requested leverage and margin target (or explicitly requested Size/Order Value), TP/SL, and opening-control states. That approval also covers a same-intent rendered final confirmation.

Immediately before the outer click, re-read the core intent. If market, side, type, requested leverage or target, protection, or opening-control state cannot match the approval, pause and ask the user for a new current choice. Do not request another approval merely because price, base-size rounding, calculated Order Value or Margin Required, fees, slippage, liquidation, estimated execution, or submit wording changed while the core intent remains the same.

Click the current rendered ticket-submit control once.

## Handle a rendered platform confirmation

If the ticket click opens a rendered TrueNorth confirmation rather than an accepted result:

1. Re-read its action, token size, estimated execution, Order Value, Margin Required, fees, slippage, protections, and final button label.
2. Continue under the existing one approval only when it still represents the same market, side, opening intent, protection, and opening-control state. Price, converted base size, calculated values, fees, and slippage can refresh without another approval.
3. If the final layer conflicts with that core intent, is off-origin, is unclear, or the previous attempt failed and needs a new amount or other user choice, stop and ask the user. Do not retry an uncertain click.
4. Otherwise click the rendered final control once without a second confirmation prompt.

If a wallet, signature, password, permission, or 2FA prompt appears at any time, stop for the user to complete it.

## Read back and report

Read rendered **Positions** (tab count and current row) alongside **Open Orders** on the current TrueNorth page. A `Current Position` string inside a working ticket is contextual only; it cannot establish that a position exists or is flat by itself. If the ticket string conflicts with the rendered position state, report the discrepancy and do not make a further order action from it.

- **Opened** — a current position is rendered.
- **Open order** — a current resting order is rendered.
- **Not executed** — neither is rendered after the completed flow.
- **Could not confirm** — ticket, confirmation, session, or read-back became unclear.

Localize the result with `templates/order-card.md`. Never claim a trade succeeded from a click, toast, confirmation screen, or wallet prompt alone.
