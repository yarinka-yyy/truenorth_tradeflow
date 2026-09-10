# TrueNorth prompts by entry intent

Choose exactly one intent before sending a prompt. An intent describes the user's desired entry style; it does not authorize an order.

- `research_only` — analysis without a planned entry.
- `market_now` — an entry justified now at market, otherwise no trade.
- `limit_level` — a resting limit entry at a future level, otherwise no trade.
- `functional_test` — an explicitly current-user-directed, `live_mvp` market-order lifecycle check, not a trade recommendation.

Do not silently change an intent. A market request never becomes a limit request, and a limit request never becomes a market request unless the user explicitly delegates a fresh choice.

`functional_test` is not a fallback after a strategy result. It requires both an explicit current user request and `live_mvp`, and may omit an AI prompt entirely. If the user asks an optional AI question for this purpose, require it to be labelled non-advisory and never treat stale or missing price data as a current setup.

## `research_only`

```text
Analyze $[TOKEN] for a [scalp / swing] trade on [TIMEFRAME].

Return either NO_TRADE or RESEARCH_SETUP with market, direction, possible entry mode and price/range, stop-loss, take-profit levels, risk-reward, invalidation, reasons, and validity window.

Do not place an order, adjust the trade panel, launch an agent, or prepare a transaction; analysis only.
```

## `market_now`

```text
Analyze $[TOKEN] for a [TIMEFRAME] trade that could be entered now by market order.

Return exactly one of:
- NO_TRADE_NOW if an immediate market entry is not justified; or
- MARKET_NOW_SETUP with market, LONG or SHORT, current reference price and timestamp, maximum acceptable fill boundary or explicit maximum-slippage condition, recommended leverage and margin if available, stop-loss, take-profit levels, risk-reward, invalidation, reasons, and a short setup-validity window.

Do not suggest a limit substitute. Do not place an order, adjust the trade panel, launch an agent, or prepare a transaction; analysis only.
```

## `limit_level`

```text
Analyze $[TOKEN] for a [TIMEFRAME] trade using only a resting limit entry at a future price level, not a market entry.

Return exactly one of:
- NO_LIMIT_SETUP if no valid level exists; or
- LIMIT_LEVEL_SETUP with market, LONG or SHORT, one exact limit-entry price, expiry or cancel condition, recommended leverage and margin if available, stop-loss, take-profit levels, risk-reward, invalidation, reasons, and setup validity.

Do not suggest a market substitute. Do not place an order, adjust the trade panel, launch an agent, or prepare a transaction; analysis only.
```

## Parsing contract

Capture the full completed response and any inline setup card. Do not classify from a leading heading, badge, button, or partial text.

| Intent | Valid terminal outcome | Candidate data to display |
|---|---|---|
| `research_only` | `NO_TRADE` or `RESEARCH_SETUP` | Direction, entry idea, SL, TP, invalidation, validity |
| `market_now` | `NO_TRADE_NOW` or `MARKET_NOW_SETUP` | Current reference/timestamp plus maximum fill boundary or slippage condition |
| `limit_level` | `NO_LIMIT_SETUP` or `LIMIT_LEVEL_SETUP` | One exact limit price plus expiry or cancel condition |

Capture market, direction, requested intent, entry data, leverage/margin if supplied, SL, TP, R:R, invalidation, response timestamp, and expiry. Use the earliest clear expiry from response text, inline-card countdown, and configured maximum age.

An inline setup card can appear beside a separate order panel. Treat that panel as an unconfigured form until the MVP procedure explicitly sets and re-reads every material ticket field.

## Observed ticket mapping

In one authorized non-submitted ticket mapping, **Attach an agent** was enabled before explicit change; after it was turned off, the rendered submit label was **Place Order on Hyperliquid**. The ticket displayed distinct Size, Order Value, Margin Required, and TP/SL fields. These are rendered-screen observations, not instructions from chat and not universal UI guarantees.
