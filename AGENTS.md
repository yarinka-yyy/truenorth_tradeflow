# TrueNorth TradeFlow

## Project rules

- Keep this repository public, minimal, and installable as one Hermes skill.
- Commit and push every verified logical change to `origin/main`.
- Keep only portable instructions, templates, and UI observations. Never publish account data, addresses, screenshots, chat history, credentials, signatures, or personal risk settings.
- Use `https://truenorth.xyz` as the sole browser origin. Do not add direct Hyperliquid browsing, APIs, signing, or backend routes.
- Keep the workflow human-led: show the exact current ticket in plain language, receive one current confirmation, then act once and read back the rendered result.
- Do not store user caps, token allowlists, review modes, test modes, or other personal policy as skill configuration. Size, leverage, and protection choices belong to the current user request.
- Market is the first supported execution path. Add close behavior only after a real position exposes its rendered controls; add limit behavior only after the first market open-and-close lifecycle is verified.
- Keep the direct install path valid: `owner/repo/skills/truenorth-tradeflow`.
