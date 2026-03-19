---
name: task-stop
description: >-
  This skill should be used when the user asks to stop a scheduled task, disable a scheduled task, pause a task,
  or wants to stop a ScriptMCP scheduled task.
version: 1.0.0
---

# Stop a ScriptMCP Scheduled Task

Stop or disable a ScriptMCP scheduled task.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, ask the user which scheduled task they want to stop.

Ask the user for the interval in minutes if it is not already clear from context.

Call `stop_scheduled_task` with:
- `function_name`
- `interval_minutes`

Only stop the scheduled task the user identified.
