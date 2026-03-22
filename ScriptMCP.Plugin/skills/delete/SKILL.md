---
name: delete
description: >-
  This skill should be used when the user asks to delete a script, remove a script, or wants to delete
  a registered ScriptMCP script.
version: 1.0.0
---

# Delete a ScriptMCP Script

Delete a registered script. This does not delete scheduled tasks.

If an argument was provided ($ARGUMENTS), use it as the script name. Otherwise, call `list_scripts` to show available scripts and ask the user which one to delete.

Before deleting, call `inspect_script` on the target script and show the user what they are about to delete. Ask for explicit confirmation before proceeding.

Only call `delete_script` after the user confirms.
