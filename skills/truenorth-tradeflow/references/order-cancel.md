# Verified full-set open-order cancellation

Read this reference only when the user explicitly asks to cancel current resting orders. This route uses the rendered **Open Orders → Cancel All** control only when its entire possible scope is readable and approved. The observed Open Orders filter offers **All**, **Active**, **Long**, and **Short**, with no market filter; do not describe `Cancel All` as exact-market scoped. It does not close a position or support single-row cancellation, edits, resizing, or reversing.

## Establish the complete scope

Verify the exact origin `https://app.truenorth.xyz` and selected trading account. Choose the rendered **All** filter, then read the complete Open Orders count and every row, including market, type, direction, size, and trigger condition. Read **Positions** separately. Continue only if the rows are fully visible, the count matches them, no pagination or hidden rows remain, and **Cancel All** is present in this same view. If the user named one market, every row in the complete set must belong to that market; otherwise stop and explain that this control may affect other markets. If no rows remain, report that there is nothing to cancel.

## Get one cancellation approval

Show the cancellation card from `templates/order-card.md` with every row in scope and explain that `Cancel All` may apply to the complete account Open Orders set. The user's approval covers that complete rendered set and one same-intent on-origin confirmation. Do not infer that an earlier trade approval authorizes cancellation.

Immediately before the click, re-read the same account, **All** filter, complete row set, count, Positions state, and `Cancel All` control. If any row or scope changed, a row is hidden, or the state is unclear, stop for a new current choice. Click **Cancel All** once. If an on-origin confirmation appears, re-read its scope and click once only if it still names the approved complete set. The user handles any wallet, signature, password, permission, recovery, or 2FA prompt. Do not retry an uncertain click.

## Read back and report

Read **Open Orders** in the **All** view and **Positions** after the click and compare the named rows with the baseline.

- **Cancelled** — every approved row is gone; report Positions separately because cancellation does not close a position.
- **Not cancelled** — an approved row remains after a completed flow; do not retry automatically.
- **Could not confirm** — the session, account, origin, or rendered scope became unclear.

Never claim that a position was closed because its protective or entry orders were cancelled. A current position requires the separate verified-position full Market-close route and its own approval.
