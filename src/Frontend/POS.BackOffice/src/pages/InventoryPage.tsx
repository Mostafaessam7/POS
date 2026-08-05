import { useEffect, useState, type FormEvent } from "react";
import { ApiError, apiGet, apiPost } from "../api/client";
import { useOrganization } from "../api/organization";
import { useProducts } from "../api/products";
import { useLanguage } from "../i18n/LanguageContext";

interface StockBalance {
  warehouseId: string;
  variantId: string;
  quantityOnHand: number;
  averageUnitCost: number;
  totalValue: number;
  currency: string;
  isNegative: boolean;
  lastMovementAt: string;
}

export function InventoryPage() {
  const { t } = useLanguage();
  const { companies } = useOrganization();
  const { products } = useProducts();
  const warehouses = companies?.flatMap((c) => c.branches.flatMap((b) => b.warehouses.map((w) => ({ ...w, companyName: c.name, branchName: b.name })))) ?? [];

  const [warehouseId, setWarehouseId] = useState("");
  const [balances, setBalances] = useState<StockBalance[] | null>(null);
  const [listError, setListError] = useState<string | null>(null);

  const [variantId, setVariantId] = useState("");
  const [kind, setKind] = useState<"Increase" | "Decrease" | "Wastage">("Increase");
  const [quantity, setQuantity] = useState("1");
  const [unitCost, setUnitCost] = useState("1.00");
  const [reasonCode, setReasonCode] = useState("");
  const [adjustmentError, setAdjustmentError] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState(false);

  useEffect(() => {
    if (!warehouseId && warehouses.length > 0) {
      setWarehouseId(warehouses[0].id);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [warehouses, warehouseId]);

  async function loadBalances(forWarehouseId: string) {
    if (!forWarehouseId) return;
    try {
      const items = await apiGet<StockBalance[]>(`/api/v1/inventory/warehouses/${forWarehouseId}/balances`);
      setBalances(items);
      setListError(null);
    } catch (err) {
      setListError(err instanceof ApiError ? err.message : t("inventory.loadError"));
    }
  }

  useEffect(() => {
    loadBalances(warehouseId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [warehouseId]);

  async function handleAdjust(event: FormEvent) {
    event.preventDefault();
    setAdjustmentError(null);
    setIsSaving(true);

    try {
      await apiPost("/api/v1/inventory/adjustments", {
        warehouseId,
        variantId,
        kind,
        quantity: Number(quantity),
        reasonCode,
        businessDate: new Date().toISOString().slice(0, 10),
        ...(kind === "Increase" ? { unitCost: Number(unitCost), currency: "USD" } : {}),
      });

      setVariantId("");
      setReasonCode("");
      await loadBalances(warehouseId);
    } catch (err) {
      setAdjustmentError(err instanceof ApiError ? err.message : t("inventory.adjustError"));
    } finally {
      setIsSaving(false);
    }
  }

  return (
    <div>
      <div className="app-page-header">
        <h1>{t("inventory.title")}</h1>
      </div>

      <div className="app-card">
        <div className="app-form-field" style={{ maxWidth: 320 }}>
          <label htmlFor="warehouse-select">{t("inventory.warehouse")}</label>
          <select id="warehouse-select" value={warehouseId} onChange={(e) => setWarehouseId(e.target.value)}>
            {warehouses.map((warehouse) => (
              <option key={warehouse.id} value={warehouse.id}>
                {warehouse.companyName} / {warehouse.branchName} / {warehouse.name}
              </option>
            ))}
          </select>
        </div>
      </div>

      <div className="app-card">
        <h2 className="app-card__title">{t("inventory.recordAdjustment")}</h2>
        {adjustmentError && <div className="app-error-banner">{adjustmentError}</div>}
        <form onSubmit={handleAdjust}>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="adj-variant">{t("inventory.product")}</label>
              <select id="adj-variant" value={variantId} onChange={(e) => setVariantId(e.target.value)} required>
                <option value="" disabled>
                  {t("common.selectProduct")}
                </option>
                {products?.map((product) => (
                  <option key={product.variantId} value={product.variantId}>
                    {product.name}
                  </option>
                ))}
              </select>
            </div>
            <div className="app-form-field">
              <label htmlFor="adj-kind">{t("inventory.kind")}</label>
              <select id="adj-kind" value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}>
                <option value="Increase">{t("inventory.kindIncrease")}</option>
                <option value="Decrease">{t("inventory.kindDecrease")}</option>
                <option value="Wastage">{t("inventory.kindWastage")}</option>
              </select>
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="adj-quantity">{t("inventory.quantity")}</label>
              <input
                id="adj-quantity"
                type="number"
                min="0.01"
                step="0.01"
                value={quantity}
                onChange={(e) => setQuantity(e.target.value)}
                required
              />
            </div>
            {kind === "Increase" && (
              <div className="app-form-field">
                <label htmlFor="adj-cost">{t("inventory.unitCost")}</label>
                <input
                  id="adj-cost"
                  type="number"
                  min="0"
                  step="0.01"
                  value={unitCost}
                  onChange={(e) => setUnitCost(e.target.value)}
                  required
                />
              </div>
            )}
            <div className="app-form-field">
              <label htmlFor="adj-reason">{t("inventory.reasonCode")}</label>
              <input id="adj-reason" value={reasonCode} onChange={(e) => setReasonCode(e.target.value)} required />
            </div>
          </div>
          <button type="submit" className="app-button" disabled={isSaving || !warehouseId}>
            {isSaving ? t("common.saving") : t("inventory.recordAdjustment")}
          </button>
        </form>
      </div>

      <div className="app-card">
        {listError && <div className="app-error-banner">{listError}</div>}

        {balances !== null && balances.length === 0 && (
          <div className="app-empty-state">{t("inventory.empty")}</div>
        )}

        {balances !== null && balances.length > 0 && (
          <table className="app-table">
            <thead>
              <tr>
                <th>{t("inventory.colVariant")}</th>
                <th>{t("inventory.colOnHand")}</th>
                <th>{t("inventory.colAvgCost")}</th>
                <th>{t("inventory.colTotalValue")}</th>
              </tr>
            </thead>
            <tbody>
              {balances.map((balance) => (
                <tr key={balance.variantId}>
                  <td style={{ fontFamily: "monospace", fontSize: "0.8rem" }}>{balance.variantId}</td>
                  <td>
                    {balance.quantityOnHand}
                    {balance.isNegative && (
                      <span className="app-badge app-badge--negative" style={{ marginLeft: "0.4rem" }}>
                        {t("inventory.negative")}
                      </span>
                    )}
                  </td>
                  <td>
                    {balance.averageUnitCost.toFixed(2)} {balance.currency}
                  </td>
                  <td>
                    {balance.totalValue.toFixed(2)} {balance.currency}
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
