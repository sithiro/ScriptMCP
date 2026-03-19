---
name: compile
description: >-
  This skill should be used when the user asks to recompile a function, compile a function, rebuild a function,
  or wants to trigger recompilation of a ScriptMCP dynamic code function.
version: 1.0.0
---

# Recompile a ScriptMCP Dynamic Function

Recompile a registered code-type dynamic function.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, call `list_dynamic_functions` to show available functions and ask the user which one to recompile.

Call `compile_dynamic_function` with the chosen name and report the result to the user. If compilation fails, show the errors.
