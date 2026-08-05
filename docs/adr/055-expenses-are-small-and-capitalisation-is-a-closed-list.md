# ADR 055 — Expenses stay small, and only freight and duty may reach stock

**Status:** Accepted · **Date:** 2026-07-22 · **Phase:** 7

## Context

"Expenses" invites scope creep. It can become an accounts-payable ledger, an approval engine, a budgeting module, a mileage claims system. A point-of-sale product needs none of that; it needs to record that money was spent, on what, by whom, and — critically — whether it belongs in the cost of stock.

## Decision

`Expense` records a category, a net amount, a separately-held tax amount, a date incurred, who recorded it, and a description. Tax is separate because it is usually recoverable and therefore not a cost of anything. `GrossAmount` is derived.

**Only freight and customs duty may be capitalised** into the value of stock, via `LinkToGoodsReceipt`. Every other category is refused by the domain.

Capitalising overheads into stock defers cost out of the current period and flatters margin. It is a genuine temptation, a recurring audit finding, and easy to do by accident when the link is a free choice. So the answer is a property of the category — a closed list in code — rather than a flag someone can tick on the record.

`IsCapitalised` is derived from the presence of the link, so an expense cannot be attached to two deliveries or counted twice.

Approval mirrors purchase orders: the person who recorded an expense cannot approve it. Expenses are the smaller and softer of the two routes money leaves a business by, which is exactly why they are the less watched one. Rejection requires a reason, and a rejected expense can no longer be capitalised.

## Consequences

There is no expense payment, no reimbursement, no attachment or receipt image, no budget, no recurring expense. All are plausible and all are out of scope until asked for.

The link between an expense and a receipt is recorded on the expense, but **applying that expense as a landed cost is a separate act** through the landed cost mechanism — and if the goods have already been received, through ADR 049's split. Linking does not itself move any value, which means a linked-but-unapplied expense is possible and is another gap that wants a report.

Expenses are scoped to tenant, company **and** branch, unlike suppliers. A branch's electricity bill belongs to that branch, and expense reporting is overwhelmingly a per-site question.
