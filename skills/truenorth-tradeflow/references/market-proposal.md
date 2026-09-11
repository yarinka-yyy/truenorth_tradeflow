# Normal market proposal

Read this reference only when the user asks for a current **market** proposal and has not explicitly requested a lifecycle test.

## Ask TrueNorth once

Verify the exact origin `https://truenorth.xyz`, then send exactly one current English analysis-only request. Replace bracketed values with the current request.

```text
Analyze one current [TOKEN]-USDC MARKET entry for [TIMEFRAME or current market].

Return one compact result with: market, LONG or SHORT, current reference price and timestamp, one plain-language reason, confidence, recommended leverage if available, recommended size or size unit if available, exact TP/SL only if available, and a short validity statement.

If confidence is low, say so plainly. Return NO_MARKET_SETUP only when you cannot provide a current market candidate.

Analysis only: do not place, prepare, modify, or cancel an order; do not change the ticket; do not use wallet, account, agent, signature, or trading tools.
```

Wait for the completed response. A heading, badge, or partial streaming text is not a result. Do not spend extra quota on an audit or duplicate prompt.

## Turn the result into a proposal

Use `templates/order-card.md` in the language of the current conversation.

- Attribute the proposal to TrueNorth rather than presenting it as fact.
- Keep the reason to one short sentence.
- A completed `NO_MARKET_SETUP` ends this normal attempt. Never replace it with a limit order.
- Merge current user choices with the completed result. Ask the user one short question for all choices absent from both, such as direction, leverage plus target rendered Margin Required, explicitly requested ticket Size/Order Value, or TP/SL-or-none.
- A current user choice overrides a provider recommendation. Do not infer an amount, unit, or protection state.

Continue with `references/market-ticket.md` only after the required current choices are explicit.
