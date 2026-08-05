import { Fragment, useEffect, useState, type FormEvent } from "react";
import { ApiError, apiGet, apiPost } from "../api/client";
import { useOrganization } from "../api/organization";
import { useProducts } from "../api/products";
import { useLanguage } from "../i18n/LanguageContext";
import type { TranslationKey } from "../i18n/translations";

interface Supplier {
  id: string;
  code: string;
  name: string;
  currency: string;
  isActive: boolean;
  paymentTermDays: number;
  leadTimeDays: number;
}

interface OrderLine {
  lineNumber: number;
  variantId: string;
  quantityOrdered: number;
  quantityReceived: number;
  outstandingQuantity: number;
  unitPrice: number;
}

interface Order {
  id: string;
  orderNumber: string;
  supplierId: string;
  currency: string;
  status: string;
  totalValue: number;
  expectedDeliveryDate: string;
  lines: OrderLine[];
}

interface Invoice {
  id: string;
  supplierInvoiceNumber: string;
  purchaseOrderId: string;
  status: string;
  netTotal: number;
  blockReason: string | null;
}

interface SupplierReturn {
  id: string;
  returnNumber: string;
  supplierId: string;
  status: string;
  expectedCredit: number;
  creditedAmount: number | null;
  creditNoteNumber: string | null;
}

const RETURN_REASONS: { value: string; labelKey: TranslationKey }[] = [
  { value: "Damaged", labelKey: "purchasing.reasonDamaged" },
  { value: "WrongItem", labelKey: "purchasing.reasonWrongItem" },
  { value: "Overstock", labelKey: "purchasing.reasonOverstock" },
  { value: "Expired", labelKey: "purchasing.reasonExpired" },
  { value: "QualityRejection", labelKey: "purchasing.reasonQualityRejection" },
  { value: "Other", labelKey: "purchasing.reasonOther" },
];

function statusBadgeClass(status: string): string {
  if (["Approved", "Received", "Matched", "Credited", "Paid"].includes(status)) return "app-badge app-badge--positive";
  if (["Cancelled", "Blocked"].includes(status)) return "app-badge app-badge--negative";
  return "app-badge";
}

export function PurchasingPage() {
  const { t } = useLanguage();
  const { companies } = useOrganization();
  const { products } = useProducts();

  const [suppliers, setSuppliers] = useState<Supplier[] | null>(null);
  const [orders, setOrders] = useState<Order[] | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  // Supplier form
  const [supplierCode, setSupplierCode] = useState("");
  const [supplierName, setSupplierName] = useState("");
  const [supplierCurrency, setSupplierCurrency] = useState("USD");
  const [supplierCompanyId, setSupplierCompanyId] = useState("");
  const [supplierError, setSupplierError] = useState<string | null>(null);
  const [isSavingSupplier, setIsSavingSupplier] = useState(false);

  // Order form
  const [orderSupplierId, setOrderSupplierId] = useState("");
  const [orderNumber, setOrderNumber] = useState("");
  const [orderBranchId, setOrderBranchId] = useState("");
  const [orderWarehouseId, setOrderWarehouseId] = useState("");
  const [orderVariantId, setOrderVariantId] = useState("");
  const [orderQuantity, setOrderQuantity] = useState("1");
  const [orderUnitPrice, setOrderUnitPrice] = useState("1.00");
  const [orderError, setOrderError] = useState<string | null>(null);
  const [isSavingOrder, setIsSavingOrder] = useState(false);

  // Invoices
  const [invoices, setInvoices] = useState<Invoice[] | null>(null);
  const [invoiceOrderId, setInvoiceOrderId] = useState("");
  const [invoiceNumber, setInvoiceNumber] = useState("");
  const [invoiceDate, setInvoiceDate] = useState(new Date().toISOString().slice(0, 10));
  const [invoiceDueDate, setInvoiceDueDate] = useState(
    new Date(Date.now() + 30 * 86_400_000).toISOString().slice(0, 10),
  );
  const [invoiceError, setInvoiceError] = useState<string | null>(null);
  const [isSavingInvoice, setIsSavingInvoice] = useState(false);
  const [invoiceActionError, setInvoiceActionError] = useState<string | null>(null);
  const [overrideTargetId, setOverrideTargetId] = useState<string | null>(null);
  const [overrideReason, setOverrideReason] = useState("");

  // Returns
  const [returns, setReturns] = useState<SupplierReturn[] | null>(null);
  const [returnSupplierId, setReturnSupplierId] = useState("");
  const [returnBranchId, setReturnBranchId] = useState("");
  const [returnWarehouseId, setReturnWarehouseId] = useState("");
  const [returnNumber, setReturnNumber] = useState("");
  const [returnReason, setReturnReason] = useState("Damaged");
  const [returnVariantId, setReturnVariantId] = useState("");
  const [returnQuantity, setReturnQuantity] = useState("1");
  const [returnUnitCost, setReturnUnitCost] = useState("1.00");
  const [returnError, setReturnError] = useState<string | null>(null);
  const [isSavingReturn, setIsSavingReturn] = useState(false);
  const [returnActionError, setReturnActionError] = useState<string | null>(null);
  const [creditNoteTargetId, setCreditNoteTargetId] = useState<string | null>(null);
  const [creditNoteNumber, setCreditNoteNumber] = useState("");
  const [creditNoteAmount, setCreditNoteAmount] = useState("0.00");
  const [creditNoteDate, setCreditNoteDate] = useState(new Date().toISOString().slice(0, 10));
  const [creditNoteError, setCreditNoteError] = useState<string | null>(null);

  async function loadSuppliers() {
    try {
      const items = await apiGet<Supplier[]>("/api/v1/purchasing/suppliers");
      setSuppliers(items);
    } catch (err) {
      setListError(err instanceof ApiError ? err.message : t("purchasing.loadSuppliersError"));
    }
  }

  async function loadOrders() {
    try {
      const items = await apiGet<Order[]>("/api/v1/purchasing/orders");
      setOrders(items);
    } catch (err) {
      setListError(err instanceof ApiError ? err.message : t("purchasing.loadOrdersError"));
    }
  }

  async function loadInvoices() {
    try {
      setInvoices(await apiGet<Invoice[]>("/api/v1/purchasing/invoices"));
    } catch (err) {
      setListError(err instanceof ApiError ? err.message : t("purchasing.loadInvoicesError"));
    }
  }

  async function loadReturns() {
    try {
      setReturns(await apiGet<SupplierReturn[]>("/api/v1/purchasing/returns"));
    } catch (err) {
      setListError(err instanceof ApiError ? err.message : t("purchasing.loadReturnsError"));
    }
  }

  useEffect(() => {
    loadSuppliers();
    loadOrders();
    loadInvoices();
    loadReturns();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const selectedCompany = companies?.find((c) => c.id === supplierCompanyId);
  const orderCompany = companies?.find((c) => c.branches.some((b) => b.id === orderBranchId));
  const selectedBranch = companies?.flatMap((c) => c.branches).find((b) => b.id === orderBranchId);
  const invoiceOrder = orders?.find((o) => o.id === invoiceOrderId);
  const returnSelectedBranch = companies?.flatMap((c) => c.branches).find((b) => b.id === returnBranchId);

  async function handleCreateSupplier(event: FormEvent) {
    event.preventDefault();
    setSupplierError(null);
    setIsSavingSupplier(true);

    try {
      await apiPost("/api/v1/purchasing/suppliers", {
        companyId: supplierCompanyId,
        code: supplierCode,
        name: supplierName,
        currency: supplierCurrency,
      });

      setSupplierCode("");
      setSupplierName("");
      await loadSuppliers();
    } catch (err) {
      setSupplierError(err instanceof ApiError ? err.message : t("purchasing.createSupplierError"));
    } finally {
      setIsSavingSupplier(false);
    }
  }

  async function handleRaiseOrder(event: FormEvent) {
    event.preventDefault();
    setOrderError(null);
    setIsSavingOrder(true);

    try {
      await apiPost("/api/v1/purchasing/orders", {
        supplierId: orderSupplierId,
        companyId: orderCompany?.id,
        branchId: orderBranchId,
        warehouseId: orderWarehouseId,
        orderNumber,
        businessDate: new Date().toISOString().slice(0, 10),
        expectedDeliveryDate: new Date(Date.now() + 7 * 86_400_000).toISOString().slice(0, 10),
        lines: [
          {
            variantId: orderVariantId,
            quantity: Number(orderQuantity),
            unitPrice: Number(orderUnitPrice),
          },
        ],
      });

      setOrderNumber("");
      setOrderVariantId("");
      await loadOrders();
    } catch (err) {
      setOrderError(err instanceof ApiError ? err.message : t("purchasing.raiseOrderError"));
    } finally {
      setIsSavingOrder(false);
    }
  }

  async function handleApprove(orderId: string) {
    setActionError(null);
    try {
      await apiPost(`/api/v1/purchasing/orders/${orderId}/approve`);
      await loadOrders();
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : t("purchasing.approveOrderError"));
    }
  }

  async function handleSend(orderId: string) {
    setActionError(null);
    try {
      await apiPost(`/api/v1/purchasing/orders/${orderId}/send`);
      await loadOrders();
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : t("purchasing.sendOrderError"));
    }
  }

  async function handleRecordInvoice(event: FormEvent) {
    event.preventDefault();
    setInvoiceError(null);
    setIsSavingInvoice(true);

    try {
      const order = invoiceOrder;
      await apiPost("/api/v1/purchasing/invoices", {
        companyId: orderCompany?.id ?? companies?.[0]?.id,
        supplierId: order?.supplierId,
        purchaseOrderId: invoiceOrderId,
        supplierInvoiceNumber: invoiceNumber,
        currency: order?.currency ?? "USD",
        invoiceDate,
        dueDate: invoiceDueDate,
        lines: order?.lines.map((line) => ({
          purchaseOrderLineNumber: line.lineNumber,
          variantId: line.variantId,
          quantity: line.quantityOrdered,
          unitPrice: line.unitPrice,
        })) ?? [],
      });

      setInvoiceNumber("");
      await loadInvoices();
    } catch (err) {
      setInvoiceError(err instanceof ApiError ? err.message : t("purchasing.recordInvoiceError"));
    } finally {
      setIsSavingInvoice(false);
    }
  }

  async function handleMatch(invoiceId: string) {
    setInvoiceActionError(null);
    try {
      await apiPost(`/api/v1/purchasing/invoices/${invoiceId}/match`);
      await loadInvoices();
    } catch (err) {
      setInvoiceActionError(err instanceof ApiError ? err.message : t("purchasing.matchInvoiceError"));
    }
  }

  async function handleApproveInvoice(invoiceId: string) {
    setInvoiceActionError(null);
    try {
      await apiPost(`/api/v1/purchasing/invoices/${invoiceId}/approve`);
      await loadInvoices();
    } catch (err) {
      setInvoiceActionError(err instanceof ApiError ? err.message : t("purchasing.approveInvoiceError"));
    }
  }

  async function handleOverrideBlock(invoiceId: string, event: FormEvent) {
    event.preventDefault();
    setInvoiceActionError(null);

    try {
      await apiPost(`/api/v1/purchasing/invoices/${invoiceId}/override-block`, { reason: overrideReason });
      setOverrideTargetId(null);
      setOverrideReason("");
      await loadInvoices();
    } catch (err) {
      setInvoiceActionError(err instanceof ApiError ? err.message : t("purchasing.overrideBlockError"));
    }
  }

  async function handleCreateReturn(event: FormEvent) {
    event.preventDefault();
    setReturnError(null);
    setIsSavingReturn(true);

    try {
      await apiPost("/api/v1/purchasing/returns", {
        supplierId: returnSupplierId,
        branchId: returnBranchId,
        warehouseId: returnWarehouseId,
        returnNumber,
        currency: suppliers?.find((s) => s.id === returnSupplierId)?.currency ?? "USD",
        reason: returnReason,
        businessDate: new Date().toISOString().slice(0, 10),
        lines: [
          {
            variantId: returnVariantId,
            quantity: Number(returnQuantity),
            unitCost: Number(returnUnitCost),
          },
        ],
      });

      setReturnNumber("");
      setReturnVariantId("");
      await loadReturns();
    } catch (err) {
      setReturnError(err instanceof ApiError ? err.message : t("purchasing.createReturnError"));
    } finally {
      setIsSavingReturn(false);
    }
  }

  async function handleDispatch(returnId: string) {
    setReturnActionError(null);
    try {
      await apiPost(`/api/v1/purchasing/returns/${returnId}/dispatch`);
      await loadReturns();
    } catch (err) {
      setReturnActionError(err instanceof ApiError ? err.message : t("purchasing.dispatchReturnError"));
    }
  }

  async function handleRecordCreditNote(returnId: string, event: FormEvent) {
    event.preventDefault();
    setCreditNoteError(null);

    try {
      await apiPost(`/api/v1/purchasing/returns/${returnId}/credit-note`, {
        creditNoteNumber,
        amount: Number(creditNoteAmount),
        creditNoteDate,
      });

      setCreditNoteTargetId(null);
      setCreditNoteNumber("");
      setCreditNoteAmount("0.00");
      await loadReturns();
    } catch (err) {
      setCreditNoteError(err instanceof ApiError ? err.message : t("purchasing.creditNoteError"));
    }
  }

  return (
    <div>
      <div className="app-page-header">
        <h1>{t("purchasing.title")}</h1>
      </div>

      <div className="app-card">
        <h2 className="app-card__title">{t("purchasing.newSupplier")}</h2>
        {supplierError && <div className="app-error-banner">{supplierError}</div>}
        <form onSubmit={handleCreateSupplier}>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="supplier-company">{t("common.company")}</label>
              <select
                id="supplier-company"
                value={supplierCompanyId}
                onChange={(e) => setSupplierCompanyId(e.target.value)}
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
              <label htmlFor="supplier-code">{t("purchasing.code")}</label>
              <input id="supplier-code" value={supplierCode} onChange={(e) => setSupplierCode(e.target.value)} required />
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="supplier-name">{t("common.name")}</label>
              <input id="supplier-name" value={supplierName} onChange={(e) => setSupplierName(e.target.value)} required />
            </div>
            <div className="app-form-field">
              <label htmlFor="supplier-currency">{t("common.currency")}</label>
              <input
                id="supplier-currency"
                value={supplierCurrency}
                onChange={(e) => setSupplierCurrency(e.target.value.toUpperCase())}
                maxLength={3}
                required
              />
            </div>
          </div>
          <button type="submit" className="app-button" disabled={isSavingSupplier || !selectedCompany}>
            {isSavingSupplier ? t("purchasing.creating") : t("purchasing.createSupplier")}
          </button>
        </form>
      </div>

      <div className="app-card">
        {suppliers !== null && suppliers.length === 0 && (
          <div className="app-empty-state">{t("purchasing.suppliersEmpty")}</div>
        )}
        {suppliers !== null && suppliers.length > 0 && (
          <table className="app-table">
            <thead>
              <tr>
                <th>{t("purchasing.colCode")}</th>
                <th>{t("common.name")}</th>
                <th>{t("common.currency")}</th>
                <th>{t("purchasing.colTerms")}</th>
              </tr>
            </thead>
            <tbody>
              {suppliers.map((supplier) => (
                <tr key={supplier.id}>
                  <td>{supplier.code}</td>
                  <td>{supplier.name}</td>
                  <td>{supplier.currency}</td>
                  <td>
                    {t("purchasing.termsLine", { days: supplier.paymentTermDays, lead: supplier.leadTimeDays })}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="app-card">
        <h2 className="app-card__title">{t("purchasing.raiseOrder")}</h2>
        {orderError && <div className="app-error-banner">{orderError}</div>}
        <form onSubmit={handleRaiseOrder}>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="order-supplier">{t("purchasing.supplier")}</label>
              <select
                id="order-supplier"
                value={orderSupplierId}
                onChange={(e) => setOrderSupplierId(e.target.value)}
                required
              >
                <option value="" disabled>
                  {t("purchasing.selectSupplier")}
                </option>
                {suppliers?.map((supplier) => (
                  <option key={supplier.id} value={supplier.id}>
                    {supplier.name} ({supplier.code})
                  </option>
                ))}
              </select>
            </div>
            <div className="app-form-field">
              <label htmlFor="order-number">{t("purchasing.orderNumber")}</label>
              <input id="order-number" value={orderNumber} onChange={(e) => setOrderNumber(e.target.value)} required />
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="order-branch">{t("common.branch")}</label>
              <select
                id="order-branch"
                value={orderBranchId}
                onChange={(e) => {
                  setOrderBranchId(e.target.value);
                  setOrderWarehouseId("");
                }}
                required
              >
                <option value="" disabled>
                  {t("common.selectBranch")}
                </option>
                {companies?.flatMap((company) =>
                  company.branches.map((branch) => (
                    <option key={branch.id} value={branch.id}>
                      {company.name} / {branch.name}
                    </option>
                  )),
                )}
              </select>
            </div>
            <div className="app-form-field">
              <label htmlFor="order-warehouse">{t("common.warehouse")}</label>
              <select
                id="order-warehouse"
                value={orderWarehouseId}
                onChange={(e) => setOrderWarehouseId(e.target.value)}
                required
                disabled={!selectedBranch}
              >
                <option value="" disabled>
                  {t("common.selectWarehouse")}
                </option>
                {selectedBranch?.warehouses.map((warehouse) => (
                  <option key={warehouse.id} value={warehouse.id}>
                    {warehouse.name}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="order-variant">{t("purchasing.product")}</label>
              <select
                id="order-variant"
                value={orderVariantId}
                onChange={(e) => setOrderVariantId(e.target.value)}
                required
              >
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
              <label htmlFor="order-quantity">{t("purchasing.quantity")}</label>
              <input
                id="order-quantity"
                type="number"
                min="1"
                step="1"
                value={orderQuantity}
                onChange={(e) => setOrderQuantity(e.target.value)}
                required
              />
            </div>
            <div className="app-form-field">
              <label htmlFor="order-price">{t("purchasing.unitPrice")}</label>
              <input
                id="order-price"
                type="number"
                min="0"
                step="0.01"
                value={orderUnitPrice}
                onChange={(e) => setOrderUnitPrice(e.target.value)}
                required
              />
            </div>
          </div>
          <button type="submit" className="app-button" disabled={isSavingOrder}>
            {isSavingOrder ? t("purchasing.raising") : t("purchasing.raiseOrder")}
          </button>
        </form>
      </div>

      <div className="app-card">
        {listError && <div className="app-error-banner">{listError}</div>}
        {actionError && <div className="app-error-banner">{actionError}</div>}

        {orders !== null && orders.length === 0 && (
          <div className="app-empty-state">{t("purchasing.ordersEmpty")}</div>
        )}

        {orders !== null && orders.length > 0 && (
          <table className="app-table">
            <thead>
              <tr>
                <th>{t("purchasing.colNumber")}</th>
                <th>{t("common.status")}</th>
                <th>{t("purchasing.colTotal")}</th>
                <th>{t("purchasing.colDelivery")}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {orders.map((order) => (
                <tr key={order.id}>
                  <td>{order.orderNumber}</td>
                  <td>
                    <span className={statusBadgeClass(order.status)}>{order.status}</span>
                  </td>
                  <td>
                    {order.totalValue.toFixed(2)} {order.currency}
                  </td>
                  <td>{order.expectedDeliveryDate}</td>
                  <td style={{ textAlign: "right", display: "flex", gap: "0.4rem", justifyContent: "flex-end" }}>
                    {order.status === "PendingApproval" && (
                      <button
                        type="button"
                        className="app-button app-button--ghost"
                        onClick={() => handleApprove(order.id)}
                      >
                        {t("purchasing.approve")}
                      </button>
                    )}
                    {(order.status === "Approved" || order.status === "Draft") && (
                      <button type="button" className="app-button app-button--ghost" onClick={() => handleSend(order.id)}>
                        {t("purchasing.send")}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="app-card">
        <h2 className="app-card__title">{t("purchasing.recordInvoice")}</h2>
        {invoiceError && <div className="app-error-banner">{invoiceError}</div>}
        <form onSubmit={handleRecordInvoice}>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="invoice-order">{t("purchasing.purchaseOrder")}</label>
              <select
                id="invoice-order"
                value={invoiceOrderId}
                onChange={(e) => setInvoiceOrderId(e.target.value)}
                required
              >
                <option value="" disabled>
                  {t("purchasing.selectOrder")}
                </option>
                {orders?.map((order) => (
                  <option key={order.id} value={order.id}>
                    {order.orderNumber} ({order.status})
                  </option>
                ))}
              </select>
            </div>
            <div className="app-form-field">
              <label htmlFor="invoice-number">{t("purchasing.supplierInvoiceNumber")}</label>
              <input
                id="invoice-number"
                value={invoiceNumber}
                onChange={(e) => setInvoiceNumber(e.target.value)}
                required
              />
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="invoice-date">{t("purchasing.invoiceDate")}</label>
              <input
                id="invoice-date"
                type="date"
                value={invoiceDate}
                onChange={(e) => setInvoiceDate(e.target.value)}
                required
              />
            </div>
            <div className="app-form-field">
              <label htmlFor="invoice-due-date">{t("purchasing.dueDate")}</label>
              <input
                id="invoice-due-date"
                type="date"
                value={invoiceDueDate}
                onChange={(e) => setInvoiceDueDate(e.target.value)}
                required
              />
            </div>
          </div>
          <button type="submit" className="app-button" disabled={isSavingInvoice || !invoiceOrder}>
            {isSavingInvoice ? t("purchasing.recording") : t("purchasing.recordInvoice")}
          </button>
        </form>
      </div>

      <div className="app-card">
        {invoiceActionError && <div className="app-error-banner">{invoiceActionError}</div>}

        {invoices !== null && invoices.length === 0 && (
          <div className="app-empty-state">{t("purchasing.invoicesEmpty")}</div>
        )}

        {invoices !== null && invoices.length > 0 && (
          <table className="app-table">
            <thead>
              <tr>
                <th>{t("purchasing.colNumber")}</th>
                <th>{t("common.status")}</th>
                <th>{t("purchasing.colNetTotal")}</th>
                <th>{t("purchasing.colBlockReason")}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {invoices.map((invoice) => (
                <Fragment key={invoice.id}>
                  <tr>
                    <td>{invoice.supplierInvoiceNumber}</td>
                    <td>
                      <span className={statusBadgeClass(invoice.status)}>{invoice.status}</span>
                    </td>
                    <td>{invoice.netTotal.toFixed(2)}</td>
                    <td>{invoice.blockReason ?? "—"}</td>
                    <td style={{ textAlign: "right", display: "flex", gap: "0.4rem", justifyContent: "flex-end" }}>
                      {invoice.status === "Recorded" && (
                        <button type="button" className="app-button app-button--ghost" onClick={() => handleMatch(invoice.id)}>
                          {t("purchasing.match")}
                        </button>
                      )}
                      {invoice.status === "Matched" && (
                        <button
                          type="button"
                          className="app-button app-button--ghost"
                          onClick={() => handleApproveInvoice(invoice.id)}
                        >
                          {t("purchasing.approve")}
                        </button>
                      )}
                      {invoice.status === "Blocked" && (
                        <button
                          type="button"
                          className="app-button app-button--ghost"
                          onClick={() => setOverrideTargetId(overrideTargetId === invoice.id ? null : invoice.id)}
                        >
                          {overrideTargetId === invoice.id ? t("common.cancel") : t("purchasing.overrideBlock")}
                        </button>
                      )}
                    </td>
                  </tr>
                  {overrideTargetId === invoice.id && (
                    <tr>
                      <td colSpan={5}>
                        <form
                          onSubmit={(e) => handleOverrideBlock(invoice.id, e)}
                          style={{ display: "flex", gap: "0.5rem", alignItems: "flex-end" }}
                        >
                          <div className="app-form-field" style={{ marginBottom: 0, flex: 1 }}>
                            <label htmlFor={`override-reason-${invoice.id}`}>{t("common.reason")}</label>
                            <input
                              id={`override-reason-${invoice.id}`}
                              value={overrideReason}
                              onChange={(e) => setOverrideReason(e.target.value)}
                              required
                            />
                          </div>
                          <button type="submit" className="app-button">
                            {t("purchasing.confirmOverride")}
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

      <div className="app-card">
        <h2 className="app-card__title">{t("purchasing.createReturn")}</h2>
        {returnError && <div className="app-error-banner">{returnError}</div>}
        <form onSubmit={handleCreateReturn}>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="return-supplier">{t("purchasing.supplier")}</label>
              <select
                id="return-supplier"
                value={returnSupplierId}
                onChange={(e) => setReturnSupplierId(e.target.value)}
                required
              >
                <option value="" disabled>
                  {t("purchasing.selectSupplier")}
                </option>
                {suppliers?.map((supplier) => (
                  <option key={supplier.id} value={supplier.id}>
                    {supplier.name} ({supplier.code})
                  </option>
                ))}
              </select>
            </div>
            <div className="app-form-field">
              <label htmlFor="return-number">{t("purchasing.returnNumber")}</label>
              <input id="return-number" value={returnNumber} onChange={(e) => setReturnNumber(e.target.value)} required />
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="return-branch">{t("common.branch")}</label>
              <select
                id="return-branch"
                value={returnBranchId}
                onChange={(e) => {
                  setReturnBranchId(e.target.value);
                  setReturnWarehouseId("");
                }}
                required
              >
                <option value="" disabled>
                  {t("common.selectBranch")}
                </option>
                {companies?.flatMap((company) =>
                  company.branches.map((branch) => (
                    <option key={branch.id} value={branch.id}>
                      {company.name} / {branch.name}
                    </option>
                  )),
                )}
              </select>
            </div>
            <div className="app-form-field">
              <label htmlFor="return-warehouse">{t("common.warehouse")}</label>
              <select
                id="return-warehouse"
                value={returnWarehouseId}
                onChange={(e) => setReturnWarehouseId(e.target.value)}
                required
                disabled={!returnSelectedBranch}
              >
                <option value="" disabled>
                  {t("common.selectWarehouse")}
                </option>
                {returnSelectedBranch?.warehouses.map((warehouse) => (
                  <option key={warehouse.id} value={warehouse.id}>
                    {warehouse.name}
                  </option>
                ))}
              </select>
            </div>
            <div className="app-form-field">
              <label htmlFor="return-reason">{t("common.reason")}</label>
              <select id="return-reason" value={returnReason} onChange={(e) => setReturnReason(e.target.value)}>
                {RETURN_REASONS.map((reason) => (
                  <option key={reason.value} value={reason.value}>
                    {t(reason.labelKey)}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <div className="app-form-row">
            <div className="app-form-field">
              <label htmlFor="return-variant">{t("purchasing.product")}</label>
              <select
                id="return-variant"
                value={returnVariantId}
                onChange={(e) => setReturnVariantId(e.target.value)}
                required
              >
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
              <label htmlFor="return-quantity">{t("purchasing.quantity")}</label>
              <input
                id="return-quantity"
                type="number"
                min="1"
                step="1"
                value={returnQuantity}
                onChange={(e) => setReturnQuantity(e.target.value)}
                required
              />
            </div>
            <div className="app-form-field">
              <label htmlFor="return-unit-cost">{t("purchasing.unitCost")}</label>
              <input
                id="return-unit-cost"
                type="number"
                min="0"
                step="0.01"
                value={returnUnitCost}
                onChange={(e) => setReturnUnitCost(e.target.value)}
                required
              />
            </div>
          </div>
          <button type="submit" className="app-button" disabled={isSavingReturn}>
            {isSavingReturn ? t("purchasing.creating") : t("purchasing.createReturn")}
          </button>
        </form>
      </div>

      <div className="app-card">
        {returnActionError && <div className="app-error-banner">{returnActionError}</div>}

        {returns !== null && returns.length === 0 && (
          <div className="app-empty-state">{t("purchasing.returnsEmpty")}</div>
        )}

        {returns !== null && returns.length > 0 && (
          <table className="app-table">
            <thead>
              <tr>
                <th>{t("purchasing.colNumber")}</th>
                <th>{t("common.status")}</th>
                <th>{t("purchasing.colExpectedCredit")}</th>
                <th>{t("purchasing.colCredited")}</th>
                <th>{t("purchasing.colCreditNote")}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {returns.map((supplierReturn) => (
                <Fragment key={supplierReturn.id}>
                  <tr>
                    <td>{supplierReturn.returnNumber}</td>
                    <td>
                      <span className={statusBadgeClass(supplierReturn.status)}>{supplierReturn.status}</span>
                    </td>
                    <td>{supplierReturn.expectedCredit.toFixed(2)}</td>
                    <td>{supplierReturn.creditedAmount?.toFixed(2) ?? "—"}</td>
                    <td>{supplierReturn.creditNoteNumber ?? "—"}</td>
                    <td style={{ textAlign: "right", display: "flex", gap: "0.4rem", justifyContent: "flex-end" }}>
                      {supplierReturn.status === "Draft" && (
                        <button
                          type="button"
                          className="app-button app-button--ghost"
                          onClick={() => handleDispatch(supplierReturn.id)}
                        >
                          {t("purchasing.dispatch")}
                        </button>
                      )}
                      {(supplierReturn.status === "Dispatched" || supplierReturn.status === "PartiallyCredited") && (
                        <button
                          type="button"
                          className="app-button app-button--ghost"
                          onClick={() =>
                            setCreditNoteTargetId(creditNoteTargetId === supplierReturn.id ? null : supplierReturn.id)
                          }
                        >
                          {creditNoteTargetId === supplierReturn.id ? t("common.cancel") : t("purchasing.recordCreditNote")}
                        </button>
                      )}
                    </td>
                  </tr>
                  {creditNoteTargetId === supplierReturn.id && (
                    <tr>
                      <td colSpan={6}>
                        {creditNoteError && <div className="app-error-banner">{creditNoteError}</div>}
                        <form
                          onSubmit={(e) => handleRecordCreditNote(supplierReturn.id, e)}
                          style={{ display: "flex", gap: "0.5rem", alignItems: "flex-end" }}
                        >
                          <div className="app-form-field" style={{ marginBottom: 0, flex: 1 }}>
                            <label htmlFor={`credit-note-number-${supplierReturn.id}`}>
                              {t("purchasing.creditNoteNumber")}
                            </label>
                            <input
                              id={`credit-note-number-${supplierReturn.id}`}
                              value={creditNoteNumber}
                              onChange={(e) => setCreditNoteNumber(e.target.value)}
                              required
                            />
                          </div>
                          <div className="app-form-field" style={{ marginBottom: 0 }}>
                            <label htmlFor={`credit-note-amount-${supplierReturn.id}`}>{t("expenses.amount")}</label>
                            <input
                              id={`credit-note-amount-${supplierReturn.id}`}
                              type="number"
                              min="0"
                              step="0.01"
                              value={creditNoteAmount}
                              onChange={(e) => setCreditNoteAmount(e.target.value)}
                              required
                            />
                          </div>
                          <div className="app-form-field" style={{ marginBottom: 0 }}>
                            <label htmlFor={`credit-note-date-${supplierReturn.id}`}>{t("purchasing.date")}</label>
                            <input
                              id={`credit-note-date-${supplierReturn.id}`}
                              type="date"
                              value={creditNoteDate}
                              onChange={(e) => setCreditNoteDate(e.target.value)}
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
