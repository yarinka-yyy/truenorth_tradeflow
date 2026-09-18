# Shared market ticket procedure

Read this reference after either completed setup route. It governs one current market-opening attempt only.

## Interpret the current user amount

When the current user supplies an amount with leverage — for example, `10 USDC at 3x` — treat `10 USDC` as their target rendered **Margin Required** (their capital), not as the ticket's visible Size or Order Value. Set the requested leverage, then adjust only the visible **Size** control until the rendered Margin Required equals the target. Do not calculate a notional or infer a nearby Size from leverage. The user can explicitly state that an amount means ticket Size or Order Value instead.

If the exact requested target cannot be rendered, or fees and available balance make the ticket unavailable, show the current rendered values and ask the user for one new current choice. Do not submit or choose a fallback amount.

## Build the live ticket

Read the selected account's exact-market **Positions** row and **Open Orders** rows before preparing the ticket. If a position or opening order already exists for this market, stop: another opening could add to or reverse existing exposure, which this route does not cover. Other markets' rows do not block this opening. Save this baseline for the result check.

On the rendered TrueNorth ticket, explicitly set the selected market, Long or Short direction, **Market** order type, current leverage, and the user-requested margin target or explicitly requested Size/Order Value.

For an opening order, ensure **Reduce only** is off, **Skip Open Order Confirmation** is off, and **Attach an agent** is off. Set TP/SL only to exact levels supplied by the completed setup or current user choice, or leave them off only when that source explicitly says `none`. Do not use **Customize** or **Start watching**.

Compare any requested TP/SL with the current rendered market price before submission: Long requires TP above and SL below that price; Short requires TP below and SL above it. If a level has already been crossed, stop for a new current choice. Do not apply an arbitrary movement threshold to an otherwise valid Market entry.

Read the current ticket after setting it. Capture market, side, type, leverage, Size unit/value, Order Value, Margin Required, fees, maximum slippage, TP/SL, the three opening-control states, submit label, and any UI-labelled liquidation price or `Est` slippage.

## Get one opening approval

Show the rendered-ticket card from `templates/order-card.md`. The user approves the current opening intent once: market, side, Market type, requested leverage and margin target (or explicitly requested Size/Order Value), TP/SL, and opening-control states. That approval also covers a same-intent rendered final confirmation.

Immediately before the outer click, re-read the core intent and current market price. If market, side, type, requested leverage or amount target, protection, opening-control state, or displayed maximum slippage cannot match the approval, pause for a new current choice. If the editable ticket's Margin Required drifted from the requested target, adjust Size until it matches again; stop if it cannot. Check TP/SL validity again. Do not request another approval merely because an otherwise valid Market price, rounded base size, fees, estimated slippage, liquidation, or submit wording changed.

Click the current rendered ticket-submit control once.

## Handle a rendered platform confirmation

If the ticket click opens a rendered TrueNorth confirmation rather than an accepted result:

1. Re-read its exchange, action, token size, `Est. execution`, Order Value, Margin Required, fees, slippage including its maximum, protections, and final button label.
2. Continue under the existing approval only when it still represents the approved market, side, Market opening, leverage, submitted amount intent, protection, opening-control state, and maximum slippage. The final window may show converted token size without the original USDC Size input; do not require an unrendered field. A changed quote, rounded base size, calculated Order Value or Margin Required, fee, or estimated execution alone does not change intent. The final `Est. execution` is an estimate, not the live quote or a verified fill; do not infer an undocumented meaning from it. Stop if the final values show a changed input, the order became unavailable, or the final layer is unclear.
3. If the final layer conflicts with that core intent, is off-origin, is unclear, or the previous attempt failed and needs a new amount or other user choice, stop and ask the user. Do not retry an uncertain click.
4. Otherwise click the rendered final control once without a second confirmation prompt.

If a wallet, signature, password, permission, or 2FA prompt appears at any time, stop for the user to complete it.

## Read back and report

Read the selected account's exact-market **Positions** row and **Open Orders** rows and compare them with the saved baseline. A `Current Position` string inside a working ticket is contextual only; it cannot establish that a position exists or is flat by itself. The live quote, confirmation estimate, and filled position's rendered entry price are different observations; report the fill price only from the rendered result.

- **Opened** — a new exact-market position row is rendered relative to the baseline.
- **Open order** — a new exact-market opening order is rendered and no new position is rendered.
- **Not executed** — neither new opening order nor new position is rendered after the completed flow.
- **Could not confirm** — ticket, confirmation, session, or read-back became unclear.

Localize the result with `templates/order-card.md`. Never claim a trade succeeded from a click, toast, confirmation screen, or wallet prompt alone.
