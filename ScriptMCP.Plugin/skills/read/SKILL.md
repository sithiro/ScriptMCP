---
name: read
description: >-
  This skill should be used when the user asks to read task output, show scheduled output, what was the last result,
  or wants to read the latest ScriptMCP scheduled task output for a script.
version: 1.0.0
---

# Read ScriptMCP Scheduled Task Output

Read the latest scheduled-task output written by ScriptMCP for a script.

If an argument was provided ($ARGUMENTS), use it as the script name. Otherwise, ask the user which script's scheduled-task output they want to read.

Call `read_scheduled_task` with:
- `function_name`

Return the output exactly as received.
