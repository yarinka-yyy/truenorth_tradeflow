# TrueNorth TradeFlow

## Project rules

- Keep this repository public, minimal, and installable as one skill in Hermes and Codex.
- Commit and push every verified logical change to `origin/main`.
- Keep only portable instructions, templates, and UI observations. Never publish account data, addresses, screenshots, chat history, credentials, signatures, or personal risk settings.
- Use `https://app.truenorth.xyz` as the sole browser origin. Do not add direct Hyperliquid browsing, APIs, signing, or backend routes.
- Keep the workflow human-led: collect missing execution choices together; for an opening, combine the short TrueNorth proposal and current ticket in one approval card covering a same-intent rendered final confirmation. Analysis-only requests receive an informational proposal without trade approval. Act once and compare exact-market rows with the pre-action state. Normal Market price or estimate movement alone does not require another approval; a changed intent, invalid protection, uncertain state, or protected prompt does.
- Do not store user caps, token allowlists, review modes, test modes, or other personal policy as skill configuration. An amount stated with leverage means the current target rendered Margin Required unless the user explicitly names ticket Size or Order Value; leverage and protection choices belong to the current user request.
- Use only documented rendered paths for Market or Limit opening, a full Market close from a verified current position, and `Cancel All` only when the complete account Open Orders set is readable and approved. Do not assume `Cancel All` is market-specific. Resizing, reversing, partial cancellation, and watcher paths remain unsupported until their own rendered paths are verified.
- Browser access is host-specific: Hermes uses Chrome remote debugging; Codex may use the Chrome extension or a separately configured Chrome remote-debugging tool. Keep one connection for the task and apply the active tool's financial-action and handoff rules. Browser, website, and tool permissions are separate from trade approval.
- Keep the direct install path valid: `owner/repo/skills/truenorth-tradeflow`.
