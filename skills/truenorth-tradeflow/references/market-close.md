# Verified-position full Market close

Read this reference only when the user explicitly asks to close and the current TrueNorth page renders a current position. It supports one full **Market** close attempt from the observed position-row **Close → Market** control; it is not a Limit-close, resize, reverse, cancellation, margin-adjustment, TP/SL-edit, or watcher workflow.

## Observe the current position

Verify the exact origin `https://truenorth.xyz`. In the rendered **Positions** view, confirm all of the following before any close action:

1. the requested market has a current position row;
2. the row shows its current side and size;
3. the row visibly exposes **Close** with a **Market** control; and
4. current **Open Orders** and **Current Position** are read as the close baseline.

Do not use **Limit**, **Start Watcher**, margin adjustment, TP/SL editing, or a new opening ticket. If any required current control is absent or unclear, report that the full Market-close route is unavailable; do not infer a substitute.

## Get one full-close approval

Show the full-close card from `templates/order-card.md`. The user approves one current full-close intent: the current rendered market and position, the rendered **Close → Market** control, and a full reduction of that position.

Immediately before the click, re-read that the same current position row, market, side, and full position size from the approved card remain rendered, and that the same **Close → Market** control is present. If the position disappeared, changed market, side, or full size, or the close control changed, show the current state and obtain one replacement full-close approval before acting. Do not ask again for normal price, PNL, converted-size, fee, slippage, liquidation, or estimated-execution refreshes.

Click the row's rendered **Market** control in the **Close** column once.

## Handle a rendered close confirmation

If the close control opens an on-origin rendered confirmation:

1. re-read its action, market, size, reduce/full-close intent, estimated execution, fees, slippage, protections, and final label;
2. continue under the existing approval only when it still represents a full close of the approved current position; and
3. click the rendered final control once without a second approval prompt.

If the rendered action is not a full close of that position, the state is unclear, the page is off-origin, or a protected wallet/signature/password/permission/2FA prompt appears, stop. The user handles protected prompts.

## Read back and report

Read the current **Positions**, **Open Orders**, and **Current Position** after the attempt.

- **Closed** — the position is no longer rendered and current position is flat.
- **Not closed** — the position remains; report its current rendered state without retrying an uncertain click.
- **Could not confirm** — the confirmation, session, or read-back became unclear.

Report remaining open orders exactly as rendered. Do not assume that opening TP/SL orders were cancelled, and do not cancel them through an unobserved route.
