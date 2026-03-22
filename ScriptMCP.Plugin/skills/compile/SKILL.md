---
name: compile
description: >-
  This skill should be used when the user asks to recompile a script, compile a script, rebuild a script,
  or wants to trigger recompilation of a ScriptMCP code script.
version: 1.0.0
---

# Recompile a ScriptMCP Script

Recompile a registered code-type script.

If an argument was provided ($ARGUMENTS), use it as the script name. Otherwise, call `list_scripts` to show available scripts and ask the user which one to recompile.

Call `compile_script` with the chosen name and report the result to the user. If compilation fails, show the errors.
