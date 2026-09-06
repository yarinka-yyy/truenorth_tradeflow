# TrueNorth TradeFlow — Setup Review

**Setup ID:** `[one-time ID]`
**Source:** TrueNorth chat `[thread/reference]`, message `[message ID]`
**Received:** `[timestamp]`
**Expires:** `[earliest of TrueNorth text validity, inline-card expiry, and configured maximum age]`
**Source state:** `[NO_TRADE / candidate setup / rejected conflict]`
**Origin checked:** `https://truenorth.xyz`

| Field | TrueNorth result |
|---|---|
| Market | `[market]` |
| Direction | `[LONG / SHORT]` |
| Entry mode | `[market / limit]` |
| Entry | `[exact price or range]` |
| Leverage | `[exact positive number; required only for a future live release]` |
| Margin | `[exact positive USDC; required only for a future live release, not notional]` |
| Stop-loss | `[value]` |
| Take-profit | `[one or more levels]` |
| Risk/reward | `[value / not supplied]` |
| Invalidation | `[condition]` |

**TrueNorth rationale:** `[concise faithful summary]`

**Verbatim structured source response:** `[exact response or retained source reference]`

**Inline setup card:** `[exact displayed values and expiry, or absent]`

**Order-panel state:** `[not used; panel defaults are not setup evidence]`

**Live eligibility:** `[review_only in 0.1.3 / rejected with reason]`

**Decision:**

- Keep review-only `[setup ID]`
- Cancel
- Ask TrueNorth a follow-up

> Version `0.1.3` does not invoke TrueNorth order controls. Any future approval is valid only for this exact card. A change to market, side, entry, leverage, margin, SL, TP, or card expiry cancels it.
