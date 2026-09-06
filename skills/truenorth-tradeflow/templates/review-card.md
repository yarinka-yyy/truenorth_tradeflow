# TrueNorth TradeFlow — Setup Review

**Setup ID:** `[one-time ID]`
**Source:** TrueNorth chat `[thread/reference]`, message `[message ID]`
**Received:** `[timestamp]`
**Expires:** `[earlier of TrueNorth validity and configured maximum age]`

| Field | TrueNorth result |
|---|---|
| Market | `[market]` |
| Direction | `[LONG / SHORT]` |
| Entry mode | `[market / limit]` |
| Entry | `[exact price or range]` |
| Leverage | `[exact positive number; required for live]` |
| Margin | `[exact positive USDC; required for live, not notional]` |
| Stop-loss | `[value]` |
| Take-profit | `[one or more levels]` |
| Risk/reward | `[value / not supplied]` |
| Invalidation | `[condition]` |

**TrueNorth rationale:** `[concise faithful summary]`

**Verbatim structured source response:** `[exact response or retained source reference]`

**Live eligibility:** `[review_only / eligible / rejected with reason]`

**Decision:**

- Open this exact setup `[setup ID]`
- Cancel
- Ask TrueNorth a follow-up

> Approval is valid only for this exact card. Any change to market, side, entry, leverage, margin, SL, or TP cancels it. A live trade requires exact positive leverage and exact positive margin in USDC within the configured limits.
