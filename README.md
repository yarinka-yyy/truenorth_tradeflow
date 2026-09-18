# TrueNorth TradeFlow

One installable skill for Hermes Agent and Codex. It asks TrueNorth's on-site AI for a current setup, shows the user the rendered order ticket, and reads the rendered result after the permitted action. All site interaction stays in Chrome at `https://app.truenorth.xyz`.

## Install

**Hermes Agent**

```bash
hermes skills install yarinka-yyy/truenorth_tradeflow/skills/truenorth-tradeflow
```

Start a new Hermes session. In the interactive CLI, connect Chrome with `/browser connect` and check `/browser status` before using the skill.

**Codex**

Ask Codex to install the skill with its built-in installer:

```text
$skill-installer install https://github.com/yarinka-yyy/truenorth_tradeflow/tree/main/skills/truenorth-tradeflow
```

The installed folder is `truenorth-tradeflow` in the user's Codex skills directory. Start a new task if the skill does not appear.

## Choose browser access

| Host | Browser path | Setup |
| --- | --- | --- |
| Hermes | Chrome remote debugging | Connect a local Chrome session with `/browser connect`. |
| Codex, extension | ChatGPT Chrome extension | In the desktop app, enable Chrome under **Settings → Computer Use**, choose **Allow for this site** for `app.truenorth.xyz` if desired, then select `@Chrome` or its TrueNorth tab. The active tool may hand the final trade click to the user. |
| Codex, tested agent-click route | Chrome remote debugging | Configure a CDP-capable browser tool such as Chrome DevTools MCP before the task, then reuse one connection to the selected tab. One observed test completed the final click after trade approval. |

The skill does not install a browser extension or an MCP server. The Codex extension route requires the desktop app; Codex CLI/cloud does not gain that browser through the skill. [Codex MCP configuration](https://learn.chatgpt.com/docs/extend/mcp?surface=cli) and [Chrome DevTools MCP](https://developer.chrome.com/docs/devtools/agents/get-started/configuration) describe persistent CDP setup. Chrome 144+ `--autoConnect` attaches to an enabled existing session after Chrome's connection prompt; manually starting Chrome with a debugging port requires a separate, non-default profile. See [browser access](skills/truenorth-tradeflow/references/browser-access.md) for the exact route boundaries.

## Scope

The skill has documented rendered paths for one current Market or Limit opening and one full Market close from a verified position row. `Cancel All` is allowed only when the complete Open Orders set is readable and approved; the UI has not established that this control is market-specific. The skill does not trade in the background, use exchange APIs, sign wallet prompts, or treat a button click as proof of execution. The user approves the current ticket once; the active tool may require the user to perform the final trade click. The skill compares the rendered **Positions** and **Open Orders** with their pre-action state.

The repository contains only the [skill](skills/truenorth-tradeflow/SKILL.md), its route references and response template. It is independent of TrueNorth and Hyperliquid. License: MIT.
