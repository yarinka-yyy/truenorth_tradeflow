# Normal market proposal

Read this reference only when the user asks for a current **market** proposal and has not explicitly requested a lifecycle test.

## Ask TrueNorth once

Verify the exact origin `https://app.truenorth.xyz`. For an opening request, first collect missing market, order type, leverage, and amount in one question; an amount with leverage means target Margin Required unless the user explicitly says otherwise. An analysis-only request needs none of the execution choices. Then send exactly one current English analysis-only request, replacing bracketed values with current facts.

```text
Analyze one current [TOKEN]-USDC MARKET entry for [TIMEFRAME or current market].

Return one compact result with: market, LONG or SHORT, current reference price and timestamp, one plain-language reason, confidence, recommended leverage if available, recommended size or size unit if available, exact TP/SL only if available, and a short validity statement.

If confidence is low, say so plainly. Return NO_MARKET_SETUP only when you cannot provide a current market candidate.

Analysis only: do not place, prepare, modify, or cancel an order; do not change the ticket; do not use wallet, account, agent, signature, or trading tools.
```

Wait for the completed response. A heading, badge, or partial streaming text is not a result. Do not spend extra quota on an audit or duplicate prompt.

## Turn the result into a proposal

Use `templates/order-card.md` in the language of the current conversation. For an opening, keep the provider summary until the ticket is built and combine both in one approval card; do not ask the user to approve a preliminary proposal.

- Attribute the proposal to TrueNorth rather than presenting it as fact.
- Keep the reason to one short sentence.
- A completed `NO_MARKET_SETUP` ends this normal attempt. Never replace it with a limit order.
- If the provider names a different market or its stated validity has explicitly expired, do not treat its levels as a current opening setup. Normal movement from its reference price is handled by the live Market ticket, not by another provider prompt. Report when the one provider request did not yield a usable current ticket; do not silently reuse an older chat.
- Merge current user choices with the completed result. If an opening still lacks direction or an explicit TP/SL-or-none choice, ask one short question covering all remaining choices. Do not treat this informational proposal as trade approval.
- A current user choice overrides a provider recommendation. Do not infer an amount, unit, or protection state.

For analysis-only requests, return the proposal and stop. For an opening, continue with `references/market-ticket.md` only after the required current choices are explicit.
