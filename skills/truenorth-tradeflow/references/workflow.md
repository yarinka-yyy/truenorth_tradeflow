# Workflow state machine

```text
IDLE
  -> ENTRY_INTENT_RESOLVED
  -> SAME_ORIGIN_VERIFIED
  -> ANALYSIS_REQUESTED
  -> RESPONSE_COMPLETE
  -> SETUP_CAPTURED
  -> AWAITING_LIVE_MODE
  -> TICKET_CONFIGURED
  -> TICKET_READ_BACK
  -> AWAITING_EXACT_CONFIRMATION
  -> TICKET_SUBMIT_REQUESTED
  -> PLATFORM_CONFIRMATION_READ_BACK (only if rendered)
  -> AWAITING_PLATFORM_CONFIRMATION
  -> FINAL_SUBMIT_REQUESTED
  -> ORDER_OR_POSITION_READ_BACK
  -> COMPLETE
```

An explicit `functional_test` takes `ENTRY_INTENT_RESOLVED -> FUNCTIONAL_TEST_AUTHORIZED -> SAME_ORIGIN_VERIFIED -> AWAITING_LIVE_MODE -> TICKET_CONFIGURED`; it skips `ANALYSIS_REQUESTED`, is market-only, and reaches `TICKET_CONFIGURED` only when the user also chooses `live_mvp`.

If no platform confirmation is rendered, every mode goes from `TICKET_SUBMIT_REQUESTED` to `ORDER_OR_POSITION_READ_BACK`; it still does not prove execution. For `functional_test`, compare the read-back with the retained exact-market baseline: unchanged becomes `NOT_EXECUTED`; any new or changed exact-market order or position is `FAILED`, must be reported, and is never retried.

`review_only` transitions from `AWAITING_LIVE_MODE` directly to `COMPLETE`.

Any state may end in:

```text
CANCELLED | EXPIRED | NOT_EXECUTED | FAILED
```

## Transition rules

- `SAME_ORIGIN_VERIFIED` requires the exact parsed origin `https://truenorth.xyz`. Any off-origin redirect, popup, or link becomes `NOT_EXECUTED`.
- `RESPONSE_COMPLETE` requires the analysis stream to finish. A heading, badge, or button alone is not complete. Any conflict between completed response text and its inline setup card becomes `NOT_EXECUTED`; do not choose a value by inference.
- A direct trade request without `market_now` or `limit_level` remains unresolved until the user selects one or explicitly delegates that choice in the current request. Never substitute a mode after `NO_TRADE_NOW` or `NO_LIMIT_SETUP`. `FUNCTIONAL_TEST_AUTHORIZED` requires an explicit current user request to test the lifecycle; it is not a fallback and makes no strategy claim.
- `TICKET_CONFIGURED` requires explicit market, side, order type, leverage, and Size unit/value. Do not inherit panel defaults.
- `TICKET_READ_BACK` requires the current rendered Order Value, Margin Required, fees, slippage, **Reduce only**, **Take Profit / Stop Loss**, **Skip Open Order Confirmation**, and **Attach an agent** state, plus an exact-market Open Orders/Current Position baseline retained for later comparison. The ticket display, not a chat explanation, is the source for these fields. A configured token/mode allowlist must contain the exact ticket value, and a positive configured leverage or margin cap requires a single numeric matching ticket field within that cap; otherwise become `NOT_EXECUTED`.
- `AWAITING_EXACT_CONFIRMATION` shows the setup and complete ticket snapshot together. The confirmation binds only to its market, side, order type, leverage, Size unit/value, Order Value, Margin Required, fees, slippage, all four checkbox states, TP/SL values, current submit label, exact-market Open Orders/Current Position baseline, and setup expiry or functional-test request marker.
- `TICKET_SUBMIT_REQUESTED` re-reads every ticket-confirmation-bound field immediately before clicking the current rendered TrueNorth ticket-submit control once. Any difference cancels the authorization, returns to `TICKET_READ_BACK`, and requires a new snapshot and exact confirmation.
- If that ticket-submit control does not render a platform confirmation, transition from `TICKET_SUBMIT_REQUESTED` to `ORDER_OR_POSITION_READ_BACK`; do not infer execution from the click. For `functional_test`, an unchanged exact-market baseline after the read-back becomes `NOT_EXECUTED` and makes no final action click. Any new or changed exact-market order or position is `FAILED`, is reported, and is never retried.
- `PLATFORM_CONFIRMATION_READ_BACK` begins only if the ticket submit opens a rendered TrueNorth confirmation. Capture Exchange, Action, token size, estimated execution, Order Value, Margin Required, liquidation, slippage, Reduce only, TP/SL, fees, Skip Open Order Confirmation, and the final action label. Treat this as a new proposed action, not proof of execution.
- `AWAITING_PLATFORM_CONFIRMATION` binds a new exact user approval to every captured confirmation field. In a strategy mode, any conflict with the setup's direction, fill boundary/slippage condition, values, protections, or expiry is `NOT_EXECUTED`; cancel the rendered confirmation rather than choosing a value by inference. In `functional_test`, compare only with the ticket snapshot and do not infer a strategy field.
- `FINAL_SUBMIT_REQUESTED` immediately re-reads every platform-confirmation-bound field. Any difference cancels authorization, returns to `PLATFORM_CONFIRMATION_READ_BACK`, and requires a new snapshot and confirmation. Wallet, signature, permission, password, and 2FA prompts are user actions; their appearance ends Hermes interaction.
- `ORDER_OR_POSITION_READ_BACK` compares the exact-market result with the retained baseline. After a rendered platform final action, it requires a new matching Open Order or Position. After an unconfirmed `functional_test` ticket click, an unchanged baseline is `NOT_EXECUTED` and any new or changed exact-market order or position is `FAILED`. A click, toast, wallet prompt, or review screen is not sufficient.
- Missing read-back, disconnect, stale setup, selector mismatch, or an uncertain result is `NOT_EXECUTED`. Never retry automatically.
- The observed **Customize** control opened a watcher configuration in one audit and remains outside the order path. **Start watching** is never clicked.
- V0.2.2 has no position-management transition. Closing, resizing, reversing, averaging, and cancelling remain out of scope until a live pilot exposes their actual controls.
