---
name: task-start
description: >-
  This skill should be used when the user asks to start a scheduled task, enable a scheduled task, resume a task,
  or wants to start a ScriptMCP scheduled task.
version: 1.0.0
---

# Start a ScriptMCP Scheduled Task

Start or enable a ScriptMCP scheduled task.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, ask the user which scheduled task they want to start.

Ask the user for the interval in minutes if it is not already clear from context.

Call `start_scheduled_task` with:
- `function_name`
- `interval_minutes`

Only start the scheduled task the user identified.
