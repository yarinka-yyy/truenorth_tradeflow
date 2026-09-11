# Verified exact-market open-order cancellation

Read this reference only when the user explicitly asks to cancel the current resting setup. This is an order-cancellation route, not a position close. It supports one observed batch-cancellation control for the current exact-market Open Orders view; it does not support editing, resizing, reversing, or partial row cancellation.

## Establish the cancellation scope

Verify the exact origin `https://truenorth.xyz` and the selected market. Read rendered **Positions** and **Open Orders** together before any cancellation action. List the exact current order rows and their count in the user-facing card.

Use this route only when:

1. the current market is rendered;
2. the current Open Orders rows are readable;
3. the user has explicitly chosen to cancel the full currently rendered set; and
4. the rendered **Cancel All** control is present and belongs to that current Open Orders view.

Do not infer that a row's `Cancel` control has the same scope as `Cancel All`. Do not use this route for a single-row cancellation, edits, resizing, reversing, or a position close unless that separate rendered path is observed and documented.

## Get one cancellation approval

Show the cancellation card from `templates/order-card.md`. The approval must name the current market, Positions state, Open Orders count, the exact rows in scope, and the rendered `Cancel All` control. One approval covers one current batch-cancellation click and a same-intent rendered confirmation if the UI adds one.

Immediately before the click, re-read the same market, order count, rows, flat/position state, and `Cancel All` control. If the scope changed, the control disappeared, the page is off-origin, or the state is unclear, stop and ask for a new current choice.

Click the rendered **Cancel All** control once. If a wallet, signature, password, permission, recovery, or 2FA prompt appears, stop and let the user handle it.

## Read back and report

Read rendered **Open Orders** and **Positions** after the click. A toast or button response is not enough.

- **Cancelled** — Open Orders renders zero and the named rows are gone; report Positions separately because cancellation does not close a position.
- **Not cancelled** — any named order remains or the read-back is unclear; do not retry automatically.
- **Could not confirm** — the session, origin, or rendered state became unclear.

Never claim that a position was closed because its protective or entry orders were cancelled. A current position requires the separate verified-position full Market-close route and its own approval.
