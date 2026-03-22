---
name: list
description: >-
  This skill should be used when the user asks to list scripts, show scripts, what scripts exist,
  what can ScriptMCP do, or wants to discover all registered ScriptMCP scripts.
version: 1.0.0
---

# List ScriptMCP Scripts

Discover all registered scripts and present them to the user.

## Steps

1. Call `list_scripts` to retrieve all registered scripts
2. Present the results to the user, showing each script's name, type, and description
3. If no scripts are registered, let the user know and suggest they can create one

## Understanding the Output

Each script in the list has a **Type** field:

- **code** — a compiled C# script that executes and returns a result
- **instructions** — plain English instructions that the AI reads and follows when the script is called

## After Listing

The user may want to:
- **Run** a script — match their request to a script by name or description and call it
- **Inspect** a script — call `inspect_script` to see its parameters and metadata
- **View source** — call `inspect_script` with `fullInspection: true`
- **Create** a new script — use `create_script`
- **Delete** a script — use `delete_script` after confirmation
