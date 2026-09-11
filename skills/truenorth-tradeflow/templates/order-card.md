# TrueNorth order card

Use this as the normal user-facing response. Write it in the language of the user's current conversation. Use English only for the TrueNorth request. Keep empty fields out of the message.

## Normal TrueNorth proposal

```markdown
**TrueNorth proposes opening now**

- Market: `[market]`
- Direction: `[Long / Short]`
- Entry: `Market`
- Leverage: `[TrueNorth recommendation / user choice]`
- User capital target: `[target Margin Required / explicitly requested Size or Order Value]`
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
- Current reference: `[provider price and timestamp]`
- Entry: `Market`
- Current user execution constraints: `[for example: leverage ×3; target Margin Required 10 USDC]`
- Protection: `[SL …, TP … / none]`
- Why: `[one short test-context reason]`

This is a real interface/lifecycle test, not a trading recommendation. The rendered TrueNorth ticket will be shown before opening.
```

## One opening approval

```markdown
**Approve the current TrueNorth opening**

- Market: `[rendered market]`
- Direction: `[rendered Long / Short]`
- Entry: `Market`
- Leverage: `[rendered leverage]`
- User capital target: `[requested Margin Required / explicitly requested Size or Order Value]`
- Size: `[current rendered Size unit and value]`
- Order Value: `[current rendered value]`
- Margin Required: `[current rendered value]`
- Fees: `[current rendered values]`
- Slippage: `[current rendered values]`
- Live estimates: `[liquidation and Est slippage, if the screen shows them]`
- Protection: `[rendered TP/SL or none]`
- Opening controls: `Reduce only [on/off]`, `Skip Open Order Confirmation [on/off]`, `Attach an agent [on/off]`
- Submit control: `[rendered label]`

Approve this current opening? This one approval also covers a same-intent rendered final confirmation.
```

Re-read and report the current ticket and final confirmation for visibility. Normal price movement, base-size rounding, calculated Order Value or Margin Required, fees, slippage, liquidation, estimated execution, or final submit wording do not create another confirmation. Ask again only when the requested market, side, Market type, leverage or amount target, protection, or opening controls cannot be honored, or when a failed attempt needs a new user choice.

## Result

```markdown
**TrueNorth result:** `[Opened / Open order / Not executed / Could not confirm]`

- Market: `[observed market if rendered]`
- Direction: `[observed side if rendered]`
- Size: `[observed size if rendered]`
- Status: `[observed status]`
```

Never populate the result from a button click, toast, wallet prompt, or chat response alone.
