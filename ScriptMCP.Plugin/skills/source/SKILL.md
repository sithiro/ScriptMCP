---
name: source
description: >-
  This skill should be used when the user asks to show source code, view function code, show the code,
  or wants to see the full source code of a ScriptMCP dynamic function.
version: 1.0.0
---

# Show ScriptMCP Dynamic Function Source Code

Show the full source code and compiled status of a registered dynamic function.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, call `list_dynamic_functions` to show available functions and ask the user which one to view.

Call `inspect_dynamic_function` with the chosen name and set `fullInspection` to `true`. Present the result to the user exactly as returned.
