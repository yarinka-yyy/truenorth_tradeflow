# Workflow state machine

```text
IDLE
  -> ENTRY_INTENT_RESOLVED
  -> SAME_ORIGIN_VERIFIED
  -> ANALYSIS_REQUESTED
  -> RESPONSE_COMPLETE
  -> SETUP_CAPTURED
  -> VALIDATED
  -> AWAITING_USER_APPROVAL
  -> REVIEW_COMPLETE
```

Any state may end in:

```text
CANCELLED | EXPIRED | REJECTED_BY_POLICY | NOT_EXECUTED | FAILED
```

## Transition rules

- `SAME_ORIGIN_VERIFIED` requires the exact parsed origin `https://truenorth.xyz`; every off-origin redirect, popup, or link transitions to `NOT_EXECUTED`.
- `RESPONSE_COMPLETE` requires the streaming response to finish. A heading, badge, or action button alone is not a complete response.
- A direct trade request without `market_now` or `limit_level` stays in `IDLE` until the user chooses one. A bare research request resolves to `research_only` unless a local default says otherwise.
- `market_now` accepts only `NO_TRADE_NOW` or a market candidate with a reference price and maximum fill boundary. `limit_level` accepts only `NO_LIMIT_SETUP` or a limit candidate with one exact price and expiry or cancel condition. Never change the intent automatically.
- Approval is fresh, single-use, setup-bound, and must expire at the earlier of the configured lifetime, the TrueNorth text validity window, and an inline-card countdown. Missing validity is not executable.
- A changed token, direction, entry, leverage, margin, stop-loss, take-profit, or inline-card expiry invalidates approval.
- A response/card conflict or an unknown UI state transitions to `REJECTED_BY_POLICY`; a disconnect, login wall, selector mismatch, stale data, or uncertain result transitions to `NOT_EXECUTED`.
- A chat explanation that conflicts with the rendered ticket UI is a `REJECTED_BY_POLICY` condition. Before any later execution transition, the ticket must re-read **Reduce only**, **Take Profit / Stop Loss**, **Skip Open Order Confirmation**, and **Attach an agent**; no state may be inherited.
- The rendered **Customize** control observed in the authorized audit is treated as a watcher entry point, not an order-editing safe action. **Start watching** transitions to `NOT_EXECUTED` unless a separately scoped watcher feature exists.
- Version `0.1.6` transitions from `AWAITING_USER_APPROVAL` only to `REVIEW_COMPLETE`, `CANCELLED`, or `EXPIRED`. It never invokes the order panel. A future live state may be added only after independent mapping proves exact control semantics, sufficient available-to-trade balance, confirmation not skipped, and matching order/position read-back.
