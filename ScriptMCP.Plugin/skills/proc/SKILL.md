---
name: proc
description: >-
  This skill should be used when the user asks to run out-of-process, run isolated, run in subprocess,
  or wants to execute a ScriptMCP script out-of-process.
version: 1.0.0
---

# Run a ScriptMCP Script Out-of-Process

Execute a registered script out-of-process in an isolated subprocess.

If an argument was provided ($ARGUMENTS), use it as the script name. Otherwise, call `list_scripts` to show available scripts and ask the user which one to run.

Call `inspect_script` on the chosen script to verify its parameters. If the script requires arguments, ask the user to provide them.

Ask whether the user wants output persisted to file:
- `output_mode: "Default"` for `--exec` (no persisted output file)
- `output_mode: "WriteNew"` for `--exec-out` (new timestamped file per execution)
- `output_mode: "WriteAppend"` for `--exec-out-append` (append to stable `<function>.txt`)

If not specified, default to `Default`.

Call `call_process` with the name, arguments, and chosen `output_mode`. Return the output exactly as received — do not add, remove, wrap, or modify the output in any way. If the result contains `[Output Instructions]`, follow them precisely and do not show the `[Output Instructions]` line itself.
