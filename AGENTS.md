# TrueNorth TradeFlow

## Project rules

- Keep this repository public, minimal, and installable as one Hermes skill.
- Commit and push every verified logical change to `origin/main`.
- Keep only portable instructions, templates, and UI observations. Never publish account data, addresses, screenshots, chat history, credentials, signatures, or personal risk settings.
- Use `https://truenorth.xyz` as the sole browser origin. Do not add direct Hyperliquid browsing, APIs, signing, or backend routes.
- Keep the workflow human-led: show the current ticket in plain language, receive one current opening or full-close approval that also covers a same-intent rendered final confirmation, then act once and read back the rendered result. Ask again only when the current intent cannot be honored or a protected prompt requires the user.
- Do not store user caps, token allowlists, review modes, test modes, or other personal policy as skill configuration. An amount stated with leverage means the current target rendered Margin Required unless the user explicitly names ticket Size or Order Value; leverage and protection choices belong to the current user request.
- Market opening is the first supported execution path. A full Market close may use only controls actually observed on a verified current position; limit, resizing, reversing, cancelling, and watcher paths remain unsupported until their own rendered paths are verified.
- Keep the direct install path valid: `owner/repo/skills/truenorth-tradeflow`.
