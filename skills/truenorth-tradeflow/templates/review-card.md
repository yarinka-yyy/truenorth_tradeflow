# TrueNorth TradeFlow — Setup and Live Ticket

**Setup ID:** `[one-time ID]`

**Requested intent:** `[research_only / market_now / limit_level / functional_test]`

**TrueNorth outcome:** `[NO_TRADE / NO_TRADE_NOW / NO_LIMIT_SETUP / candidate / functional_test non-advisory]`

**Source:** `[TrueNorth chat thread/reference / explicit current user functional_test request]`

**Received / expires:** `[timestamp / not applicable functional_test] / [earliest validity / not applicable functional_test]`

**Origin checked:** `https://truenorth.xyz`

## Setup

For `functional_test`, record **User request:** `[current explicit lifecycle-test request]`, **AI text:** `[none or non-advisory only]`, then mark all strategy fields below `[not used]`.

| Field | TrueNorth result |
|---|---|
| Market | `[market / not used in functional_test setup]` |
| Direction | `[LONG / SHORT / not used in functional_test setup]` |
| Entry mode | `[market / limit / not used in functional_test setup]` |
| Entry / fill condition | `[exact entry, boundary, or slippage condition / not used in functional_test setup]` |
| Leverage | `[recommended / absent / not used in functional_test setup]` |
| Margin | `[recommended / absent / not used in functional_test setup]` |
| Stop-loss | `[value / absent / not used in functional_test setup]` |
| Take-profit | `[levels / absent / not used in functional_test setup]` |
| Risk/reward | `[value / absent / not used in functional_test setup]` |
| Invalidation | `[condition / not used in functional_test setup]` |

**TrueNorth rationale:** `[concise faithful summary / not applicable functional_test]`

**Verbatim source:** `[exact response or retained source reference / not applicable functional_test]`

## Live ticket — only in `live_mvp`; `functional_test` additionally requires its explicit current user request

| Field | Current rendered ticket |
|---|---|
| Market | `[read back]` |
| Side | `[Buy/Long or Sell/Short]` |
| Order type | `[Market only for functional_test / Market or Limit for strategy]` |
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
| Open Orders baseline | `[exact market read-back before ticket-submit click]` |
| Current Position baseline | `[exact market read-back before ticket-submit click]` |

**Final live confirmation:** `[Confirm exactly this ticket / Cancel]`

## Platform confirmation — if rendered after the ticket click; separately approved when rendered

| Field | Current rendered confirmation |
|---|---|
| Exchange | `[read back]` |
| Action | `[read back]` |
| Token size | `[unit and value]` |
| Estimated execution | `[read back]` |
| Order Value / Margin Required | `[read back]` |
| Estimated liquidation | `[read back]` |
| Fees / slippage | `[read back]` |
| Reduce only | `[read back]` |
| TP/SL | `[read back]` |
| Skip Open Order Confirmation | `[read back]` |
| Final action label | `[current rendered label]` |

**Final platform confirmation:** `[Confirm exactly this confirmation / Cancel]`

For `functional_test`, no rendered platform confirmation requires the mandatory baseline comparison: no change is **Live MVP:** `NOT_EXECUTED`; any new or changed exact-market order or position is `FAILED`. Do not make a final action click.

## Result

- **Review-only:** `[kept / cancelled / follow-up requested]`
- **Post-click baseline comparison:** `[not applicable / unchanged / new or changed exact-market order or position -> FAILED]`
- **Live MVP:** `[not submitted / Open Order read back / Position read back / NOT_EXECUTED / FAILED]`
- **Observed order or position:** `[only after read-back]`

> A candidate card does not populate the ticket. A ticket click, a platform-confirmation screen, toast, wallet prompt, or review screen does not prove execution. Any change to intent, market, side, order type, leverage, size unit/value, Order Value, Margin Required, fees, slippage, any protection checkbox, TP/SL, current submit label, or expiry invalidates the corresponding confirmation and requires a new snapshot. A rendered platform confirmation is a separate action and needs its own exact approval.
