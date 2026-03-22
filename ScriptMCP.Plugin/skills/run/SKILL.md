---
name: run
description: >-
  This skill should be used when the user asks to run a ScriptMCP script by name or by describing
  what they need in natural language. The user does not need to know the script name — a plain English query
  is matched to the best candidate script automatically.
version: 1.0.0
---

# Run a ScriptMCP Script

Execute a registered script. The user may provide an explicit script name or a natural language query that implies which script to call.

## Recognizing Implied Script Calls

Users will often describe what they want rather than naming a script directly. Match the user's intent to a registered script by purpose, not just by name.

**Examples:**

| User says | Likely script | Why |
|-----------|----------------|-----|
| "price of btc in eur" | `get_btc_price` | Describing the script's exact purpose |
| "what's bitcoin at right now?" | `get_btc_price` | Asking for a price they've fetched before |
| "convert 50 dollars to euros" | `usd_to_eur` | Describing the script's exact purpose |
| "show me the market overview" | `market_fast_fancy` or similar | Referencing a dashboard they built |
| "how's my portfolio doing?" | `portfolio` | Referring to a script by its domain |
| "check cpu usage" | `get_cpu_utilization` | Describing the metric, not the script |
| "what time is it?" | `get_time` | Trivial query that maps to a known script |

The key signal is that the user's request maps directly to what a registered script does, even though they never mention the script by name.

## Execution Steps

1. Call `list_scripts` to discover available scripts
2. Match the user's query to one or more candidate scripts by name and description
3. If exactly one script matches, call `inspect_script` to verify its type, parameters, and purpose
4. If multiple scripts could match, ask the user to clarify — do not guess
5. If the script requires arguments, extract them from the query or ask the user to provide them
6. Call `call_script` with the name and arguments

## Handling Output

- Return the output exactly as received — do not add, remove, wrap, or modify the output in any way
- If the result contains `[Output Instructions]`, follow them precisely and do not show the `[Output Instructions]` line itself
- If the output instructions say to return the output exactly, return it with zero modifications
- For `instructions`-type scripts, read and follow the returned instructions yourself — do NOT return the raw instruction text to the user
