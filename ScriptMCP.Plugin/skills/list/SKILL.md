---
name: list
description: >-
  This skill should be used when the user asks to list functions, show functions, what functions exist,
  what can ScriptMCP do, or wants to discover all registered ScriptMCP dynamic functions.
version: 1.0.0
---

# List ScriptMCP Dynamic Functions

Discover all registered dynamic functions and present them to the user.

## Steps

1. Call `list_dynamic_functions` to retrieve all registered functions
2. Present the results to the user, showing each function's name, type, and description
3. If no functions are registered, let the user know and suggest they can create one

## Understanding the Output

Each function in the list has a **Type** field:

- **code** — a compiled C# function that executes and returns a result
- **instructions** — plain English instructions that the AI reads and follows when the function is called

## After Listing

The user may want to:
- **Run** a function — match their request to a function by name or description and call it
- **Inspect** a function — call `inspect_dynamic_function` to see its parameters and metadata
- **View source** — call `inspect_dynamic_function` with `fullInspection: true`
- **Create** a new function — use `register_dynamic_function`
- **Delete** a function — use `delete_dynamic_function` after confirmation
