---
name: delete
description: >-
  This skill should be used when the user asks to delete a function, remove a function, or wants to delete
  a registered ScriptMCP dynamic function.
version: 1.0.0
---

# Delete a ScriptMCP Dynamic Function

Delete a registered dynamic function. This does not delete scheduled tasks.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, call `list_dynamic_functions` to show available functions and ask the user which one to delete.

Before deleting, call `inspect_dynamic_function` on the target function and show the user what they are about to delete. Ask for explicit confirmation before proceeding.

Only call `delete_dynamic_function` after the user confirms.
