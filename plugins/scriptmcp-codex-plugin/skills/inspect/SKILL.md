---
name: inspect
description: >-
  This skill should be used when the user asks to inspect a script, show script details, view script metadata,
  or wants to see the metadata and parameters of a ScriptMCP script.
version: 1.0.0
---

# Inspect a ScriptMCP Script

Inspect a registered script and display its details to the user.

If an argument was provided ($ARGUMENTS), use it as the script name. Otherwise, call `list_scripts` to show available scripts and ask the user which one to inspect.

Call `inspect_script` with the chosen name. Do not use `fullInspection` unless the user specifically asks for source code — default to the summary view.

Present the inspection result to the user exactly as returned.
