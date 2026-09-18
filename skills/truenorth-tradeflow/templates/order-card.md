# TrueNorth order card

Use this as the normal user-facing response. Write it in the language of the user's current conversation. Use English only for the TrueNorth request. Keep empty fields out of the message. Use the proposal sections alone for analysis-only requests; for an opening, combine their short provider facts with the live-ticket approval below in one message.

## Normal TrueNorth proposal

```markdown
**TrueNorth proposes opening now**

- Market: `[market]`
- Direction: `[Long / Short]`
- Current reference: `[provider price and timestamp]`
- Entry: `Market`
- Leverage: `[TrueNorth recommendation / user choice]`
- User capital target: `[target Margin Required / explicitly requested Size or Order Value]`
- Protection: `[SL …, TP … / none]`
- Why: `[one short TrueNorth reason]`
- Confidence: `[if shown]`

This is TrueNorth's informational setup, not a request to approve a trade. If opening was requested, the rendered ticket will be shown before one trade approval.
```

## Normal TrueNorth limit proposal

```markdown
**TrueNorth proposes a limit entry**

- Market: `[market]`
- Direction: `[Long / Short]`
- Current reference: `[provider price and timestamp]`
- Entry: `Limit [exact price]`
- Leverage: `[TrueNorth recommendation / user choice]`
- User capital target: `[target Margin Required / explicitly requested Size or Order Value]`
- Time in force: `GTC`
- Protection: `[SL …, TP … / none]`
- Why: `[one short TrueNorth reason]`
- Confidence: `[if shown]`

This is TrueNorth's informational setup, not a request to approve a trade. If opening was requested, the rendered limit ticket will be shown before one trade approval. A limit order is reported as resting until Positions proves a fill.
```

## TrueNorth lifecycle-test setup

Use only when the user explicitly requested the real lifecycle/interface test.

```markdown
**TrueNorth lifecycle-test setup**

- Market: `[market]`
- Direction: `[Long / Short]`
- Current reference: `[provider price and timestamp]`
- Entry: `Market`
- Current user execution constraints: `[for example: leverage ×3; target Margin Required 10 USDC]`
- Protection: `[SL …, TP … / none]`
- Why: `[one short test-context reason]`

This is a real interface/lifecycle test, not a trading recommendation or trade approval request. The rendered TrueNorth ticket will be shown before one trade approval.
```

## One opening approval

```markdown
**Approve the current TrueNorth opening**

- TrueNorth setup: `[attributed direction, current reference and timestamp, one short reason; mark a lifecycle test as a test]`
- Market: `[rendered market]`
- Direction: `[rendered Long / Short]`
- Entry: `[Market / Limit [exact price]]`
- Leverage: `[rendered leverage]`
- User capital target: `[requested Margin Required / explicitly requested Size or Order Value]`
- Time in force: `[GTC for a Limit entry / n/a for Market]`
- Size: `[current rendered Size unit and value]`
- Order Value: `[current rendered value]`
- Margin Required: `[current rendered value]`
- Fees: `[current rendered values]`
- Slippage: `[current estimate and maximum]`
- Liquidation estimate: `[if shown]`
- Protection: `[rendered TP/SL or none]`
- Opening controls: `Reduce only [on/off]`, `Skip Open Order Confirmation [on/off]`, `Attach an agent [on/off]`
- Submit control: `[rendered label]`

Approve this current opening, including a same-intent rendered final confirmation? For Market, normal price movement and updated estimates within the displayed maximum slippage do not require another approval; the exact Limit price stays fixed.
```

## One full-close approval

```markdown
**Approve the current TrueNorth full Market close**

- Trading account: `[selected account, for identifying the position]`
- Market: `[rendered market]`
- Current position: `[rendered side and size]`
- Close route: `Positions → Close → Market`
- Intent: `full close`
- Current Open Orders: `[rendered count]`
- Close control: `[rendered Market label]`

Approve this full close? This one approval also covers a same-intent rendered final confirmation.
```

## One full-set cancellation approval

```markdown
**Approve cancellation of the complete current TrueNorth Open Orders set**

- Trading account: `[selected account]`
- Filter: `All`
- Positions: `[rendered count and row]`
- Open Orders: `[complete rendered count]`
- Orders in scope: `[every exact rendered row, with its market]`
- Cancellation control: `Open Orders → Cancel All`
- Intent: `cancel the full account Open Orders set shown above; this control is not proven to be market-specific`

Approve cancellation of every listed order? This one approval covers one current `Cancel All` click and a same-intent rendered confirmation if one appears.
```

Re-read the approved intent and any final confirmation. A normal Market price or `Est. execution` change alone does not require another approval; the exact Limit price must remain fixed. Ask again only when the approved market, side, type, leverage, amount target, maximum slippage, TIF, protection, opening/close controls, full-close target, or complete cancellation scope cannot be honored, or a failed attempt needs a new user choice. A connected browser or website permission is separate from trade approval.

## Result

```markdown
**TrueNorth result:** `[Opened / Open order / Closed / Cancelled / Not closed / Not executed / Could not confirm]`

- Market: `[observed market if rendered]`
- Direction: `[observed side if rendered]`
- Entry type: `[Market / resting Limit / filled position]`
- Size: `[observed size if rendered]`
- Entry price: `[filled position entry price only if rendered; do not use Est. execution]`
- Status: `[observed status; use Closed only when the approved position row is absent or flat]`
- Open Orders: `[observed exact-market rows and count if relevant]`
```

Never populate the result from a button click, toast, wallet prompt, or chat response alone. Compare exact-market rows before and after an opening; use **Closed** when the approved row disappears or becomes flat, even if other positions remain.
