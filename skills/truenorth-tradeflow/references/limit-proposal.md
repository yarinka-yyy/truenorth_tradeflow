# Normal limit-entry proposal

Read this reference only when the user asks for a current **limit** entry and has not explicitly requested a market entry or a separate lifecycle-test route.

## Ask TrueNorth once

Verify the exact origin `https://app.truenorth.xyz`. For an opening request, first collect missing market, order type, leverage, and amount in one question; an amount with leverage means target Margin Required unless the user explicitly says otherwise. An analysis-only request needs none of the execution choices. Then send exactly one current English analysis-only request, replacing bracketed values with current facts.

```text
Analyze one current [TOKEN]-USDC LIMIT entry for the current market, using these current user constraints: [leverage and amount semantics, or explicitly requested ticket Size or Order Value].

Return one compact result with: market, LONG or SHORT, current reference price and timestamp, proposed limit entry price, one plain-language reason for that level, confidence, recommended leverage if available, exact TP/SL only if available, and a short validity/invalidation statement.

If confidence is low, say so plainly. Return NO_LIMIT_SETUP only when you cannot provide a current limit candidate.

Analysis only: do not place, prepare, modify, or cancel an order; do not change the ticket; do not use wallet, account, agent, signature, or trading tools.
```

Wait for the completed response. A heading, badge, partial stream, or agent thought is not a result. Do not spend extra quota on an audit or duplicate prompt.

## Turn the result into a proposal

Use `templates/order-card.md` in the language of the current conversation. For an opening, keep the provider summary until the ticket is built and combine both in one approval card; do not ask the user to approve a preliminary proposal.

- Attribute the proposal to TrueNorth rather than presenting it as fact.
- Include the exact limit entry price and exact TP/SL values when the response provides them.
- Keep the reason to one short sentence and state confidence when shown.
- A completed `NO_LIMIT_SETUP` ends this attempt. Never replace it with a Market order.
- If the provider names a different market or its stated validity or invalidation condition has explicitly been met, do not treat its level as a current opening setup. Normal movement in the live quote alone does not change the exact proposed Limit price. Report when the one provider request did not yield a usable current ticket; do not silently reuse an older chat.
- Merge current user choices with the completed result. If an opening still lacks direction, an exact limit price, or an explicit TP/SL-or-none choice, ask one short question covering all remaining choices. Do not treat this informational proposal as trade approval.
- A current user choice overrides a provider recommendation. Do not infer a price, amount, unit, protection state, or fallback order type.

For analysis-only requests, return the proposal and stop. For an opening, continue with `references/limit-ticket.md` only after the required current choices are explicit.
