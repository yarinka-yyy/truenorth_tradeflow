# Shared market ticket procedure

Read this reference after either completed setup route. It governs one current market-opening attempt only.

## Build the live ticket

On the rendered TrueNorth ticket, explicitly set the selected market, Long or Short direction, **Market** order type, current leverage, and Size in its currently displayed unit.

Current user choices override provider recommendations. When the user specifies a target rendered field such as `Margin Required`, adjust only the rendered **Size** control and re-read the ticket after each adjustment. Never calculate a notional from leverage, assume a conversion, or silently choose the nearest available amount.

If the exact target cannot be rendered, or fees and the available balance make the ticket unavailable, show the current rendered values and ask the user for one new current choice. Do not submit or choose a fallback amount.

For an opening order, ensure **Reduce only** is off, **Skip Open Order Confirmation** is off, and **Attach an agent** is off. Set TP/SL only to exact levels supplied by the completed setup or current user choice, or leave them off only when that source explicitly says `none`. Do not use **Customize** or **Start watching**.

Read the current ticket after setting it. Capture only the values needed for the decision: market, side, type, leverage, Size unit/value, Order Value, Margin Required, fees, maximum slippage, TP/SL, the three opening-control states, and the current submit label. Capture UI-labelled liquidation price or `Est` slippage separately as live estimates when shown.

## Get the exact ticket confirmation

Show the rendered-ticket card from `templates/order-card.md`. The user must approve the exact rendered values, not merely the earlier TrueNorth proposal or lifecycle setup.

Immediately before the click, re-read every bound field: market, side, type, leverage, Size unit/value, Order Value, Margin Required, fees, maximum slippage, TP/SL, **Reduce only**, **Skip Open Order Confirmation**, **Attach an agent**, and submit label. If any bound field changed, show the updated short card and obtain a new approval.

On a current screen that labels liquidation price or slippage as a live `Est` value, re-read and retain its latest display. A price-tick-only change to those estimates does not invalidate an otherwise unchanged approval. A change to a stated maximum slippage or any other bound field does.

Click the current rendered ticket-submit control once.

## Handle a rendered platform confirmation

If the ticket click opens a rendered TrueNorth confirmation rather than an accepted result:

1. Read its action, token size, estimated execution, Order Value, Margin Required, fees, maximum slippage, protections, and final button label; retain liquidation and `Est` slippage separately if shown.
2. Compare its material values with the approved ticket, show only changed or newly shown values, and get a separate exact user confirmation.
3. Immediately re-read every bound field. If one changed, cancel that approval and show a replacement card. A price-tick-only live estimate change does not invalidate an otherwise unchanged final approval.
4. After approval, click the rendered final control once.

If a wallet, signature, password, permission, or 2FA prompt appears at any time, stop for the user to complete it. Do not retry a click whose result is uncertain.

## Read back and report

Read **Open Orders** and **Current Position** on the current TrueNorth page.

- **Opened** — a current position is rendered.
- **Open order** — a current resting order is rendered.
- **Not executed** — neither is rendered after the completed flow.
- **Could not confirm** — ticket, confirmation, session, or read-back became unclear.

Localize the result with `templates/order-card.md`. Never claim a trade succeeded from a click, toast, confirmation screen, or wallet prompt alone.
