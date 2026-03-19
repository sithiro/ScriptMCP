---
name: db-delete
description: >-
  This skill should be used when the user asks to delete a database, remove a database, or wants to delete
  a non-default ScriptMCP database.
version: 1.0.0
---

# Delete a ScriptMCP Database

Delete a ScriptMCP database file.

If an argument was provided ($ARGUMENTS), use it as the target path or database name. Otherwise, ask the user which database they want to delete.

Before deletion, ask for explicit confirmation.

Only after the user confirms, call `delete_database` with:
- `path`: the chosen target
- `confirm`: true

Present the tool result to the user exactly as returned.
