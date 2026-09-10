# TrueNorth order card

Use this as the normal user-facing response. Write it in the language of the user's current conversation. Use English only for the TrueNorth analysis request. Keep empty fields out of the message.

## TrueNorth proposal

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

For an interface-only pilot, state the localized equivalent of: **This is an interface pilot, not a trading recommendation.**

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
- Fees / Slippage: `[rendered values]`
- Protection: `[rendered TP/SL or none]`

Confirm this exact market order?
```

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
