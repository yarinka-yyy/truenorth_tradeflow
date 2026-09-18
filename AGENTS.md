# TrueNorth TradeFlow

## Project rules

- Keep this repository public, minimal, and installable as one skill in Hermes and Codex.
- Commit and push every verified logical change to `origin/main`.
- Keep only portable instructions, templates, and UI observations. Never publish account data, addresses, screenshots, chat history, credentials, signatures, or personal risk settings.
- Use `https://app.truenorth.xyz` as the sole browser origin. Do not add direct Hyperliquid browsing, APIs, signing, or backend routes.
- Keep the workflow human-led: show the current ticket in plain language, receive one current opening or full-close approval that also covers a same-intent rendered final confirmation, then act once and read back the rendered result. Ask again only when the current intent cannot be honored or a protected prompt requires the user.
- Do not store user caps, token allowlists, review modes, test modes, or other personal policy as skill configuration. An amount stated with leverage means the current target rendered Margin Required unless the user explicitly names ticket Size or Order Value; leverage and protection choices belong to the current user request.
- Use only documented rendered paths for Market or Limit opening, a full Market close from a verified current position, and exact-market `Cancel All`. Resizing, reversing, partial cancellation, and watcher paths remain unsupported until their own rendered paths are verified.
- Browser access is host-specific: Hermes uses Chrome remote debugging; Codex recommends the Chrome extension and may use a separately configured Chrome remote-debugging tool. Apply the active host's financial-action and handoff rules.
- Keep the direct install path valid: `owner/repo/skills/truenorth-tradeflow`.
