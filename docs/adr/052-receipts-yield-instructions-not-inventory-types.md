# ADR 052 — Posting a receipt yields plain instructions, not Inventory objects

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 7

## Context

Receiving goods must move stock. The direct implementation has `GoodsReceipt.Post` construct `StockMovement` instances and hand them back, which requires Purchasing to reference Inventory's domain assembly.

ADR 002 forbids this: modules take no hard dependency on each other's domain types. The rule is not aesthetic. Purchasing referencing Inventory means Inventory's invariants can be constructed from outside Inventory, its factories become part of Purchasing's compile surface, and the two modules can no longer be reasoned about — or deployed, or tested — separately.

## Decision

`GoodsReceipt.Post` returns a `GoodsReceiptPosting`: the receipt id, its number, warehouse, business date, posting time, and a list of `StockReceiptInstruction` records carrying variant, quantity, landed unit cost, and allocated landed cost. Every type in that payload is either a primitive or lives in SharedKernel. The application layer reads the instructions and calls Inventory's factories.

`SupplierReturn.Dispatch` follows the same shape with `StockReturnInstruction`.

The instruction carries the **landed** unit cost — supplier price plus this line's share of freight and duty — because that is the figure that enters weighted average cost. A product bought at 10 with 2 of freight cost 12 to have, and pricing off 10 produces a margin report that is confidently wrong. The allocated landed cost is carried separately as well, so a buyer asking why the cost is not the price they negotiated can be shown the arithmetic.

## Consequences

The application layer is genuinely responsible for the transaction: post the receipt, apply the instructions to Inventory, commit. Nothing enforces at compile time that the second step happens. This is the same exposure the Sales module already has, and it is discharged the same way — by a reconciliation report over receipts posted versus movements recorded. **That report is not yet built.**

The mapping code is real work that a shared type would have avoided. It is small, it is the boundary made visible, and it is where a change in either module's shape shows up as a compile error in one obvious place rather than as a subtle behaviour change in two.

The gate test in `PurchasingToInventoryWorkflowTests` exercises exactly this seam: Purchasing produces instructions, the test applies them to `StockBalance` and `StockMovement` as the application layer will, and the resulting weighted average is asserted. Neither module references the other.
