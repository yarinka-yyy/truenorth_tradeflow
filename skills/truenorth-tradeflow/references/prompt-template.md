# TrueNorth prompts by entry intent

Choose exactly one intent before sending a prompt. An intent describes the user's desired entry style; it does not authorize an order.

- `research_only` — analysis without a planned entry.
- `market_now` — an entry that would be justified now at market, otherwise no trade.
- `limit_level` — a resting limit entry at a future level, otherwise no trade.

Do not silently change an intent. A market request never becomes a limit request, and a limit request never becomes a market request.

## `research_only`

Use when the user asks to analyze a token without asking to enter now or wait for a level.

```text
Analyze $[TOKEN] for a [scalp / swing] trade on [TIMEFRAME].

Return either NO_TRADE or RESEARCH_SETUP with:
- market and direction if a candidate exists;
- possible entry mode and exact entry price or range;
- stop-loss and take-profit levels;
- risk-reward, invalidation, reasons, and validity window.

Do not place an order, adjust the trade panel, or prepare a transaction; analysis only.
```

## `market_now`

Use only after the user explicitly asks to enter now or selects market entry.

```text
Analyze $[TOKEN] for a [TIMEFRAME] swing that could be entered now by market order.

Return exactly one of:
- NO_TRADE_NOW if an immediate market entry is not justified; or
- MARKET_NOW_SETUP with market, LONG or SHORT, current reference price, maximum acceptable fill boundary or exact allowed entry band, exact positive leverage, exact positive margin in USDC, stop-loss, take-profit levels, risk-reward, invalidation, reasons, and setup validity.

Do not suggest a limit-entry substitute. Do not place an order, adjust the trade panel, or prepare a transaction; analysis only.
```

## `limit_level`

Use only after the user explicitly asks to wait for a level or selects a limit entry.

```text
Analyze $[TOKEN] for a [TIMEFRAME] swing using only a resting limit entry at a future price level, not a market entry.

Return exactly one of:
- NO_LIMIT_SETUP if no valid level exists; or
- LIMIT_LEVEL_SETUP with market, LONG or SHORT, one exact limit-entry price, limit-order expiry or cancel condition, exact positive leverage, exact positive margin in USDC, stop-loss, take-profit levels, risk-reward, invalidation, reasons, and setup validity.

Do not suggest a market-entry substitute. Do not place an order, adjust the trade panel, or prepare a transaction; analysis only.
```

## Parsing contract

Capture the full completed response and any inline setup card. Do not classify an outcome from a leading heading, badge, or button alone: a `NO_*` label can coexist with a completed structured setup in the response body.

| Intent | Valid terminal outcome | Additional required data for a candidate |
|---|---|---|
| `research_only` | `NO_TRADE` or `RESEARCH_SETUP` | Direction, entry idea, SL, TP, invalidation, validity |
| `market_now` | `NO_TRADE_NOW` or `MARKET_NOW_SETUP` | Current reference price plus maximum fill boundary or allowed entry band |
| `limit_level` | `NO_LIMIT_SETUP` or `LIMIT_LEVEL_SETUP` | One exact limit price plus order expiry or cancel condition |

For every candidate, capture market, direction, leverage, margin, SL, TP, R:R, invalidation, response timestamp, and expiry. Use the earliest clear expiry from textual validity, inline-card countdown, and configured maximum age.

If the full response, inline card, or requested intent conflicts, classify it as `REJECTED_BY_POLICY`. In `review_only` mode, unavailable leverage or margin can be shown as analysis, but neither may be inferred from notional or the other value.

## Observed UI boundary

An inline setup card can appear beside a separate order panel. That panel may initially show a different side, leverage, order type, or empty size from the AI recommendation. Treat it as an unconfigured form, not as proof that the AI setup has been applied.

The labels **Edit**, **One-Click Setup**, **Place Order & Launch Agent**, and **Skip Open Order Confirmation** were observed, but their behavior is not verified for live use. Do not click or enable them in this release.
