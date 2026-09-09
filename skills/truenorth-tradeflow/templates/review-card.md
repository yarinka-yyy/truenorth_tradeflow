# TrueNorth TradeFlow — Setup and Live Ticket

**Setup ID:** `[one-time ID]`

**Requested intent:** `[research_only / market_now / limit_level]`

**TrueNorth outcome:** `[NO_TRADE / NO_TRADE_NOW / NO_LIMIT_SETUP / candidate]`

**Source:** `TrueNorth chat [thread/reference]`

**Received / expires:** `[timestamp] / [earliest validity]`

**Origin checked:** `https://truenorth.xyz`

## Setup

| Field | TrueNorth result |
|---|---|
| Market | `[market]` |
| Direction | `[LONG / SHORT]` |
| Entry mode | `[market / limit]` |
| Entry / fill condition | `[exact entry, boundary, or slippage condition]` |
| Leverage | `[recommended / absent]` |
| Margin | `[recommended / absent]` |
| Stop-loss | `[value / absent]` |
| Take-profit | `[levels / absent]` |
| Risk/reward | `[value / absent]` |
| Invalidation | `[condition]` |

**TrueNorth rationale:** `[concise faithful summary]`

**Verbatim source:** `[exact response or retained source reference]`

## Live ticket — only in `live_mvp`

| Field | Current rendered ticket |
|---|---|
| Market | `[read back]` |
| Side | `[Buy/Long or Sell/Short]` |
| Order type | `[Market / Limit]` |
| Leverage | `[read back]` |
| Size | `[unit and value]` |
| Order Value | `[read back]` |
| Margin Required | `[read back]` |
| Fees / slippage | `[read back]` |
| Reduce only | `[off for opening]` |
| TP/SL | `[off, or exact populated fields]` |
| Skip Open Order Confirmation | `[off]` |
| Attach an agent | `[off]` |
| Submit label | `[current rendered label]` |

**Final live confirmation:** `[Confirm exactly this ticket / Cancel]`

## Result

- **Review-only:** `[kept / cancelled / follow-up requested]`
- **Live MVP:** `[not submitted / Open Order read back / Position read back / NOT_EXECUTED]`
- **Observed order or position:** `[only after read-back]`

> A candidate card does not populate the ticket. A ticket click, toast, wallet prompt, or review screen does not prove execution. Any change to intent, market, side, order type, leverage, size unit/value, Order Value, Margin Required, fees, slippage, any protection checkbox, TP/SL, current submit label, or expiry invalidates the final confirmation and requires a new live-ticket snapshot.
