# TrueNorth prompt template

Use this template in the TrueNorth chat. Replace only bracketed fields.

```text
Analyze $[TOKEN] for a [scalp / swing] trade on [TIMEFRAME].

Return either NO_TRADE or one structured setup with:
- market and exact direction: LONG or SHORT;
- entry mode and exact entry price or range;
- exact positive numerical leverage and exact positive margin in USDC; do not return notional instead of margin;
- stop-loss;
- take-profit levels;
- risk-reward ratio;
- invalidation condition;
- the main technical, derivatives, news, and market-structure reasons;
- setup validity window.

Do not place an order yet. I need to review the setup first.
```

## Parsing contract

Accept a setup only when all material fields are clear:

| Field | Required |
|---|---:|
| Market/token | Yes |
| LONG or SHORT | Yes |
| Market or limit entry | Yes |
| Exact entry or range | Yes |
| Positive numerical leverage | Required for live execution |
| Exact positive margin in USDC | Required for live execution; notional-only is rejected |
| Stop-loss | Yes |
| At least one take-profit | Yes |
| Invalidation condition | Yes |
| Time of response / validity | Yes |

In `review_only` mode, an unavailable leverage or margin can be displayed as analysis only. In `live` mode, both must be exact positive values; never infer margin from notional or leverage. If the response lacks a field, conflicts with itself, recommends a different token, or says `NO_TRADE`, return it to the user without trying to fill gaps.
