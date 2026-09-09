# TrueNorth TradeFlow — Setup Review

**Setup ID:** `[one-time ID]`
**Requested intent:** `[research_only / market_now / limit_level]`
**TrueNorth outcome:** `[NO_TRADE / NO_TRADE_NOW / NO_LIMIT_SETUP / candidate setup]`
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
| Market reference / fill boundary | `[required for a market_now candidate; otherwise N/A]` |
| Limit expiry / cancel condition | `[required for a limit_level candidate; otherwise N/A]` |
| Leverage | `[exact value if supplied; no live eligibility in this release]` |
| Margin | `[exact USDC margin if supplied; no live eligibility in this release; notional alone is insufficient for future live use]` |
| Stop-loss | `[value]` |
| Take-profit | `[one or more levels]` |
| Risk/reward | `[value / not supplied]` |
| Invalidation | `[condition]` |

**TrueNorth rationale:** `[concise faithful summary]`

**Verbatim structured source response:** `[exact response or retained source reference]`

**Inline setup card:** `[exact displayed values and expiry, or absent]`

**Order-panel state:** `[not used; panel defaults are not setup evidence]`

**Future ticket-checkbox snapshot:** `[not used in review-only; re-read Reduce only, Take Profit / Stop Loss, Skip Open Order Confirmation=off, Attach an agent=off]`

**Watcher state:** `[Customize and Start watching not invoked]`

**Live eligibility:** `[review_only in 0.1.6 / rejected with reason]`

**Decision:**

- Keep review-only `[setup ID]`
- Cancel
- Ask TrueNorth a follow-up

> Version `0.1.6` does not invoke TrueNorth order controls. Any future approval is valid only for this exact card. A change to requested intent, market, side, entry, leverage, margin, SL, TP, fill boundary, limit expiry, or card expiry cancels it.
