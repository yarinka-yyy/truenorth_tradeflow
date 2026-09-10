# TrueNorth order card

Use this as the normal user-facing response. Write it in the language of the user's current conversation. Use English only for the TrueNorth request. Keep empty fields out of the message.

## Normal TrueNorth proposal

```markdown
**TrueNorth proposes opening now**

- Market: `[market]`
- Direction: `[Long / Short]`
- Entry: `Market`
- Leverage: `[TrueNorth recommendation / user choice]`
- Size: `[TrueNorth recommendation / user choice]`
- Protection: `[SL …, TP … / none]`
- Why: `[one short TrueNorth reason]`
- Confidence: `[if shown]`

The rendered TrueNorth ticket will be shown before opening.
```

## TrueNorth lifecycle-test setup

Use only when the user explicitly requested the real lifecycle/interface test.

```markdown
**TrueNorth lifecycle-test setup**

- Market: `[market]`
- Direction: `[Long / Short]`
- Entry: `Market`
- Leverage: `[test setup value]`
- Size: `[test setup value and unit]`
- Protection: `[SL …, TP … / none]`
- Why: `[one short test-context reason]`

This is a real interface/lifecycle test, not a trading recommendation. The rendered TrueNorth ticket will be shown before opening.
```

## Rendered ticket confirmation

```markdown
**Confirm the TrueNorth ticket**

- Market: `[rendered market]`
- Direction: `[rendered Long / Short]`
- Entry: `Market`
- Leverage: `[rendered leverage]`
- Size: `[rendered Size unit and value]`
- Order Value: `[rendered value]`
- Margin Required: `[rendered value]`
- Fees: `[rendered values]`
- Maximum slippage: `[rendered maximum]`
- Live estimates: `[liquidation and Est slippage, if the screen shows them]`
- Protection: `[rendered TP/SL or none]`
- Opening controls: `Reduce only [on/off]`, `Skip Open Order Confirmation [on/off]`, `Attach an agent [on/off]`
- Submit control: `[rendered label]`

Confirm this exact market order?
```

If the screen labels liquidation or slippage as a live estimate, include its latest value for visibility. A price-tick-only change to that estimate is re-read and reported; any changed bound field — selected field, Order Value, Margin Required, fee, opening control, submit label, or stated maximum — requires a replacement approval.

## Rendered final confirmation

```markdown
**TrueNorth requests final order confirmation**

Changed or newly shown:
- `[field]: [rendered value]`

Confirm this exact final order?
```

Do not show this section unless TrueNorth actually renders a separate confirmation.

## Result

```markdown
**TrueNorth result:** `[Opened / Open order / Not executed / Could not confirm]`

- Market: `[observed market if rendered]`
- Direction: `[observed side if rendered]`
- Size: `[observed size if rendered]`
- Status: `[observed status]`
```

Never populate the result from a button click, toast, wallet prompt, or chat response alone.