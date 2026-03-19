---
name: task-delete
description: >-
  This skill should be used when the user asks to delete a scheduled task, remove a scheduled task,
  or wants to delete a ScriptMCP scheduled task.
version: 1.0.0
---

# Delete a ScriptMCP Scheduled Task

Delete a scheduled task created by ScriptMCP.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, ask the user which scheduled task they want to delete.

Ask the user for the interval in minutes if it is not already clear from context.

Call `delete_scheduled_task` with:
- `function_name`
- `interval_minutes`

Only delete the scheduled task the user identified.
