---
name: truenorth-tradeflow
description: Open TrueNorth market orders after user approval.
version: 0.3.0
author: yarinka-yyy, Hermes Agent
license: MIT
platforms: [windows, macos, linux]
metadata:
  hermes:
    tags: [truenorth, trading, browser, market-order]
    category: crypto
    related_skills: []
    requires_toolsets: [browser]
---

# TrueNorth TradeFlow

Use this skill when the user wants a current market order through the authenticated TrueNorth UI. It turns one TrueNorth analysis into a short, human-readable proposal, then opens only the exact ticket the user confirms.

## When to use

- “Open ETH at market.”
- “Ask TrueNorth whether SOL has a current market setup.”
- “Show the current market ticket, then open after I confirm.”

Do not use this skill for direct Hyperliquid navigation, exchange APIs, wallet signing, background trading, position closing, or limit orders. Closing and limit paths are intentionally not claimed until their real controls have been observed.

## Common rules

1. Begin at `https://truenorth.xyz` and stop on every off-origin redirect, popup, or link.
2. Send one consolidated TrueNorth analysis request for the current market attempt. Wait for its completed result; do not spend extra quota on audit or retry prompts.
3. Keep market and limit distinct. If the user asks for a limit order, say that this market-first release does not yet support it; never silently substitute a market order.
4. Show the user a compact card in their language. Preserve the full completed TrueNorth result only as execution data; do not dump its chat text into the reply.
5. The user approves the exact displayed ticket, not a previous suggestion. A changed ticket requires a new confirmation.
6. If TrueNorth renders a separate order-confirmation screen, treat it as a new action: show its material values, get a separate confirmation, then re-read it before one final click.
7. The user handles every wallet, signature, password, permission, or 2FA prompt. Never type or approve one.
8. After any submit click, read the rendered **Open Orders** and **Current Position** before claiming an order or position exists.

## Market route

Read `references/market-entry.md` and use `templates/order-card.md`.

## Current UI observation

One rendered TrueNorth ticket showed separate Size, Order Value, Margin Required, TP/SL, and agent controls. Its initial submit control opened a separate on-origin confirmation screen in that observation. Read the current screen every time; this is not a universal UI contract.

## Verification

A market attempt is complete only when all are true:

- every observed page kept the exact TrueNorth origin;
- one completed current TrueNorth response supplied the proposal;
- the user saw and confirmed the current rendered ticket;
- any rendered final confirmation was separately confirmed;
- no protected prompt was handled by Hermes; and
- Open Orders or Current Position was read back and reported plainly.
