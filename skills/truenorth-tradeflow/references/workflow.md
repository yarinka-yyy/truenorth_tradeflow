# Workflow state machine

```text
IDLE
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
- Approval is fresh, single-use, setup-bound, and must expire at the earlier of the configured lifetime, the TrueNorth text validity window, and an inline-card countdown. Missing validity is not executable.
- A changed token, direction, entry, leverage, margin, stop-loss, take-profit, or inline-card expiry invalidates approval.
- A response/card conflict or an unknown UI state transitions to `REJECTED_BY_POLICY`; a disconnect, login wall, selector mismatch, stale data, or uncertain result transitions to `NOT_EXECUTED`.
- Version `0.1.2` transitions from `AWAITING_USER_APPROVAL` only to `REVIEW_COMPLETE`, `CANCELLED`, or `EXPIRED`. It never invokes the order panel. A future live state may be added only after independent mapping proves exact control semantics and matching order/position read-back.
