# Market lifecycle test

Read this reference only when the user explicitly asks to exercise a real current market-opening interface path and says ordinary setup quality is not the execution gate. This is a per-request interface test, not trading advice or saved configuration.

## Collect the setup once

Verify the exact origin `https://truenorth.xyz`, then send exactly one current English analysis-only request. Replace bracketed values only with current facts and user choices.

```text
The user explicitly requests a real TrueNorth interface and market-lifecycle test for the currently selected [TOKEN]-USDC market. This is not a request for trading advice: normal quality, confidence, reward/risk, or timing filters must not prevent the test.

The user has already chosen these current execution constraints: [current user choices, or "none"]. Do not replace, calculate, or persist them. Return exactly one compact MARKET_TEST_SETUP with: market, LONG or SHORT direction, current reference price and timestamp, exact TP/SL values or explicit none, one short test-context reason, and a validity statement.

Do not return NO_TRADE or NO_MARKET_SETUP solely because the setup is weak. If direction or protection cannot be provided, return TEST_SETUP_UNAVAILABLE and list only the missing current choices.

Analysis only: do not place, prepare, modify, or cancel an order; do not change the ticket; do not use wallet, account, agent, signature, or trading tools.
```

Wait for the completed response. A heading, badge, or partial streaming text is not a result. Do not spend extra quota on an audit or duplicate prompt.

## Determine whether it is usable

A completed test setup must name the selected market, explicit Long or Short direction, and TP/SL values or explicit `none`. Current user constraints for leverage, Size, order value, or rendered Margin Required are not provider-owned fields and must not be replaced by the response.

- A completed `MARKET_TEST_SETUP` meeting those fields is usable even when its reason says the timing or quality is poor.
- If the response is `TEST_SETUP_UNAVAILABLE`, `NO_TRADE`, `NO_MARKET_SETUP`, or lacks direction or protection, ask the user once for all missing current choices. Do not send a second provider prompt, reuse stale data, or invent a direction or protection state.
- If the user has not chosen leverage, Size value/unit, order value, or a rendered Margin Required target needed for the ticket, collect that current choice now. Do not use a stored cap, a default amount, or a formula.

Use the lifecycle-test proposal card in `templates/order-card.md`, mark it as a real interface/lifecycle test rather than a recommendation, then continue with `references/market-ticket.md`.
