---
name: db-set
description: >-
  This skill should be used when the user asks to switch database, change database, use a different database,
  or wants to switch the active ScriptMCP database.
version: 1.0.0
---

# Switch the Active ScriptMCP Database

Switch the active ScriptMCP database.

If an argument was provided ($ARGUMENTS), use it as the target path or database name. Otherwise, ask the user which database they want to switch to.

Call `set_database` with:
- `path`: the provided target

If the tool says the database does not exist, ask the user whether they want to create it. Only if they explicitly confirm, call `set_database` again with:
- `path`: the same target
- `create`: true

Present the tool result to the user exactly as returned.
