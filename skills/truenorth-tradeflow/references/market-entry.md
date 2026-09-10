# Market entry

Use this reference only after the user explicitly asks for a current **market** order through TrueNorth.

## 1. Choose the current request shape

Use the user's current words only. Never persist this choice as a setting.

- **Normal market proposal:** the user wants TrueNorth's current strategy-qualified market candidate.
- **Market lifecycle test:** the user explicitly wants to exercise a real market opening and says that setup quality is not the execution gate. After a verified position exists, its close controls may be observed for future support; this release does not yet offer a close workflow. This is an interface test, not a trading recommendation.

Verify the exact origin `https://truenorth.xyz`, then send exactly one matching analysis-only request in English. Do not send both prompts for one attempt.

### Normal market proposal

Replace bracketed values with the current request.

```text
Analyze one current [TOKEN]-USDC MARKET entry for [TIMEFRAME or current market].

Return one compact result with: market, LONG or SHORT, current reference price and timestamp, one plain-language reason, confidence, recommended leverage if available, recommended size or size unit if available, exact TP/SL only if available, and a short validity statement.

If confidence is low, say so plainly. Return NO_MARKET_SETUP only when you cannot provide a current market candidate.

Analysis only: do not place, prepare, modify, or cancel an order; do not change the ticket; do not use wallet, account, agent, signature, or trading tools.
```

### Market lifecycle test

Replace bracketed values with the current request.

```text
The user explicitly requests a real TrueNorth interface and market-lifecycle test for the currently selected [TOKEN]-USDC market. This is not a request for trading advice: a normal quality, confidence, reward/risk, or timing filter must not prevent the test.

Return exactly one compact MARKET_TEST_SETUP with a concrete current configuration sufficient to render a Market ticket: market, LONG or SHORT, current reference price and timestamp, specific leverage, specific Size and displayed unit, TP/SL values or explicit none, one short test-context reason, and a validity statement.

Do not return NO_TRADE or NO_MARKET_SETUP solely because the setup is weak. If a material setting cannot be provided, return TEST_SETUP_UNAVAILABLE and list only the missing settings.

Analysis only: do not place, prepare, modify, or cancel an order; do not change the ticket; do not use wallet, account, agent, signature, or trading tools.
```

Wait for the completed response. A heading, badge, or partial streaming text is not a result. Do not spend extra quota on an audit or duplicate prompt.

A lifecycle-test setup is complete only when it names the selected market, an explicit Long or Short direction, a specific leverage, a specific Size **and** its displayed unit, and either exact TP/SL values or explicit `none`. Treat a response that misses any of those fields as `TEST_SETUP_UNAVAILABLE`, regardless of its heading. Never infer a missing direction, Size unit, or protection choice.

## 2. Explain the result plainly

Use `templates/order-card.md` in the language of the user's current conversation. English is for the TrueNorth request only.

- Attribute the proposal to TrueNorth in the user's language rather than presenting it as fact.
- Keep the reason to one short sentence.
- A normal `NO_MARKET_SETUP` ends that normal market attempt. Never replace it with a limit order.
- A completed `MARKET_TEST_SETUP` is a user-requested test input, not a trading recommendation. Mark the compact proposal as a lifecycle/interface test.
- If the lifecycle-test result is `TEST_SETUP_UNAVAILABLE` or fails the completeness check, ask the user one short question for every missing current choice together, including direction, leverage, Size value/unit, and TP/SL-or-none. Do not send another provider prompt, reuse a stale setup, or invent a trading value.

If a normal result lacks a usable leverage, Size, Size unit, direction, or TP/SL choice, ask the user one short question for all missing choices together. Do not use a stored cap, default amount, or personal policy.

## 3. Build the live ticket

On the rendered TrueNorth ticket, explicitly set:

1. market;
2. Long or Short;
3. **Market** order type;
4. leverage;
5. Size in its currently displayed unit.

For an opening order, ensure **Reduce only** is off, **Skip Open Order Confirmation** is off, and **Attach an agent** is off. Set TP/SL only to exact levels supplied by the completed setup and accepted by the user, or leave them off only when the completed setup or the user explicitly says `none`. If protection state is unclear, collect that choice before configuring the ticket. Do not use **Customize** or **Start watching**.

Read the ticket after setting it. Capture only the values needed for the user decision: market, side, type, leverage, Size unit/value, Order Value, Margin Required, fees, maximum slippage, TP/SL, the three opening-control states, and the current submit label. If the rendered screen also shows liquidation price or `Est` slippage, capture those as current estimates.

## 4. Get one exact ticket confirmation

Show the compact rendered-ticket card from `templates/order-card.md`. The user must approve the exact rendered values, not merely the earlier TrueNorth proposal or test setup.

Immediately before the click, re-read every bound field: market, side, type, leverage, Size unit/value, Order Value, Margin Required, fees, maximum slippage, TP/SL, **Reduce only**, **Skip Open Order Confirmation**, **Attach an agent**, and submit label. If any bound field changed, show the updated short card and obtain a new confirmation.

On a current screen that labels liquidation price or slippage as a live `Est` value, re-read and retain its latest display. A price-tick-only change to those estimates does not invalidate an otherwise unchanged approval; it is not a user-selected ticket field. A change to a stated maximum slippage or any bound field still requires new confirmation.

Click the current rendered ticket-submit control once.

## 5. Handle a rendered platform confirmation

If the ticket click opens a rendered TrueNorth confirmation rather than an accepted result:

1. read its action, token size, estimated execution, Order Value, Margin Required, fees, maximum slippage, protections, and final button label; record liquidation and `Est` slippage separately when the current screen shows them as live estimates;
2. compare its material values with the ticket;
3. show a short confirmation card containing only the changed or newly shown values;
4. ask for a separate exact confirmation;
5. immediately re-read every bound field: action, token size, estimated execution, Order Value, Margin Required, fees, maximum slippage, protections, and final button label. If a bound field changed, cancel that approval and show the new card. Re-read current liquidation and `Est` slippage separately; a price-tick-only change to those live estimates does not invalidate an otherwise unchanged final approval;
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