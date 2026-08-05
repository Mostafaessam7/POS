import { useState, type FormEvent } from "react";
import { ApiError, apiGet } from "../api/client";
import { useOrganization } from "../api/organization";
import { useLanguage } from "../i18n/LanguageContext";
import type { TranslationKey } from "../i18n/translations";

interface Discrepancy {
  kind: string;
  reference: string;
  detail: string;
  financialImpact: number;
}

interface ReconciliationReport {
  reportName: string;
  businessDate: string;
  recordsExamined: number;
  isClean: boolean;
  netImpact: number;
  currency: string;
  discrepancies: Discrepancy[];
}

interface StockBalanceDivergence {
  variantId: string;
  storedQuantity: number;
  ledgerQuantity: number;
  quantityDifference: number;
  storedValue: number;
  ledgerValue: number;
}

interface StockBalanceReport {
  warehouseId: string;
  isClean: boolean;
  divergences: StockBalanceDivergence[];
}

const DATE_REPORTS: { key: string; labelKey: TranslationKey }[] = [
  { key: "receipt-stock-reconciliation", labelKey: "reconciliation.reportReceiptStock" },
  { key: "supplier-credit-reconciliation", labelKey: "reconciliation.reportSupplierCredit" },
  { key: "sale-fiscal-reconciliation", labelKey: "reconciliation.reportSaleFiscal" },
  { key: "sale-payment-reconciliation", labelKey: "reconciliation.reportSalePayment" },
];

function ReportCard({ report }: { report: ReconciliationReport }) {
  const { t } = useLanguage();

  return (
    <div>
      <p>
        <span className={report.isClean ? "app-badge app-badge--positive" : "app-badge app-badge--negative"}>
          {report.isClean ? t("reconciliation.clean") : t("reconciliation.discrepanciesFound")}
        </span>{" "}
        {report.recordsExamined} {t("reconciliation.recordsExamined")} · {t("reconciliation.netImpact")}{" "}
        {report.netImpact.toFixed(2)} {report.currency}
      </p>
      {report.discrepancies.length > 0 && (
        <table className="app-table">
          <thead>
            <tr>
              <th>{t("reconciliation.colKind")}</th>
              <th>{t("reconciliation.colReference")}</th>
              <th>{t("reconciliation.colDetail")}</th>
              <th>{t("reconciliation.colImpact")}</th>
            </tr>
          </thead>
          <tbody>
            {report.discrepancies.map((d, i) => (
              <tr key={i}>
                <td>{d.kind}</td>
                <td>{d.reference}</td>
                <td>{d.detail}</td>
                <td>
                  {d.financialImpact.toFixed(2)} {report.currency}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

export function ReconciliationPage() {
  const { t } = useLanguage();
  const { companies } = useOrganization();

  const [businessDate, setBusinessDate] = useState(new Date().toISOString().slice(0, 10));
  const [currency, setCurrency] = useState("");
  const [reports, setReports] = useState<Record<string, ReconciliationReport>>({});
  const [reportErrors, setReportErrors] = useState<Record<string, string>>({});
  const [loadingKey, setLoadingKey] = useState<string | null>(null);

  const [warehouseId, setWarehouseId] = useState("");
  const [stockReport, setStockReport] = useState<StockBalanceReport | null>(null);
  const [stockError, setStockError] = useState<string | null>(null);
  const [isLoadingStock, setIsLoadingStock] = useState(false);

  const warehouses = companies?.flatMap((c) =>
    c.branches.flatMap((b) => b.warehouses.map((w) => ({ id: w.id, label: `${c.name} / ${b.name} / ${w.name}` }))),
  );

  async function handleRunDateReports(event: FormEvent) {
    event.preventDefault();

    for (const { key } of DATE_REPORTS) {
      setLoadingKey(key);
      try {
        const params = new URLSearchParams({ businessDate });
        if (currency) params.set("currency", currency);

        const report = await apiGet<ReconciliationReport>(`/api/v1/reports/${key}?${params.toString()}`);
        setReports((current) => ({ ...current, [key]: report }));
        setReportErrors((current) => ({ ...current, [key]: "" }));
      } catch (err) {
        setReportErrors((current) => ({
          ...current,
          [key]: err instanceof ApiError ? err.message : t("reconciliation.loadError"),
        }));
      }
    }
    setLoadingKey(null);
  }

  async function handleRunStockReport(event: FormEvent) {
    event.preventDefault();
    setStockError(null);
    setIsLoadingStock(true);

    try {
      const report = await apiGet<StockBalanceReport>(
        `/api/v1/reports/stock-balance-reconciliation?warehouseId=${warehouseId}`,
      );
      setStockReport(report);
    } catch (err) {
      setStockError(err instanceof ApiError ? err.message : t("reconciliation.loadError"));
    } finally {
      setIsLoadingStock(false);
    }
  }

  return (
    <div>
      <div className="app-page-header">
        <h1>{t("reconciliation.title")}</h1>
      </div>

      <div className="app-card">
        <h2 className="app-card__title">{t("reconciliation.runDateReports")}</h2>
        <form onSubmit={handleRunDateReports}>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="reconciliation-date">{t("reconciliation.businessDate")}</label>
              <input
                id="reconciliation-date"
                type="date"
                value={businessDate}
                onChange={(e) => setBusinessDate(e.target.value)}
                required
              />
            </div>
            <div className="app-form-field">
              <label htmlFor="reconciliation-currency">{t("reconciliation.currencyOptional")}</label>
              <input
                id="reconciliation-currency"
                value={currency}
                onChange={(e) => setCurrency(e.target.value.toUpperCase())}
                maxLength={3}
                placeholder="USD"
              />
            </div>
          </div>
          <button type="submit" className="app-button" disabled={loadingKey !== null}>
            {loadingKey !== null ? t("reconciliation.running") : t("reconciliation.runReports")}
          </button>
        </form>
      </div>

      {DATE_REPORTS.map(({ key, labelKey }) => (
        <div className="app-card" key={key}>
          <h2 className="app-card__title">{t(labelKey)}</h2>
          {reportErrors[key] && <div className="app-error-banner">{reportErrors[key]}</div>}
          {reports[key] ? (
            <ReportCard report={reports[key]} />
          ) : (
            <div className="app-empty-state">{t("reconciliation.runHint")}</div>
          )}
        </div>
      ))}

      <div className="app-card">
        <h2 className="app-card__title">{t("reconciliation.stockVsLedger")}</h2>
        {stockError && <div className="app-error-banner">{stockError}</div>}
        <form onSubmit={handleRunStockReport}>
          <div className="app-form-row">
            <div className="app-form-field" style={{ flex: 1 }}>
              <label htmlFor="reconciliation-warehouse">{t("common.warehouse")}</label>
              <select
                id="reconciliation-warehouse"
                value={warehouseId}
                onChange={(e) => setWarehouseId(e.target.value)}
                required
              >
                <option value="" disabled>
                  {t("common.selectWarehouse")}
                </option>
                {warehouses?.map((w) => (
                  <option key={w.id} value={w.id}>
                    {w.label}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <button type="submit" className="app-button" disabled={isLoadingStock}>
            {isLoadingStock ? t("reconciliation.running") : t("reconciliation.runReport")}
          </button>
        </form>

        {stockReport && (
          <div style={{ marginTop: "1rem" }}>
            <p>
              <span className={stockReport.isClean ? "app-badge app-badge--positive" : "app-badge app-badge--negative"}>
                {stockReport.isClean ? t("reconciliation.clean") : t("reconciliation.divergencesFound")}
              </span>
            </p>
            {stockReport.divergences.length > 0 && (
              <table className="app-table">
                <thead>
                  <tr>
                    <th>{t("inventory.colVariant")}</th>
                    <th>{t("reconciliation.colStoredQty")}</th>
                    <th>{t("reconciliation.colLedgerQty")}</th>
                    <th>{t("reconciliation.colDifference")}</th>
                    <th>{t("reconciliation.colStoredValue")}</th>
                    <th>{t("reconciliation.colLedgerValue")}</th>
                  </tr>
                </thead>
                <tbody>
                  {stockReport.divergences.map((d) => (
                    <tr key={d.variantId}>
                      <td>{d.variantId}</td>
                      <td>{d.storedQuantity}</td>
                      <td>{d.ledgerQuantity}</td>
                      <td>{d.quantityDifference}</td>
                      <td>{d.storedValue.toFixed(2)}</td>
                      <td>{d.ledgerValue.toFixed(2)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
