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

Do not place an order yet; analysis only. I need to review the setup first.
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

In `review_only` mode, an unavailable leverage or margin can be displayed as analysis only. A future live mode must require both to be exact positive values; never infer margin from notional or leverage. If the response lacks a field, conflicts with itself, recommends a different token, or says `NO_TRADE`, return it to the user without trying to fill gaps.

## Completed-response capture

1. Send the prompt once and wait for the TrueNorth response to finish before reading it.
2. Preserve the full response, not just its first heading, badge, or TL;DR.
3. If TrueNorth renders an inline setup card, capture its market, direction, entry, TP, SL, R:R, and countdown as source data.
4. Treat a conflict between the response text and the inline card as `REJECTED_BY_POLICY` for any future live flow.
5. Use the earliest clear expiry from the textual validity, inline-card countdown, and configured maximum age. An absent or ambiguous expiry is review-only.

## Observed UI boundary

An inline setup card can appear beside a separate order panel. That panel may initially show a different side, leverage, order type, or empty size from the AI recommendation. Treat it as an unconfigured form, not as proof that the AI setup has been applied.

The labels **Edit**, **One-Click Setup**, and **Place Order & Launch Agent** were observed, but their order semantics were not verified. Do not click them in this release.
