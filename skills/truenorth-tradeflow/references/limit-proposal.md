# Normal limit-entry proposal

Read this reference only when the user asks for a current **limit** entry and has not explicitly requested a market entry or a separate lifecycle-test route.

## Ask TrueNorth once

Verify the exact origin `https://truenorth.xyz`, then send exactly one current English analysis-only request. Replace bracketed values with the current request.

```text
Analyze one current [TOKEN]-USDC LIMIT entry for the current market, using these current user constraints: [leverage and amount semantics, or explicitly requested ticket Size or Order Value].

Return one compact result with: market, LONG or SHORT, current reference price and timestamp, proposed limit entry price, one plain-language reason for that level, confidence, recommended leverage if available, exact TP/SL only if available, and a short validity/invalidation statement.

If confidence is low, say so plainly. Return NO_LIMIT_SETUP only when you cannot provide a current limit candidate.

Analysis only: do not place, prepare, modify, or cancel an order; do not change the ticket; do not use wallet, account, agent, signature, or trading tools.
```

Wait for the completed response. A heading, badge, partial stream, or agent thought is not a result. Do not spend extra quota on an audit or duplicate prompt.

## Turn the result into a proposal

Use `templates/order-card.md` in the language of the current conversation.

- Attribute the proposal to TrueNorth rather than presenting it as fact.
- Include the exact limit entry price and exact TP/SL values when the response provides them.
- Keep the reason to one short sentence and state confidence when shown.
- A completed `NO_LIMIT_SETUP` ends this attempt. Never replace it with a Market order.
- Merge current user choices with the completed result. Ask the user one short question for all choices absent from both, including direction, limit price, leverage plus target rendered Margin Required (or explicitly requested ticket Size/Order Value), and TP/SL-or-none.
- A current user choice overrides a provider recommendation. Do not infer a price, amount, unit, protection state, or fallback order type.

Continue with `references/limit-ticket.md` only after the required current choices are explicit.
