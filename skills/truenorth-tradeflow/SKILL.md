---
name: truenorth-tradeflow
description: Propose and manage current TrueNorth trades through the rendered Chrome UI in Hermes or Codex; use for Market/Limit entries, verified full Market closes, or approved full-set order cancellation.
license: MIT
metadata:
  hermes:
    tags: [truenorth, trading, browser, market-order, limit-order]
    category: crypto
    related_skills: []
    requires_toolsets: [browser]
---

# TrueNorth TradeFlow

Use this skill when the user wants a current Market or Limit opening through the authenticated TrueNorth UI, a full Market close of a verified current position, or cancellation of the complete readable Open Orders set after approving every row in that set. For an opening, gather missing execution choices in one question, ask TrueNorth once, then combine its short proposal with the verified rendered ticket in one trade-approval card. A Limit opening keeps its exact resting price and may attach exact TP/SL controls. A close uses only the verified position row's observed **Close → Market** control after one close approval. Cancellation uses only **Open Orders → Cancel All** after one explicit approval of its full scope. A user can explicitly request a real market lifecycle test, where proving the UI path matters more than ordinary setup quality.

## Browser access

Read [references/browser-access.md](references/browser-access.md) before browsing. Select one available transport for this task: Hermes uses its Chrome remote-debugging connection; Codex can use the ChatGPT Chrome extension or a configured Chrome remote-debugging tool. A skill does not install or enable browser access by itself. Keep one browser connection and the same selected tab for the proposal, ticket, approval, action, and read-back. Follow the active tool's action and handoff rules if they are stricter than this skill's one-trade-approval workflow.

## When to use

- “Open ETH at market.”
- “Open an ETH limit entry from the level TrueNorth recommends.”
- “Ask TrueNorth whether SOL has a current market setup.”
- “Run a real ETH market lifecycle test; it does not need to be a good trade.”
- “Close my current ETH position at market.”
- “Cancel all current ETH open orders.”

Do not use this skill for direct Hyperliquid navigation, exchange APIs, wallet signing, background trading, limit closes, resizing, reversing, partial order cancellation, order editing, margin adjustment, TP/SL editing, or watcher controls.

## Common rules

1. Begin at `https://app.truenorth.xyz` and stop on every off-origin redirect, popup, or link.
2. Send one consolidated current TrueNorth request for an opening. A normal Market request uses the market-proposal route; a normal Limit request uses the limit-proposal route; an explicit market lifecycle test uses `MARKET_TEST_SETUP`, even when ordinary quality is weak. This choice and every user parameter apply only to the current request.
3. Before an opening request to TrueNorth, ask one concise question for missing user execution choices: market, Market or Limit entry, leverage, and amount. An amount stated with leverage defaults to target **Margin Required** under rule 5; ask about its meaning only when genuinely ambiguous. Direction, Limit price, and TP/SL may come from the provider unless the user chooses them. If the completed response still lacks an essential direction, entry price, or explicit protection choice, ask once for the remaining choices; never invent them. An analysis-only request needs no amount, ticket, or trade approval.
4. Keep Market and Limit distinct. This release supports Market and Limit openings, the full-scope **Cancel All** route, and only the verified position row's rendered **Close → Market** route. Never silently substitute an entry type, treat a resting Limit order as a position, or add to an existing exact-market position or opening order through this opening route.
5. Current user choices override a provider recommendation. An amount supplied with leverage means the user's target rendered **Margin Required**, unless the user explicitly calls it ticket Size or Order Value. Adjust only the visible Size control until the target is rendered; do not assume a leverage formula or choose a nearby amount. Do not store a cap, leverage, size, protection, test mode, or other personal policy.
6. For analysis-only requests, show a compact informational proposal in the user's language. For openings, build the live ticket first and put the compact attributed TrueNorth proposal and rendered ticket in one approval card; do not solicit approval of a preliminary setup. Never paste the full TrueNorth chat response. One current trade approval covers one opening or full-close intent and its same-intent on-origin final confirmation; cancellation separately covers the approved complete order set.
7. Before the first submit click, record rendered **Positions** and **Open Orders** rows for the selected market and account. Re-read the editable ticket and any final confirmation. For Market, normal price movement, base-size rounding, and updates to calculated Order Value, Margin Required, fees, estimated slippage, liquidation, and `Est. execution` do not create another approval while the approved input choices and displayed maximum slippage remain intact. For Limit, the approved entry price and TIF stay exact. Restore the requested amount target in the editable ticket if it drifts before submission; stop if it cannot be rendered. A later calculated refresh in the confirmation alone does not change the approved amount intent. Stop if the live Market price has crossed a proposed TP or SL, an input choice changes, the order becomes unavailable, or the state becomes uncertain. Do not use an arbitrary price-movement threshold.
8. Distinguish the live quote/mark, the final window's `Est. execution`, and the fill's rendered entry price. Do not infer the meaning of an unexplained estimate or treat its difference from the quote alone as a new intent. Re-read a same-intent on-origin final confirmation and click its final control once only if the active tool permits it; otherwise hand the control to the user. The user alone handles wallet, signature, password, permission, and 2FA prompts. Do not retry an uncertain submit.
9. After a submit, close, or cancellation, compare the selected market's rendered **Positions** and **Open Orders** rows with the baseline before claiming a result. Counts alone, toasts, and a working ticket's `Current Position` string are insufficient. Report a resting Limit entry as **Open order** until a new position row is rendered.

## Routes

- **Normal market proposal:** read `references/market-proposal.md`, then `references/market-ticket.md`.
- **Normal limit-entry proposal:** read `references/limit-proposal.md`, then `references/limit-ticket.md`.
- **Explicit lifecycle test:** read `references/market-lifecycle-test.md`, then `references/market-ticket.md`.
- **Verified current-position full close:** read `references/market-close.md`.
- **Approved full-set open-order cancellation:** read `references/order-cancel.md`.
- Use `templates/order-card.md` for every user-facing proposal, approval, and result.

Read only the route selected by the current request.
