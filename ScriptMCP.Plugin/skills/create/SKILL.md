---
name: create
description: >-
  This skill should be used when the user asks to create a function, register a function, make a new function,
  add a function, or wants to create a new ScriptMCP dynamic function (code or instructions type).
version: 1.0.0
---

# Create a New ScriptMCP Dynamic Function

Guide the user through creating a new dynamic function.

If an argument was provided ($ARGUMENTS), use it as the function name. Otherwise, ask the user what function they want to create.

## Steps

1. Call `list_dynamic_functions` to check if a function with that name already exists
2. Ensure the name uses only letters, numbers, underscore, or hyphen
3. Ask the user to describe what the function should do
4. Determine the appropriate function type: `code` for C# compiled functions, `instructions` for plain-English guidance
5. Define the parameters as a JSON array
6. Write the function body (C# method body for code type, plain English for instructions type)
7. Register the function with `register_dynamic_function`
8. If compilation fails, fix the errors and re-register
9. Once registered, call `inspect_dynamic_function` to confirm and show the result to the user
