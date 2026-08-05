import { useEffect, useState, type FormEvent } from "react";
import { ApiError, apiGet, apiPut } from "../api/client";
import { useLanguage } from "../i18n/LanguageContext";

interface ApprovalThreshold {
  fromValue: number;
  level: string;
}

interface PurchasingPolicy {
  approvalRequiredAbove: number;
  allowSelfApproval: boolean;
  thresholds: ApprovalThreshold[];
  receiptTolerancePercentage: number;
  receiptToleranceUnits: number;
}

interface InventoryPolicy {
  allowSelfApproval: boolean;
  varianceWriteOffThresholds: ApprovalThreshold[];
}

export function SettingsPage() {
  const { t } = useLanguage();

  const [purchasing, setPurchasing] = useState<PurchasingPolicy | null>(null);
  const [inventory, setInventory] = useState<InventoryPolicy | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [purchasingError, setPurchasingError] = useState<string | null>(null);
  const [purchasingSaved, setPurchasingSaved] = useState(false);
  const [isSavingPurchasing, setIsSavingPurchasing] = useState(false);

  const [inventoryError, setInventoryError] = useState<string | null>(null);
  const [inventorySaved, setInventorySaved] = useState(false);
  const [isSavingInventory, setIsSavingInventory] = useState(false);

  useEffect(() => {
    Promise.all([
      apiGet<PurchasingPolicy>("/api/v1/settings/purchasing-policy"),
      apiGet<InventoryPolicy>("/api/v1/settings/inventory-policy"),
    ])
      .then(([p, i]) => {
        setPurchasing(p);
        setInventory(i);
      })
      .catch((err) => setLoadError(err instanceof ApiError ? err.message : t("settings.loadError")));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function handleSavePurchasing(event: FormEvent) {
    event.preventDefault();
    if (!purchasing) return;

    setPurchasingError(null);
    setPurchasingSaved(false);
    setIsSavingPurchasing(true);

    try {
      const saved = await apiPut<PurchasingPolicy>("/api/v1/settings/purchasing-policy", purchasing);
      setPurchasing(saved);
      setPurchasingSaved(true);
    } catch (err) {
      setPurchasingError(err instanceof ApiError ? err.message : "Failed to save purchasing policy.");
    } finally {
      setIsSavingPurchasing(false);
    }
  }

  async function handleSaveInventory(event: FormEvent) {
    event.preventDefault();
    if (!inventory) return;

    setInventoryError(null);
    setInventorySaved(false);
    setIsSavingInventory(true);

    try {
      const saved = await apiPut<InventoryPolicy>("/api/v1/settings/inventory-policy", inventory);
      setInventory(saved);
      setInventorySaved(true);
    } catch (err) {
      setInventoryError(err instanceof ApiError ? err.message : "Failed to save inventory policy.");
    } finally {
      setIsSavingInventory(false);
    }
  }

  return (
    <div>
      <div className="app-page-header">
        <h1>{t("settings.title")}</h1>
      </div>

      {loadError && (
        <div className="app-card app-card--error">
          <div className="app-error-banner" style={{ marginBottom: 0 }}>
            {loadError}
          </div>
          <p className="app-card__hint">{t("settings.loadErrorHint")}</p>
        </div>
      )}

      {purchasing && (
        <div className="app-card">
          <h2 className="app-card__title">{t("settings.purchasingTitle")}</h2>
          <p className="app-card__hint">{t("settings.purchasingDesc")}</p>
          {purchasingError && <div className="app-error-banner">{purchasingError}</div>}
          {purchasingSaved && <div className="app-badge app-badge--positive">{t("settings.saved")}</div>}
          <form onSubmit={handleSavePurchasing} style={{ marginTop: "0.75rem" }}>
            <div className="app-form-row">
              <div className="app-form-field">
                <label htmlFor="approval-above">{t("settings.approvalAbove")}</label>
                <input
                  id="approval-above"
                  type="number"
                  min="0"
                  step="0.01"
                  value={purchasing.approvalRequiredAbove}
                  onChange={(e) =>
                    setPurchasing({ ...purchasing, approvalRequiredAbove: Number(e.target.value) })
                  }
                />
              </div>
              <div className="app-form-field">
                <label htmlFor="receipt-tolerance-pct">{t("settings.receiptTolerancePct")}</label>
                <input
                  id="receipt-tolerance-pct"
                  type="number"
                  min="0"
                  step="0.1"
                  value={purchasing.receiptTolerancePercentage}
                  onChange={(e) =>
                    setPurchasing({ ...purchasing, receiptTolerancePercentage: Number(e.target.value) })
                  }
                />
              </div>
              <div className="app-form-field">
                <label htmlFor="receipt-tolerance-units">{t("settings.receiptToleranceUnits")}</label>
                <input
                  id="receipt-tolerance-units"
                  type="number"
                  min="0"
                  step="1"
                  value={purchasing.receiptToleranceUnits}
                  onChange={(e) =>
                    setPurchasing({ ...purchasing, receiptToleranceUnits: Number(e.target.value) })
                  }
                />
              </div>
            </div>
            <label className="app-checkbox-field">
              <input
                type="checkbox"
                checked={purchasing.allowSelfApproval}
                onChange={(e) => setPurchasing({ ...purchasing, allowSelfApproval: e.target.checked })}
              />
              {t("settings.allowSelfApprovalPurchasing")}
            </label>
            <div style={{ marginTop: "1rem" }}>
              <button type="submit" className="app-button" disabled={isSavingPurchasing}>
                {isSavingPurchasing ? t("settings.saving") : t("settings.savePurchasing")}
              </button>
            </div>
          </form>
        </div>
      )}

      {inventory && (
        <div className="app-card">
          <h2 className="app-card__title">{t("settings.inventoryTitle")}</h2>
          <p className="app-card__hint">{t("settings.inventoryDesc")}</p>
          {inventoryError && <div className="app-error-banner">{inventoryError}</div>}
          {inventorySaved && <div className="app-badge app-badge--positive">{t("settings.saved")}</div>}
          <form onSubmit={handleSaveInventory} style={{ marginTop: "0.75rem" }}>
            <label className="app-checkbox-field">
              <input
                type="checkbox"
                checked={inventory.allowSelfApproval}
                onChange={(e) => setInventory({ ...inventory, allowSelfApproval: e.target.checked })}
              />
              {t("settings.allowSelfApprovalInventory")}
            </label>
            <div style={{ marginTop: "1rem" }}>
              <button type="submit" className="app-button" disabled={isSavingInventory}>
                {isSavingInventory ? t("settings.saving") : t("settings.saveInventory")}
              </button>
            </div>
          </form>
        </div>
      )}
    </div>
  );
}
