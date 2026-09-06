# Personal policy template

Keep personal values in the Hermes profile configuration, not in this public repository.

| Setting | Safe initial value | Purpose |
|---|---|---|
| `execution_mode` | `review_only` | Blocks live action until deliberately enabled. |
| `allowed_tokens` | Empty | Only named tokens may reach a live review. |
| `max_leverage` | `0` | Zero keeps live execution disabled. |
| `max_margin_usdc` | `0` | Zero keeps live execution disabled. |
| `allowed_entry_modes` | `market,limit` | Restricts allowed order types. |
| `setup_max_age_seconds` | `300` | Expires a setup after five minutes. |

## Minimum live policy

Before setting `execution_mode` to `live`:

1. Set a non-empty token allowlist.
2. Set a maximum leverage.
3. Set a maximum margin.
4. Complete a dry run that ends at review/cancel, not execution.
5. Confirm the current TrueNorth UI shows all material values before its execution action.

A policy is a safety limit, not a recommendation or a promise of profit.
