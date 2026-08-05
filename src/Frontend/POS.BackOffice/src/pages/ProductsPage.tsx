import { Fragment, useEffect, useState, type FormEvent } from "react";
import { ApiError, apiDelete, apiGet, apiPost, apiPut } from "../api/client";
import { useLanguage } from "../i18n/LanguageContext";

interface ProductSummary {
  id: string;
  name: string;
}

interface ProductListResponse {
  items: ProductSummary[];
}

export function ProductsPage() {
  const { t } = useLanguage();

  const [products, setProducts] = useState<ProductSummary[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [name, setName] = useState("");
  const [sku, setSku] = useState("");
  const [price, setPrice] = useState("1.00");
  const [currency, setCurrency] = useState("USD");
  const [formError, setFormError] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState(false);

  const [barcodeTargetId, setBarcodeTargetId] = useState<string | null>(null);
  const [barcodeValue, setBarcodeValue] = useState("");
  const [barcodeError, setBarcodeError] = useState<string | null>(null);

  const [editTargetId, setEditTargetId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editError, setEditError] = useState<string | null>(null);
  const [isSavingEdit, setIsSavingEdit] = useState(false);

  const [actionError, setActionError] = useState<string | null>(null);
  const [deactivatingId, setDeactivatingId] = useState<string | null>(null);

  async function loadProducts() {
    try {
      const response = await apiGet<ProductListResponse>("/api/v1/catalog/products");
      setProducts(response.items);
      setLoadError(null);
    } catch (err) {
      setLoadError(err instanceof ApiError ? err.message : t("products.loadError"));
    }
  }

  useEffect(() => {
    loadProducts();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function handleCreate(event: FormEvent) {
    event.preventDefault();
    setFormError(null);
    setIsSaving(true);

    try {
      await apiPost("/api/v1/catalog/products", {
        name,
        sku,
        price: Number(price),
        currency,
      });

      setName("");
      setSku("");
      setPrice("1.00");
      await loadProducts();
    } catch (err) {
      setFormError(err instanceof ApiError ? err.message : t("products.createError"));
    } finally {
      setIsSaving(false);
    }
  }

  async function handleRename(productId: string, event: FormEvent) {
    event.preventDefault();
    setEditError(null);
    setIsSavingEdit(true);

    try {
      await apiPut(`/api/v1/catalog/products/${productId}`, { name: editName });
      setEditTargetId(null);
      await loadProducts();
    } catch (err) {
      setEditError(err instanceof ApiError ? err.message : t("products.renameError"));
    } finally {
      setIsSavingEdit(false);
    }
  }

  async function handleDeactivate(productId: string) {
    setActionError(null);
    setDeactivatingId(productId);

    try {
      await apiDelete(`/api/v1/catalog/products/${productId}`);
      await loadProducts();
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : t("products.deactivateError"));
    } finally {
      setDeactivatingId(null);
    }
  }

  async function handleAddBarcode(productId: string, event: FormEvent) {
    event.preventDefault();
    setBarcodeError(null);

    try {
      await apiPost(`/api/v1/catalog/products/${productId}/barcodes`, {
        value: barcodeValue,
        symbology: "Ean13",
      });

      setBarcodeTargetId(null);
      setBarcodeValue("");
    } catch (err) {
      setBarcodeError(err instanceof ApiError ? err.message : t("products.barcodeError"));
    }
  }

  return (
    <div>
      <div className="app-page-header">
        <h1>{t("products.title")}</h1>
      </div>

      <div className="app-card">
        <h2 className="app-card__title">{t("products.newProduct")}</h2>
        {formError && <div className="app-error-banner">{formError}</div>}
        <form onSubmit={handleCreate}>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="product-name">{t("common.name")}</label>
              <input id="product-name" value={name} onChange={(e) => setName(e.target.value)} required />
            </div>
            <div className="app-form-field">
              <label htmlFor="product-sku">{t("products.sku")}</label>
              <input id="product-sku" value={sku} onChange={(e) => setSku(e.target.value)} required />
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="product-price">{t("products.price")}</label>
              <input
                id="product-price"
                type="number"
                min="0"
                step="0.01"
                value={price}
                onChange={(e) => setPrice(e.target.value)}
                required
              />
            </div>
            <div className="app-form-field">
              <label htmlFor="product-currency">{t("common.currency")}</label>
              <input
                id="product-currency"
                value={currency}
                onChange={(e) => setCurrency(e.target.value.toUpperCase())}
                maxLength={3}
                required
              />
            </div>
          </div>
          <button type="submit" className="app-button" disabled={isSaving}>
            {isSaving ? t("products.creating") : t("products.createProduct")}
          </button>
        </form>
      </div>

      <div className="app-card">
        {loadError && <div className="app-error-banner">{loadError}</div>}
        {actionError && <div className="app-error-banner">{actionError}</div>}

        {products === null && !loadError && <div className="app-empty-state">{t("common.loading")}</div>}

        {products !== null && products.length === 0 && (
          <div className="app-empty-state">{t("products.empty")}</div>
        )}

        {products !== null && products.length > 0 && (
          <table className="app-table">
            <thead>
              <tr>
                <th>{t("products.colName")}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {products.map((product) => (
                <Fragment key={product.id}>
                  <tr>
                    <td>{product.name}</td>
                    <td style={{ textAlign: "right", display: "flex", gap: "0.4rem", justifyContent: "flex-end" }}>
                      <button
                        type="button"
                        className="app-button app-button--ghost"
                        onClick={() => {
                          setEditTargetId(editTargetId === product.id ? null : product.id);
                          setEditName(product.name);
                          setEditError(null);
                        }}
                      >
                        {editTargetId === product.id ? t("common.cancel") : t("common.edit")}
                      </button>
                      <button
                        type="button"
                        className="app-button app-button--ghost"
                        onClick={() =>
                          setBarcodeTargetId(barcodeTargetId === product.id ? null : product.id)
                        }
                      >
                        {barcodeTargetId === product.id ? t("common.cancel") : t("products.addBarcode")}
                      </button>
                      <button
                        type="button"
                        className="app-button app-button--ghost"
                        disabled={deactivatingId === product.id}
                        onClick={() => handleDeactivate(product.id)}
                      >
                        {deactivatingId === product.id ? t("products.deactivating") : t("products.deactivate")}
                      </button>
                    </td>
                  </tr>
                  {editTargetId === product.id && (
                    <tr>
                      <td colSpan={2}>
                        {editError && <div className="app-error-banner">{editError}</div>}
                        <form
                          onSubmit={(e) => handleRename(product.id, e)}
                          style={{ display: "flex", gap: "0.5rem", alignItems: "flex-end" }}
                        >
                          <div className="app-form-field" style={{ marginBottom: 0, flex: 1 }}>
                            <label htmlFor={`edit-name-${product.id}`}>{t("common.name")}</label>
                            <input
                              id={`edit-name-${product.id}`}
                              value={editName}
                              onChange={(e) => setEditName(e.target.value)}
                              required
                            />
                          </div>
                          <button type="submit" className="app-button" disabled={isSavingEdit}>
                            {isSavingEdit ? t("common.saving") : t("common.save")}
                          </button>
                        </form>
                      </td>
                    </tr>
                  )}
                  {barcodeTargetId === product.id && (
                    <tr>
                      <td colSpan={2}>
                        {barcodeError && <div className="app-error-banner">{barcodeError}</div>}
                        <form
                          onSubmit={(e) => handleAddBarcode(product.id, e)}
                          style={{ display: "flex", gap: "0.5rem", alignItems: "flex-end" }}
                        >
                          <div className="app-form-field" style={{ marginBottom: 0, flex: 1 }}>
                            <label htmlFor={`barcode-${product.id}`}>{t("products.barcodeLabel")}</label>
                            <input
                              id={`barcode-${product.id}`}
                              value={barcodeValue}
                              onChange={(e) => setBarcodeValue(e.target.value)}
                              placeholder="5901234123457"
                              required
                            />
                          </div>
                          <button type="submit" className="app-button">
                            {t("common.save")}
                          </button>
                        </form>
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
