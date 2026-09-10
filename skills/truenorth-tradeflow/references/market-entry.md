# Market entry

Use this reference only after the user explicitly asks for a current **market** order through TrueNorth.

## 1. Ask TrueNorth once

Verify the exact origin `https://truenorth.xyz`, then send this analysis-only request in English. Replace bracketed values with the current request.

```text
Analyze one current [TOKEN]-USDC MARKET entry for [TIMEFRAME or current market].

Return one compact result with: market, LONG or SHORT, current reference price and timestamp, one plain-language reason, confidence, recommended leverage if available, recommended size or size unit if available, exact TP/SL only if available, and a short validity statement.

If confidence is low, say so plainly. Return NO_MARKET_SETUP only when you cannot provide a current market candidate.

Analysis only: do not place, prepare, modify, or cancel an order; do not change the ticket; do not use wallet, account, agent, signature, or trading tools.
```

Wait for the completed response. A heading, badge, or partial streaming text is not a result. Do not send a second prompt to audit the interface or ask the same analysis again.

## 2. Explain the result plainly

Use `templates/order-card.md` in the language of the user's current conversation. English is for the TrueNorth request only.

- Attribute the proposal to TrueNorth in the user's language rather than presenting it as fact.
- Keep the reason to one short sentence.
- Include the source recommendation only when it is current and complete.
- If TrueNorth returns `NO_MARKET_SETUP`, report that in one sentence and stop this market attempt. Never replace it with a limit order.
- If the user explicitly says the goal is only an interface pilot, use the localized equivalent of “Interface pilot, not a trading recommendation”; still show the current direction and ticket facts rather than inventing them.

If TrueNorth does not give a usable leverage or size, ask the user one short question for all missing choices together. Do not use a stored cap, default amount, or personal policy.

## 3. Build the live ticket

On the rendered TrueNorth ticket, explicitly set:

1. market;
2. Long or Short;
3. **Market** order type;
4. leverage;
5. Size in its currently displayed unit.

For an opening order, ensure **Reduce only** is off, **Skip Open Order Confirmation** is off, and **Attach an agent** is off. Keep TP/SL off unless the completed TrueNorth result supplies exact levels and the user accepts them. Do not use **Customize** or **Start watching**.

Read the ticket after setting it. Capture only the values needed for the user decision: market, side, type, leverage, Size unit/value, Order Value, Margin Required, fees/slippage, TP/SL, and the current submit label.

## 4. Get one exact ticket confirmation

Show the compact ticket card from `templates/order-card.md`. The user must approve the exact rendered values, not merely the earlier TrueNorth proposal.

Immediately before the click, re-read those fields. If a material value changed, show the updated short card and obtain a new confirmation.

Click the current rendered ticket-submit control once.

## 5. Handle a rendered platform confirmation

If the ticket click opens a rendered TrueNorth confirmation rather than an accepted result:

1. read its action, token size, estimated execution, Order Value, Margin Required, liquidation if shown, fees/slippage, protections, and final button label;
2. compare its material values with the ticket;
3. show a short confirmation card containing only the changed or newly shown values;
4. ask for a separate exact confirmation;
5. immediately re-read the same visible fields; if a material value changed, cancel that approval and show the new card;
6. after approval, click the rendered final control once.

If a wallet, signature, password, permission, or 2FA prompt appears at any time, stop for the user to complete it. Do not retry a click whose result is uncertain.

## 6. Read back and report

Read **Open Orders** and **Current Position** on the current TrueNorth page.

Report only the observed result using the template:

- **Opened** — a current position is rendered; localize this label for the user.
- **Open order** — a current resting order is rendered; localize this label for the user.
- **Not executed** — neither is rendered after the completed flow; localize this label for the user.
- **Could not confirm** — ticket, confirmation, session, or read-back became unclear; localize this label for the user.

Do not claim a trade succeeded from a click, toast, confirmation screen, or wallet prompt alone.
