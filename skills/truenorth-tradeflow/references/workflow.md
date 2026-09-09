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
  -> SUBMIT_REQUESTED
  -> ORDER_OR_POSITION_READ_BACK
  -> COMPLETE
```

`review_only` transitions from `AWAITING_LIVE_MODE` directly to `COMPLETE`.

Any state may end in:

```text
CANCELLED | EXPIRED | NOT_EXECUTED | FAILED
```

## Transition rules

- `SAME_ORIGIN_VERIFIED` requires the exact parsed origin `https://truenorth.xyz`. Any off-origin redirect, popup, or link becomes `NOT_EXECUTED`.
- `RESPONSE_COMPLETE` requires the analysis stream to finish. A heading, badge, or button alone is not complete. Any conflict between completed response text and its inline setup card becomes `NOT_EXECUTED`; do not choose a value by inference.
- A direct trade request without `market_now` or `limit_level` remains unresolved until the user selects one or explicitly delegates that choice in the current request. Never substitute a mode after `NO_TRADE_NOW` or `NO_LIMIT_SETUP`.
- `TICKET_CONFIGURED` requires explicit market, side, order type, leverage, and Size unit/value. Do not inherit panel defaults.
- `TICKET_READ_BACK` requires the current rendered Order Value, Margin Required, fees, slippage, **Reduce only**, **Take Profit / Stop Loss**, **Skip Open Order Confirmation**, and **Attach an agent** state. The ticket display, not a chat explanation, is the source for these fields. A configured token/mode allowlist must contain the exact ticket value, and a positive configured leverage or margin cap requires a single numeric matching ticket field within that cap; otherwise become `NOT_EXECUTED`.
- `AWAITING_EXACT_CONFIRMATION` shows the setup and complete ticket snapshot together. The confirmation binds only to its market, side, order type, leverage, Size unit/value, Order Value, Margin Required, fees, slippage, all four checkbox states, TP/SL values, current submit label, and setup expiry.
- `SUBMIT_REQUESTED` re-reads every confirmation-bound field immediately before clicking the current rendered TrueNorth submit control once. Any difference cancels the authorization, returns to `TICKET_READ_BACK`, and requires a new snapshot and exact confirmation. Wallet, signature, permission, password, and 2FA prompts are user actions; their appearance ends Hermes interaction.
- `ORDER_OR_POSITION_READ_BACK` requires a new matching Open Order or Position. A click, toast, wallet prompt, or review screen is not sufficient.
- Missing read-back, disconnect, stale setup, selector mismatch, or an uncertain result is `NOT_EXECUTED`. Never retry automatically.
- The observed **Customize** control opened a watcher configuration in one audit and remains outside the order path. **Start watching** is never clicked.
- V0.2.0 has no position-management transition. Closing, resizing, reversing, averaging, and cancelling remain out of scope until a live pilot exposes their actual controls.
