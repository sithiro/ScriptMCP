---
name: task-create
description: >-
  This skill should be used when the user asks to schedule a script, create a scheduled task, run on a schedule,
  set up recurring execution, or wants to manage ScriptMCP scheduled tasks (create, read, list, start, stop, or delete).
version: 1.0.0
---

# Manage ScriptMCP Scheduled Tasks

Manage scheduled tasks created by ScriptMCP.

If an argument was provided ($ARGUMENTS), use it as the script name. Otherwise, ask the user which script's scheduled task they want to manage.

Determine whether the user wants to create a task, read the latest task output, list tasks, start a task, stop a task, or delete a task.

## Create

- Ask for the interval in minutes if it is not already clear.
- Ask for JSON arguments if the script needs them. Otherwise use `"{}"`.
- Ask whether the user wants append mode. Default to `false` if not specified.
- Call `create_scheduled_task` with `function_name`, `function_args`, `interval_minutes`, `append`.

## Read

- Call `read_scheduled_task` with `function_name`.

## List

- Call `list_scheduled_tasks` with no arguments.

## Start

- Ask for the interval in minutes if it is not already clear.
- Call `start_scheduled_task` with `function_name`, `interval_minutes`.

## Stop

- Ask for the interval in minutes if it is not already clear.
- Call `stop_scheduled_task` with `function_name`, `interval_minutes`.

## Delete

- Ask for the interval in minutes if it is not already clear.
- Call `delete_scheduled_task` with `function_name`, `interval_minutes`.

Only perform the scheduled-task action the user requested.
