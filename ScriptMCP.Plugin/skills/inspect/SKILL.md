---
name: inspect
description: >-
  This skill should be used when the user asks to inspect a function, show function details, view function metadata,
  or wants to see the metadata and parameters of a ScriptMCP dynamic function.
version: 1.0.0
---

# Inspect a ScriptMCP Dynamic Function

Inspect a registered dynamic function and display its details to the user.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, call `list_dynamic_functions` to show available functions and ask the user which one to inspect.

Call `inspect_dynamic_function` with the chosen name. Do not use `fullInspection` unless the user specifically asks for source code — default to the summary view.

Present the inspection result to the user exactly as returned.
