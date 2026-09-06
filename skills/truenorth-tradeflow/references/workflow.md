# Workflow state machine

```text
IDLE
  -> ANALYSIS_REQUESTED
  -> SETUP_CAPTURED
  -> VALIDATED
  -> AWAITING_USER_APPROVAL
  -> EXECUTION_REQUESTED
  -> AWAITING_EXTERNAL_CONFIRMATION (when a protected prompt appears)
  -> POSITION_VERIFIED
```

Any state may end in:

```text
CANCELLED | EXPIRED | REJECTED_BY_POLICY | NOT_EXECUTED | FAILED
```

## Transition rules

- Only `AWAITING_USER_APPROVAL` may enter `EXECUTION_REQUESTED`.
- Approval is fresh, single-use, setup-bound, and expires after the configured lifetime.
- A changed token, direction, entry, leverage, margin, stop-loss, or take-profit invalidates approval.
- A protected prompt transitions to `AWAITING_EXTERNAL_CONFIRMATION`; Hermes stops and the user handles it locally.
- A matching order or position read-back is required for `POSITION_VERIFIED`.
- An unknown UI state, disconnect, login wall, selector mismatch, stale data, or uncertain result transitions to `NOT_EXECUTED`. Never retry a trade automatically.
