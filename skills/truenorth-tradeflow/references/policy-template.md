# Local MVP policy template

Keep personal values in the Hermes profile configuration, not in this public repository.

| Setting | Initial value | MVP behavior |
|---|---|---|
| `execution_mode` | `review_only` | Default. `live_mvp` permits one current, user-confirmed order attempt. |
| `default_entry_intent` | `research_only` | Bare analysis default. Direct trade requests ask market-now or limit-level unless the user delegates that choice now. |
| `allowed_tokens` | Empty | Optional hard token allowlist when populated. |
| `max_leverage` | `0` | Optional hard maximum. Zero means no configured cap. |
| `max_margin_usdc` | `0` | Optional hard maximum against the ticket's displayed Margin Required. Zero means no configured cap. |
| `allowed_entry_modes` | `market,limit` | Optional hard mode allowlist. |
| `setup_max_age_seconds` | `300` | Expires a stale setup. |

## Minimal live-MVP requirements

`live_mvp` does not need a backend or a perfect universal UI model. It does require these practical checks for one current ticket:

1. One fresh analysis-only response for the chosen intent.
2. A candidate card that the user sees before the ticket is prepared. `NO_TRADE_NOW` or `NO_LIMIT_SETUP` ends the attempt.
3. Explicit ticket values: market, side, market/limit type, leverage, and Size unit/value.
4. A live read of Order Value, Margin Required, fees, slippage, and all checkboxes. Size in USDC is not silently renamed to margin.
5. If a positive local cap is configured, its corresponding ticket field must be exact, numeric, and within the cap. Missing, ranged, estimated, substituted, or out-of-cap values stop the attempt. A zero cap means the user deliberately uses the live ticket snapshot without that policy limit.
6. **Reduce only** off for an opening order; **Skip Open Order Confirmation** off; **Attach an agent** off.
7. Exact TP/SL fields only if enabled and populated from the reviewed setup. Never use **Customize** or **Start watching** in this order path.
8. A final confirmation binds to the complete shown snapshot. Re-read it immediately before the one submit click; any changed field cancels that confirmation and requires a new snapshot and confirmation, then an Open Orders or Positions read-back.

## Not included

V0.2.0 does not approve wallet prompts, sign transactions, retry a submission, or close/manage positions. A close flow needs an observed real position and its rendered controls.
