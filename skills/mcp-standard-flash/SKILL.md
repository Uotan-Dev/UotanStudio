# MCP Standard Flash SOP

## When to use
- User requests a standard device flash workflow.

## Inputs
- `target`, `plan` object, user confirmation.

## Steps
1. `preview_flash_plan` with target+plan. Return planId and warnings; no writes.
2. Ask user to explicitly confirm. Require `confirm=true` and `confirmationText="EXECUTE"`.
3. `execute_flash_plan` only after confirmation and only when AllowFlash is enabled.
4. `get_flash_report` for final structured status.

## Safety
- Default no real device write without confirmation.
- Destructive raw flash actions are blocked from direct `execute_action`; use plan actions.
- Keep raw logs in artifact refs; return summary/report.