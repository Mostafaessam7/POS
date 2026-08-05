import { useEffect, useState, type FormEvent } from "react";
import { ApiError, apiGet, apiPost } from "../api/client";
import { useOrganization } from "../api/organization";
import { useLanguage } from "../i18n/LanguageContext";
import type { TranslationKey } from "../i18n/translations";

const EXPENSE_CATEGORIES: { value: string; labelKey: TranslationKey }[] = [
  { value: "Freight", labelKey: "expenses.categoryFreight" },
  { value: "CustomsDuty", labelKey: "expenses.categoryCustomsDuty" },
  { value: "Rent", labelKey: "expenses.categoryRent" },
  { value: "Utilities", labelKey: "expenses.categoryUtilities" },
  { value: "Maintenance", labelKey: "expenses.categoryMaintenance" },
  { value: "Marketing", labelKey: "expenses.categoryMarketing" },
  { value: "ProfessionalFees", labelKey: "expenses.categoryProfessionalFees" },
  { value: "Travel", labelKey: "expenses.categoryTravel" },
  { value: "OfficeSupplies", labelKey: "expenses.categoryOfficeSupplies" },
  { value: "BankCharges", labelKey: "expenses.categoryBankCharges" },
  { value: "Other", labelKey: "expenses.categoryOther" },
];

const STATUS_KEY: Record<string, TranslationKey> = {
  Recorded: "expenses.statusRecorded",
  Approved: "expenses.statusApproved",
  Rejected: "expenses.statusRejected",
};

interface Expense {
  id: string;
  expenseNumber: string;
  category: string;
  amount: number;
  taxAmount: number;
  incurredOn: string;
  status: "Recorded" | "Approved" | "Rejected";
  description: string;
  isCapitalised: boolean;
}

function statusBadgeClass(status: string): string {
  if (status === "Approved") return "app-badge app-badge--positive";
  if (status === "Rejected") return "app-badge app-badge--negative";
  return "app-badge";
}

export function ExpensesPage() {
  const { t } = useLanguage();
  const { companies } = useOrganization();

  const [expenses, setExpenses] = useState<Expense[] | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const [companyId, setCompanyId] = useState("");
  const [branchId, setBranchId] = useState("");
  const [expenseNumber, setExpenseNumber] = useState("");
  const [category, setCategory] = useState("Other");
  const [amount, setAmount] = useState("0.00");
  const [taxAmount, setTaxAmount] = useState("0.00");
  const [currency, setCurrency] = useState("USD");
  const [incurredOn, setIncurredOn] = useState(new Date().toISOString().slice(0, 10));
  const [description, setDescription] = useState("");
  const [formError, setFormError] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState(false);

  const [rejectTargetId, setRejectTargetId] = useState<string | null>(null);
  const [rejectReason, setRejectReason] = useState("");
  const [rejectError, setRejectError] = useState<string | null>(null);

  async function loadExpenses() {
    try {
      setExpenses(await apiGet<Expense[]>("/api/v1/expenses"));
    } catch (err) {
      setListError(err instanceof ApiError ? err.message : t("expenses.loadError"));
    }
  }

  useEffect(() => {
    loadExpenses();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const selectedCompany = companies?.find((c) => c.id === companyId);

  async function handleRecord(event: FormEvent) {
    event.preventDefault();
    setFormError(null);
    setIsSaving(true);

    try {
      await apiPost("/api/v1/expenses", {
        companyId,
        branchId,
        expenseNumber,
        category,
        amount: Number(amount),
        taxAmount: Number(taxAmount),
        currency,
        incurredOn,
        description,
      });

      setExpenseNumber("");
      setAmount("0.00");
      setTaxAmount("0.00");
      setDescription("");
      await loadExpenses();
    } catch (err) {
      setFormError(err instanceof ApiError ? err.message : t("expenses.createError"));
    } finally {
      setIsSaving(false);
    }
  }

  async function handleApprove(id: string) {
    setActionError(null);
    try {
      await apiPost(`/api/v1/expenses/${id}/approve`);
      await loadExpenses();
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : t("expenses.approveError"));
    }
  }

  async function handleReject(id: string, event: FormEvent) {
    event.preventDefault();
    setRejectError(null);

    try {
      await apiPost(`/api/v1/expenses/${id}/reject`, { reason: rejectReason });
      setRejectTargetId(null);
      setRejectReason("");
      await loadExpenses();
    } catch (err) {
      setRejectError(err instanceof ApiError ? err.message : t("expenses.rejectError"));
    }
  }

  return (
    <div>
      <div className="app-page-header">
        <h1>{t("expenses.title")}</h1>
      </div>

      <div className="app-card">
        <h2 className="app-card__title">{t("expenses.recordExpense")}</h2>
        {formError && <div className="app-error-banner">{formError}</div>}
        <form onSubmit={handleRecord}>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="expense-company">{t("common.company")}</label>
              <select
                id="expense-company"
                value={companyId}
                onChange={(e) => {
                  setCompanyId(e.target.value);
                  setBranchId("");
                }}
                required
              >
                <option value="" disabled>
                  {t("common.selectCompany")}
                </option>
                {companies?.map((company) => (
                  <option key={company.id} value={company.id}>
                    {company.name}
                  </option>
                ))}
              </select>
            </div>
            <div className="app-form-field">
              <label htmlFor="expense-branch">{t("common.branch")}</label>
              <select
                id="expense-branch"
                value={branchId}
                onChange={(e) => setBranchId(e.target.value)}
                required
                disabled={!selectedCompany}
              >
                <option value="" disabled>
                  {t("common.selectBranch")}
                </option>
                {selectedCompany?.branches.map((branch) => (
                  <option key={branch.id} value={branch.id}>
                    {branch.name}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="expense-number">{t("expenses.expenseNumber")}</label>
              <input
                id="expense-number"
                value={expenseNumber}
                onChange={(e) => setExpenseNumber(e.target.value)}
                required
              />
            </div>
            <div className="app-form-field">
              <label htmlFor="expense-category">{t("expenses.category")}</label>
              <select id="expense-category" value={category} onChange={(e) => setCategory(e.target.value)}>
                {EXPENSE_CATEGORIES.map((c) => (
                  <option key={c.value} value={c.value}>
                    {t(c.labelKey)}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="expense-amount">{t("expenses.amount")}</label>
              <input
                id="expense-amount"
                type="number"
                min="0.01"
                step="0.01"
                value={amount}
                onChange={(e) => setAmount(e.target.value)}
                required
              />
            </div>
            <div className="app-form-field">
              <label htmlFor="expense-tax">{t("expenses.tax")}</label>
              <input
                id="expense-tax"
                type="number"
                min="0"
                step="0.01"
                value={taxAmount}
                onChange={(e) => setTaxAmount(e.target.value)}
                required
              />
            </div>
            <div className="app-form-field">
              <label htmlFor="expense-currency">{t("common.currency")}</label>
              <input
                id="expense-currency"
                value={currency}
                onChange={(e) => setCurrency(e.target.value.toUpperCase())}
                maxLength={3}
                required
              />
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="expense-date">{t("expenses.incurredOn")}</label>
              <input
                id="expense-date"
                type="date"
                value={incurredOn}
                onChange={(e) => setIncurredOn(e.target.value)}
                required
              />
            </div>
            <div className="app-form-field" style={{ flex: 1 }}>
              <label htmlFor="expense-description">{t("common.description")}</label>
              <input
                id="expense-description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                required
              />
            </div>
          </div>
          <button type="submit" className="app-button" disabled={isSaving}>
            {isSaving ? t("expenses.recording") : t("expenses.recordExpense")}
          </button>
        </form>
      </div>

      <div className="app-card">
        {listError && <div className="app-error-banner">{listError}</div>}
        {actionError && <div className="app-error-banner">{actionError}</div>}

        {expenses !== null && expenses.length === 0 && (
          <div className="app-empty-state">{t("expenses.empty")}</div>
        )}

        {expenses !== null && expenses.length > 0 && (
          <table className="app-table">
            <thead>
              <tr>
                <th>{t("expenses.colNumber")}</th>
                <th>{t("expenses.category")}</th>
                <th>{t("expenses.colAmount")}</th>
                <th>{t("expenses.colIncurredOn")}</th>
                <th>{t("common.status")}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {expenses.map((expense) => (
                <tr key={expense.id}>
                  <td>{expense.expenseNumber}</td>
                  <td>
                    {t(
                      EXPENSE_CATEGORIES.find((c) => c.value === expense.category)?.labelKey ??
                        "expenses.categoryOther",
                    )}
                  </td>
                  <td>{(expense.amount + expense.taxAmount).toFixed(2)}</td>
                  <td>{expense.incurredOn}</td>
                  <td>
                    <span className={statusBadgeClass(expense.status)}>
                      {t(STATUS_KEY[expense.status] ?? "expenses.statusRecorded")}
                    </span>
                  </td>
                  <td style={{ textAlign: "right", display: "flex", gap: "0.4rem", justifyContent: "flex-end" }}>
                    {expense.status === "Recorded" && (
                      <>
                        <button
                          type="button"
                          className="app-button app-button--ghost"
                          onClick={() => handleApprove(expense.id)}
                        >
                          {t("expenses.approve")}
                        </button>
                        <button
                          type="button"
                          className="app-button app-button--ghost"
                          onClick={() =>
                            setRejectTargetId(rejectTargetId === expense.id ? null : expense.id)
                          }
                        >
                          {rejectTargetId === expense.id ? t("common.cancel") : t("expenses.reject")}
                        </button>
                      </>
                    )}
                  </td>
                </tr>
              ))}
              {expenses
                .filter((expense) => rejectTargetId === expense.id)
                .map((expense) => (
                  <tr key={`${expense.id}-reject`}>
                    <td colSpan={6}>
                      {rejectError && <div className="app-error-banner">{rejectError}</div>}
                      <form
                        onSubmit={(e) => handleReject(expense.id, e)}
                        style={{ display: "flex", gap: "0.5rem", alignItems: "flex-end" }}
                      >
                        <div className="app-form-field" style={{ marginBottom: 0, flex: 1 }}>
                          <label htmlFor={`reject-reason-${expense.id}`}>{t("common.reason")}</label>
                          <input
                            id={`reject-reason-${expense.id}`}
                            value={rejectReason}
                            onChange={(e) => setRejectReason(e.target.value)}
                            required
                          />
                        </div>
                        <button type="submit" className="app-button">
                          {t("expenses.confirmReject")}
                        </button>
                      </form>
                    </td>
                  </tr>
                ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
