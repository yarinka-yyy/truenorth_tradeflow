# Shared limit-entry ticket procedure

Read this reference after the completed limit-proposal route. It governs one current **limit-opening** attempt only. It does not support limit closes, order edits, resizing, reversing, cancellation, or watcher controls.

## Interpret the current user amount

When the current user supplies an amount with leverage — for example, `10 USDC at 3x` — treat `10 USDC` as the target rendered **Margin Required**, not as the visible Size or Order Value. Set the requested leverage, then adjust only the visible **Size** control until the rendered Margin Required equals the target. Do not calculate a notional or infer a nearby Size from leverage. The user can explicitly state that an amount means ticket Size or Order Value instead.

If the exact target cannot be rendered, or fees and available balance make the ticket unavailable, show the current values and ask for one new current choice. Do not submit or choose a fallback amount.

## Build the live limit ticket

On the rendered TrueNorth ticket, explicitly set and verify:

- the selected market;
- **Limit** order type;
- the provider direction from the completed setup, or the user's current direction;
- the current user leverage;
- the exact limit price from the completed setup or current user choice;
- the visible Size control until the requested target is rendered as **Margin Required**, or the explicitly requested Size/Order Value;
- **GTC** time in force for this supported resting-order route;
- **Reduce only** off;
- **Take Profit / Stop Loss** on with exact levels from the completed setup or current user choice;
- **Skip Open Order Confirmation** off; and
- **Attach an agent** off.

If GTC, the requested limit price, the requested amount target, or exact protection cannot be rendered, stop and ask for one new current choice. Do not use **Customize**, **Start watching**, or an attached agent.

Read the current ticket after setting it. Capture market, direction, Limit type, limit price, leverage, TIF, Size unit/value, Order Value, Margin Required, fees, TP/SL and their displayed gain/loss, liquidation estimate, the three opening-control states, and the submit label. Capture exact-market rendered **Positions** and **Open Orders** as the baseline before the first submit click.

## Get one opening approval

Show the limit-entry approval card from `templates/order-card.md`. The user approves one current opening intent: market, direction, **Limit** type and price, leverage and target rendered Margin Required (or explicitly requested Size/Order Value), GTC, TP/SL, and opening-control states. That approval also covers a same-intent rendered final confirmation.

Immediately before the outer click, re-read the core intent. If market, direction, order type, limit price, requested leverage or target, protection, TIF, or opening-control state cannot match the approval, pause and ask for a new current choice. Do not request another approval merely because price, rounded base size, calculated Order Value or Margin Required, fees, liquidation, or final labels refresh while the core intent remains the same.

Click the current rendered limit-submit control once.

## Handle a rendered platform confirmation

If the ticket click opens a rendered TrueNorth confirmation rather than an accepted result:

1. Re-read its exchange, action, direction, token size, estimated execution or limit price, Order Value, Margin Required, liquidation estimate, fees, Reduce only state, TP, and SL.
2. Continue under the existing one approval only when it still represents the same market, direction, Limit opening, price, amount target, GTC intent, and protection.
3. If the final layer conflicts with that core intent, is off-origin, is unclear, or the previous attempt failed and needs a new user choice, stop and ask the user. Do not retry an uncertain click.
4. Otherwise click the rendered final control once without a second confirmation prompt.

If a wallet, signature, password, permission, recovery, or 2FA prompt appears at any time, stop browser interaction and let the user handle it.

## Read back and classify

After the final attempt, read rendered **Positions** and **Open Orders** on the exact market. A button click, toast, confirmation screen, available-balance change, or ticket text is not execution evidence.

- **Open order** — the approved resting limit entry is rendered in Open Orders and no filled position is rendered. With TP/SL enabled, expect separate rendered protective close orders only when the UI actually shows them; report their exact trigger conditions.
- **Opened** — a current position is rendered. Read Open Orders as well and report any protective orders exactly as rendered.
- **Not executed** — neither the approved order nor a position is rendered after the completed flow.
- **Could not confirm** — the ticket, confirmation, session, or read-back became unclear.

A resting limit order is not a position and cannot be closed through the full Market-close route. Do not cancel or amend it automatically. Start the separate full-close route only after a current position row and its rendered **Close → Market** control have been verified, with a distinct close approval.

Never claim a limit order was filled from a confirmation screen alone. Never assume TP/SL orders were cancelled or attached unless Open Orders shows them.
