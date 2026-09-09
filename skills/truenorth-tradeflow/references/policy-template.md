# Reserved future policy template

Keep personal values in the Hermes profile configuration, not in this public repository. This template does not enable order execution in version `0.1.6`.

| Setting | Safe initial value | Purpose |
|---|---|---|
| `execution_mode` | `review_only` | The only supported mode in `0.1.6`; `live` is unsupported. |
| `default_entry_intent` | `research_only` | Default for a bare analysis request. A direct trade request without intent always asks `market_now` or `limit_level`. |
| `allowed_tokens` | Empty | Reserved for a future validated live release. |
| `max_leverage` | `0` | Reserved for a future validated live release. |
| `max_margin_usdc` | `0` | Reserved for a future validated live release. |
| `allowed_entry_modes` | `market,limit` | Reserved for a future validated live release. |
| `setup_max_age_seconds` | `300` | Expires a setup after five minutes. |

## Future live-release requirements

Do not set `execution_mode` to `live` in version `0.1.6`; it cannot authorize or execute an order. Before a later release may expose live execution, it must:

1. Set a non-empty token allowlist.
2. Set a maximum leverage.
3. Set a maximum margin.
4. Complete a dry run that ends at review/cancel, not execution.
5. Independently map the exact semantics of every order control and confirm that the current TrueNorth UI shows every material value before an execution action.
6. Verify a matching order or position read-back after a deliberately authorized test; a button click or toast is never proof.
7. For a market entry, enforce an exact maximum fill boundary. For a limit entry, enforce one exact price plus expiry or cancel condition. Never switch modes automatically.
8. Confirm the proposed margin and maximum fees fit within the available-to-trade balance, confirm that **Skip Open Order Confirmation** is off, and explicitly set **Attach an agent** off unless a separate agent workflow was approved.
9. Treat **Customize** as a potential watcher configuration, not a harmless advanced-order control; do not invoke **Start watching** during an order flow.

Any future live flow must accept only an exact positive numerical leverage and exact positive margin in USDC. A notional-only, estimated, ranged, zero, or missing value is rejected.

A policy is a safety limit, not a recommendation or a promise of profit.
