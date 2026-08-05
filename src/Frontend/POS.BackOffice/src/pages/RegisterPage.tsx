import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { Link } from "react-router-dom";
import { ApiError, apiGet, apiPost } from "../api/client";
import { getTerminalId } from "../api/terminal";
import { useOrganization } from "../api/organization";
import { useAuth } from "../auth/AuthContext";
import { useLanguage } from "../i18n/LanguageContext";
import type { TranslationKey } from "../i18n/translations";

interface RegisterProduct {
  variantId: string;
  productId: string;
  sku: string;
  name: string;
  price: number;
  currency: string;
  categoryId: string;
  categoryName: string;
  taxRate: number;
  taxInclusive: boolean;
  taxCode: string;
  unitOfMeasure: string;
}

interface Shift {
  id: string;
  branchId: string;
  terminalId: string;
  currency: string;
  status: "Open" | "Closed";
  openedAt: string;
}

interface CartLine {
  variantId: string;
  name: string;
  price: number;
  currency: string;
  taxRate: number;
  taxInclusive: boolean;
  quantity: number;
  discount: number;
}

interface ReceiptLine {
  description: string;
  quantity: number;
  gross: number;
}

interface SaleReceipt {
  id: string;
  receiptNumber: string;
  totalInclusiveTax: number;
  currency: string;
  amountTendered: number;
  changeGiven: number;
  lines: ReceiptLine[];
}

interface SaleSummary {
  id: string;
  receiptNumber: string;
  businessDate: string;
  completedAt: string | null;
  status: "Open" | "Suspended" | "Completed" | "Cancelled" | "Voided";
  totalInclusiveTax: number;
  currency: string;
}

type PaymentMethod = "Cash" | "Card";

function lineTotals(price: number, quantity: number, taxRate: number, taxInclusive: boolean, discount = 0) {
  const extended = Math.max(0, price * quantity - discount);
  if (taxInclusive) {
    const net = extended / (1 + taxRate);
    return { net, tax: extended - net, gross: extended };
  }
  const tax = extended * taxRate;
  return { net: extended, tax, gross: extended + tax };
}

/** Rounds up to the next whole unit, then offers a couple of round-number steps above it. */
function quickCashAmounts(total: number): number[] {
  if (total <= 0) return [];
  const exact = Math.round(total * 100) / 100;
  const roundedUp = Math.ceil(exact);
  const steps = [roundedUp, roundedUp + 5, roundedUp + 10, roundedUp + 20]
    .map((n) => Math.ceil(n / 5) * 5)
    .filter((n, i, arr) => arr.indexOf(n) === i);
  const amounts = [exact, ...steps].filter((n, i, arr) => arr.indexOf(n) === i);
  return amounts.slice(0, 4);
}

function statusLabel(status: SaleSummary["status"], t: (key: TranslationKey, vars?: Record<string, string | number>) => string): string {
  switch (status) {
    case "Completed":
      return t("register.statusCompleted");
    case "Suspended":
      return t("register.statusSuspended");
    case "Voided":
      return t("register.statusVoided");
    case "Cancelled":
      return t("register.statusCancelled");
    default:
      return status;
  }
}

function useElapsed(since: string | undefined): string {
  const [, setTick] = useState(0);

  useEffect(() => {
    const interval = setInterval(() => setTick((n) => n + 1), 1000);
    return () => clearInterval(interval);
  }, []);

  if (!since) return "";
  const ms = Date.now() - new Date(since).getTime();
  const totalMinutes = Math.max(0, Math.floor(ms / 60000));
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  return hours > 0 ? `${hours}h ${minutes}m` : `${minutes}m`;
}

export function RegisterPage() {
  const { t } = useLanguage();
  const { session } = useAuth();
  const { companies } = useOrganization();

  const terminalId = useMemo(() => getTerminalId(), []);
  const branch = companies?.[0]?.branches?.[0];
  const warehouse = branch?.warehouses?.[0];
  const company = companies?.[0];

  const [products, setProducts] = useState<RegisterProduct[] | null>(null);
  const [shift, setShift] = useState<Shift | null | undefined>(undefined);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [openingFloat, setOpeningFloat] = useState("100.00");
  const [isOpeningShift, setIsOpeningShift] = useState(false);
  const [openShiftError, setOpenShiftError] = useState<string | null>(null);

  const [closingCash, setClosingCash] = useState("");
  const [isClosingShift, setIsClosingShift] = useState(false);
  const [closeShiftError, setCloseShiftError] = useState<string | null>(null);
  const [closeShiftShowing, setCloseShiftShowing] = useState(false);
  const [closeResult, setCloseResult] = useState<string | null>(null);

  const [search, setSearch] = useState("");
  const [category, setCategory] = useState<string | null>(null);
  const [cart, setCart] = useState<CartLine[]>([]);
  const [orderDiscountPercent, setOrderDiscountPercent] = useState("");
  const [paymentMethod, setPaymentMethod] = useState<PaymentMethod>("Cash");
  const [cashTendered, setCashTendered] = useState("");
  const [isCharging, setIsCharging] = useState(false);
  const [checkoutError, setCheckoutError] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<SaleReceipt | null>(null);

  const [resumingSale, setResumingSale] = useState<{ id: string; receiptNumber: string } | null>(null);

  const [isHolding, setIsHolding] = useState(false);
  const [holdError, setHoldError] = useState<string | null>(null);

  const [heldSalesShowing, setHeldSalesShowing] = useState(false);
  const [heldSales, setHeldSales] = useState<SaleSummary[] | null>(null);
  const [isResuming, setIsResuming] = useState(false);
  const [resumeError, setResumeError] = useState<string | null>(null);

  const [recentSalesShowing, setRecentSalesShowing] = useState(false);
  const [recentSales, setRecentSales] = useState<SaleSummary[] | null>(null);
  const [voidingSaleId, setVoidingSaleId] = useState<string | null>(null);
  const [voidReason, setVoidReason] = useState("");
  const [voidError, setVoidError] = useState<string | null>(null);

  const searchRef = useRef<HTMLInputElement>(null);
  const elapsed = useElapsed(shift?.openedAt);

  useEffect(() => {
    apiGet<{ items: RegisterProduct[] }>("/api/v1/sales/register-products")
      .then((response) => setProducts(response.items))
      .catch((err) => setLoadError(err instanceof ApiError ? err.message : t("register.loadError")));

    apiGet<Shift>(`/api/v1/sales/shifts/current?terminalId=${terminalId}`)
      .then((s) => setShift(s))
      .catch(() => setShift(null));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [terminalId]);

  const categories = useMemo(() => {
    if (!products) return [];
    const seen = new Map<string, string>();
    for (const p of products) seen.set(p.categoryId, p.categoryName);
    return [...seen.entries()];
  }, [products]);

  const filteredProducts = useMemo(() => {
    if (!products) return [];
    const q = search.trim().toLowerCase();
    return products.filter((p) => {
      const matchesCategory = !category || p.categoryId === category;
      const matchesSearch = !q || p.name.toLowerCase().includes(q) || p.sku.toLowerCase().includes(q);
      return matchesCategory && matchesSearch;
    });
  }, [products, search, category]);

  const totals = useMemo(() => {
    const zero = { net: 0, tax: 0, gross: 0 };
    return cart.reduce((sum, line) => {
      const lt = lineTotals(line.price, line.quantity, line.taxRate, line.taxInclusive, line.discount);
      return { net: sum.net + lt.net, tax: sum.tax + lt.tax, gross: sum.gross + lt.gross };
    }, zero);
  }, [cart]);

  const orderDiscountPercentNumber = Math.min(100, Math.max(0, Number(orderDiscountPercent) || 0));
  const orderDiscountFactor = 1 - orderDiscountPercentNumber / 100;

  const grandTotals = useMemo(
    () => ({
      net: totals.net * orderDiscountFactor,
      tax: totals.tax * orderDiscountFactor,
      gross: totals.gross * orderDiscountFactor,
    }),
    [totals, orderDiscountFactor],
  );

  const cartCount = cart.reduce((n, l) => n + l.quantity, 0);
  const tenderedNumber = Number(cashTendered) || 0;
  const changePreview = tenderedNumber > grandTotals.gross ? tenderedNumber - grandTotals.gross : 0;

  function addToCart(product: RegisterProduct) {
    if (resumingSale) return;
    setCart((current) => {
      const existing = current.find((l) => l.variantId === product.variantId);
      if (existing) {
        return current.map((l) => (l.variantId === product.variantId ? { ...l, quantity: l.quantity + 1 } : l));
      }
      return [
        ...current,
        {
          variantId: product.variantId,
          name: product.name,
          price: product.price,
          currency: product.currency,
          taxRate: product.taxRate,
          taxInclusive: product.taxInclusive,
          quantity: 1,
          discount: 0,
        },
      ];
    });
  }

  function updateQuantity(variantId: string, quantity: number) {
    if (quantity <= 0) {
      setCart((current) => current.filter((l) => l.variantId !== variantId));
      return;
    }
    setCart((current) => current.map((l) => (l.variantId === variantId ? { ...l, quantity } : l)));
  }

  function updateDiscount(variantId: string, discount: number) {
    setCart((current) =>
      current.map((l) => (l.variantId === variantId ? { ...l, discount: Math.max(0, discount) } : l)),
    );
  }

  function clearCart() {
    setCart([]);
    setCashTendered("");
    setCheckoutError(null);
    setOrderDiscountPercent("");
  }

  /** A barcode scanner types the code and sends Enter — this is the scan-and-go path. */
  function handleSearchKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key !== "Enter") return;
    event.preventDefault();

    const q = search.trim().toLowerCase();
    if (!q) return;

    const exactSku = products?.find((p) => p.sku.toLowerCase() === q);
    const match = exactSku ?? (filteredProducts.length === 1 ? filteredProducts[0] : null);

    if (match) {
      addToCart(match);
      setSearch("");
    }
  }

  async function handleOpenShift() {
    if (!branch) return;
    setOpenShiftError(null);
    setIsOpeningShift(true);

    try {
      const opened = await apiPost<Shift>("/api/v1/sales/shifts", {
        branchId: branch.id,
        terminalId,
        openingFloat: Number(openingFloat),
        currency: "USD",
      });
      setShift(opened);
    } catch (err) {
      setOpenShiftError(err instanceof ApiError ? err.message : t("register.loadError"));
    } finally {
      setIsOpeningShift(false);
    }
  }

  async function handleCloseShift() {
    if (!shift) return;
    setCloseShiftError(null);
    setIsClosingShift(true);

    try {
      const closed = await apiPost<{ variance: number; currency: string }>(
        `/api/v1/sales/shifts/${shift.id}/close`,
        { countedCash: Number(closingCash) },
      );
      setCloseResult(`${t("register.shiftCloseSuccess")}: ${closed.variance.toFixed(2)} ${closed.currency}`);
      setShift(null);
      setCloseShiftShowing(false);
      setClosingCash("");
    } catch (err) {
      setCloseShiftError(err instanceof ApiError ? err.message : t("register.loadError"));
    } finally {
      setIsClosingShift(false);
    }
  }

  async function handleCheckout() {
    if (!shift || !company || !branch || !warehouse) return;

    setCheckoutError(null);

    const tendered = paymentMethod === "Card" ? grandTotals.gross : Number(cashTendered);
    if (!tendered || tendered < grandTotals.gross) {
      setCheckoutError(t("register.insufficientTender"));
      return;
    }

    setIsCharging(true);

    try {
      const sale = resumingSale
        ? await apiPost<SaleReceipt>(`/api/v1/sales/${resumingSale.id}/complete-held`, {
            warehouseId: warehouse.id,
            tenders: [{ method: paymentMethod, amount: tendered }],
          })
        : await apiPost<SaleReceipt>("/api/v1/sales", {
            companyId: company.id,
            branchId: branch.id,
            terminalId,
            shiftId: shift.id,
            warehouseId: warehouse.id,
            currency: "USD",
            lines: cart.map((l) => ({ variantId: l.variantId, quantity: l.quantity, discountAmount: l.discount || undefined })),
            tenders: [{ method: paymentMethod, amount: tendered }],
            orderDiscountPercent: orderDiscountPercentNumber || undefined,
          });

      setReceipt(sale);
      setCart([]);
      setCashTendered("");
      setOrderDiscountPercent("");
      setResumingSale(null);
    } catch (err) {
      setCheckoutError(err instanceof ApiError ? err.message : t("register.checkoutError"));
    } finally {
      setIsCharging(false);
    }
  }

  async function handleHold() {
    if (!shift || !company || !branch || !warehouse || cart.length === 0) return;

    setHoldError(null);
    setIsHolding(true);

    try {
      await apiPost("/api/v1/sales/hold", {
        companyId: company.id,
        branchId: branch.id,
        terminalId,
        shiftId: shift.id,
        warehouseId: warehouse.id,
        currency: "USD",
        lines: cart.map((l) => ({ variantId: l.variantId, quantity: l.quantity, discountAmount: l.discount || undefined })),
        orderDiscountPercent: orderDiscountPercentNumber || undefined,
      });

      setCart([]);
      setOrderDiscountPercent("");
      setCashTendered("");
    } catch (err) {
      setHoldError(err instanceof ApiError ? err.message : t("register.holdError"));
    } finally {
      setIsHolding(false);
    }
  }

  async function loadHeldSales() {
    if (!shift) return;
    try {
      const response = await apiGet<{ items: SaleSummary[] }>(`/api/v1/sales/held?shiftId=${shift.id}`);
      setHeldSales(response.items);
    } catch (err) {
      setResumeError(err instanceof ApiError ? err.message : t("register.resumeError"));
    }
  }

  function toggleHeldSales() {
    setHeldSalesShowing((v) => {
      const next = !v;
      if (next) loadHeldSales();
      return next;
    });
  }

  async function handleResume(sale: SaleSummary) {
    setResumeError(null);
    setIsResuming(true);

    try {
      const detail = await apiPost<{
        id: string;
        receiptNumber: string;
        currency: string;
        lines: { variantId: string; description: string; quantity: number; unitPrice: number; discount: number }[];
      }>(`/api/v1/sales/${sale.id}/resume`, { terminalId });

      const resumedCart: CartLine[] = detail.lines.map((line) => {
        const product = products?.find((p) => p.variantId === line.variantId);
        return {
          variantId: line.variantId,
          name: product?.name ?? line.description,
          price: line.unitPrice,
          currency: detail.currency,
          taxRate: product?.taxRate ?? 0,
          taxInclusive: product?.taxInclusive ?? false,
          quantity: line.quantity,
          discount: line.discount,
        };
      });

      setCart(resumedCart);
      setResumingSale({ id: detail.id, receiptNumber: detail.receiptNumber });
      setHeldSalesShowing(false);
      setCheckoutError(null);
    } catch (err) {
      setResumeError(err instanceof ApiError ? err.message : t("register.resumeError"));
    } finally {
      setIsResuming(false);
    }
  }

  function handleCancelResume() {
    setResumingSale(null);
    setCart([]);
    setOrderDiscountPercent("");
    setCashTendered("");
  }

  async function loadRecentSales() {
    try {
      const response = await apiGet<{ items: SaleSummary[] }>("/api/v1/sales");
      setRecentSales(response.items);
    } catch (err) {
      setVoidError(err instanceof ApiError ? err.message : t("register.voidError"));
    }
  }

  function toggleRecentSales() {
    setRecentSalesShowing((v) => {
      const next = !v;
      if (next) loadRecentSales();
      return next;
    });
  }

  async function handleVoid(saleId: string) {
    if (!warehouse || !voidReason.trim()) return;

    setVoidError(null);

    try {
      await apiPost(`/api/v1/sales/${saleId}/void`, {
        warehouseId: warehouse.id,
        reason: voidReason.trim(),
      });
      setVoidingSaleId(null);
      setVoidReason("");
      await loadRecentSales();
    } catch (err) {
      setVoidError(err instanceof ApiError ? err.message : t("register.voidError"));
    }
  }

  function startNewSale() {
    setReceipt(null);
    setPaymentMethod("Cash");
    searchRef.current?.focus();
  }

  if (loadError) {
    return (
      <div className="register-page">
        <div className="app-error-banner">{loadError}</div>
      </div>
    );
  }

  if (shift === undefined || products === null) {
    return <div className="register-page register-page--loading">{t("common.loading")}</div>;
  }

  if (!shift) {
    return (
      <div className="register-page register-page--center">
        <div className="register-shift-card">
          <Link to="/" className="register-back-link">
            ← {t("nav.dashboard")}
          </Link>
          <h1>{t("register.openShift")}</h1>
          {closeResult && <div className="app-badge app-badge--positive">{closeResult}</div>}
          {openShiftError && <div className="app-error-banner">{openShiftError}</div>}
          <div className="app-form-field">
            <label htmlFor="opening-float">{t("register.openingFloat")}</label>
            <input
              id="opening-float"
              type="number"
              min="0"
              step="0.01"
              value={openingFloat}
              onChange={(e) => setOpeningFloat(e.target.value)}
            />
          </div>
          <button type="button" className="app-button" disabled={isOpeningShift || !branch} onClick={handleOpenShift}>
            {isOpeningShift ? t("register.opening") : t("register.openShiftAction")}
          </button>
        </div>
      </div>
    );
  }

  if (receipt) {
    return (
      <div className="register-page register-page--center">
        <div className="register-paper">
          <div className="register-paper__check" aria-hidden="true">
            ✓
          </div>
          <h1>{t("register.saleComplete")}</h1>
          <p className="register-paper__number">
            {t("register.receiptNumber")} {receipt.receiptNumber}
          </p>

          <div className="register-paper__lines">
            {receipt.lines.map((line, i) => (
              <div key={i} className="register-paper__line">
                <span>
                  {line.quantity} × {line.description}
                </span>
                <span>{line.gross.toFixed(2)}</span>
              </div>
            ))}
          </div>

          <div className="register-paper__divider" />

          <div className="register-paper__row">
            <span>{t("register.total")}</span>
            <strong>
              {receipt.totalInclusiveTax.toFixed(2)} {receipt.currency}
            </strong>
          </div>
          <div className="register-paper__row">
            <span>{t("register.cashTendered")}</span>
            <span>
              {receipt.amountTendered.toFixed(2)} {receipt.currency}
            </span>
          </div>
          <div className="register-paper__row register-paper__row--change">
            <span>{t("register.changeDue")}</span>
            <strong>
              {receipt.changeGiven.toFixed(2)} {receipt.currency}
            </strong>
          </div>

          <button type="button" className="app-button register-charge-button" onClick={startNewSale} autoFocus>
            {t("register.newSale")}
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="register-page">
      <div className="register-products">
        <div className="register-products__header">
          <Link to="/" className="register-back-link">
            ← {t("nav.dashboard")}
          </Link>
          <input
            ref={searchRef}
            className="register-search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            onKeyDown={handleSearchKeyDown}
            placeholder={t("register.search")}
            autoFocus
          />
          <div className="register-shift-badge">
            <span className="app-sidebar__footer-dot" />
            <span>
              {t("register.shiftOpen")} · {elapsed}
            </span>
            <button type="button" className="app-button app-button--ghost" onClick={toggleHeldSales}>
              {t("register.heldSales")}
            </button>
            <button type="button" className="app-button app-button--ghost" onClick={toggleRecentSales}>
              {t("register.recentSales")}
            </button>
            <button type="button" className="app-button app-button--ghost" onClick={() => setCloseShiftShowing((v) => !v)}>
              {t("register.closeShift")}
            </button>
          </div>
        </div>

        {closeShiftShowing && (
          <div className="app-card">
            {closeShiftError && <div className="app-error-banner">{closeShiftError}</div>}
            <div className="app-form-row">
              <div className="app-form-field">
                <label htmlFor="counted-cash">{t("register.countedCash")}</label>
                <input
                  id="counted-cash"
                  type="number"
                  min="0"
                  step="0.01"
                  value={closingCash}
                  onChange={(e) => setClosingCash(e.target.value)}
                />
              </div>
              <button type="button" className="app-button" disabled={isClosingShift} onClick={handleCloseShift}>
                {isClosingShift ? t("register.closing") : t("register.confirmClose")}
              </button>
            </div>
          </div>
        )}

        {heldSalesShowing && (
          <div className="app-card">
            {resumeError && <div className="app-error-banner">{resumeError}</div>}
            {heldSales === null && <p>{t("common.loading")}</p>}
            {heldSales !== null && heldSales.length === 0 && (
              <div className="app-empty-state">{t("register.heldSalesEmpty")}</div>
            )}
            {heldSales !== null && heldSales.length > 0 && (
              <table className="app-table">
                <tbody>
                  {heldSales.map((sale) => (
                    <tr key={sale.id}>
                      <td>{sale.receiptNumber}</td>
                      <td>
                        {sale.totalInclusiveTax.toFixed(2)} {sale.currency}
                      </td>
                      <td>
                        <button
                          type="button"
                          className="app-button app-button--ghost"
                          disabled={isResuming}
                          onClick={() => handleResume(sale)}
                        >
                          {isResuming ? t("register.resuming") : t("register.resume")}
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}

        {recentSalesShowing && (
          <div className="app-card">
            {voidError && <div className="app-error-banner">{voidError}</div>}
            {recentSales === null && <p>{t("common.loading")}</p>}
            {recentSales !== null && recentSales.length === 0 && (
              <div className="app-empty-state">{t("register.recentSalesEmpty")}</div>
            )}
            {recentSales !== null && recentSales.length > 0 && (
              <table className="app-table">
                <tbody>
                  {recentSales.map((sale) => (
                    <tr key={sale.id}>
                      <td>{sale.receiptNumber}</td>
                      <td>
                        <span
                          className={
                            "app-badge" +
                            (sale.status === "Completed"
                              ? " app-badge--positive"
                              : sale.status === "Voided" || sale.status === "Cancelled"
                                ? " app-badge--negative"
                                : "")
                          }
                        >
                          {statusLabel(sale.status, t)}
                        </span>
                      </td>
                      <td>
                        {sale.totalInclusiveTax.toFixed(2)} {sale.currency}
                      </td>
                      <td>
                        {sale.status === "Completed" &&
                          (voidingSaleId === sale.id ? (
                            <div className="app-form-row">
                              <input
                                value={voidReason}
                                onChange={(e) => setVoidReason(e.target.value)}
                                placeholder={t("register.voidReasonPlaceholder")}
                              />
                              <button
                                type="button"
                                className="app-button"
                                disabled={!voidReason.trim()}
                                onClick={() => handleVoid(sale.id)}
                              >
                                {t("register.confirmVoid")}
                              </button>
                              <button
                                type="button"
                                className="app-button app-button--ghost"
                                onClick={() => {
                                  setVoidingSaleId(null);
                                  setVoidReason("");
                                }}
                              >
                                {t("common.cancel")}
                              </button>
                            </div>
                          ) : (
                            <button
                              type="button"
                              className="app-button app-button--ghost"
                              onClick={() => setVoidingSaleId(sale.id)}
                            >
                              {t("register.void")}
                            </button>
                          ))}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}

        {categories.length > 1 && (
          <div className="register-categories">
            <button
              type="button"
              className={"register-category-pill" + (category === null ? " register-category-pill--active" : "")}
              onClick={() => setCategory(null)}
            >
              {t("common.selectEllipsis").replace("…", "") || "All"}
            </button>
            {categories.map(([id, name]) => (
              <button
                key={id}
                type="button"
                className={"register-category-pill" + (category === id ? " register-category-pill--active" : "")}
                onClick={() => setCategory(id)}
              >
                {name}
              </button>
            ))}
          </div>
        )}

        <div className="register-product-grid">
          {filteredProducts.map((product) => (
            <button
              key={product.variantId}
              type="button"
              className="register-product-card"
              onClick={() => addToCart(product)}
            >
              <span className="register-product-card__name">{product.name}</span>
              <span className="register-product-card__price">
                {product.price.toFixed(2)} {product.currency}
              </span>
            </button>
          ))}
        </div>
      </div>

      <div className="register-cart">
        <div className="register-cart__header">
          <h2>{t("register.cart")}</h2>
          {cart.length > 0 && !resumingSale && (
            <button type="button" className="register-cart__clear" onClick={clearCart}>
              {t("register.clearCart")}
            </button>
          )}
        </div>

        {resumingSale && (
          <div className="app-badge">
            {t("register.resumingBanner", { receiptNumber: resumingSale.receiptNumber })}
            <button type="button" className="app-button app-button--ghost" onClick={handleCancelResume}>
              {t("register.cancelResume")}
            </button>
          </div>
        )}

        {cart.length === 0 ? (
          <div className="app-empty-state register-cart__empty">
            <span className="register-cart__empty-icon" aria-hidden="true">
              🛒
            </span>
            {t("register.cartEmpty")}
          </div>
        ) : (
          <div className="register-cart__lines">
            {cart.map((line) => (
              <div key={line.variantId} className="register-cart__line">
                <div className="register-cart__line-info">
                  <span className="register-cart__line-name">{line.name}</span>
                  <span className="register-cart__line-unit">
                    {line.price.toFixed(2)} {line.currency}
                  </span>
                  {!resumingSale && (
                    <input
                      className="register-cart__line-discount"
                      type="number"
                      min="0"
                      step="0.01"
                      placeholder={t("register.lineDiscount")}
                      value={line.discount || ""}
                      onChange={(e) => updateDiscount(line.variantId, Number(e.target.value) || 0)}
                    />
                  )}
                </div>
                <div className="register-cart__stepper">
                  <button
                    type="button"
                    className="register-cart__step-btn"
                    disabled={!!resumingSale}
                    onClick={() => updateQuantity(line.variantId, line.quantity - 1)}
                  >
                    −
                  </button>
                  <span className="register-cart__qty-value">{line.quantity}</span>
                  <button
                    type="button"
                    className="register-cart__step-btn"
                    disabled={!!resumingSale}
                    onClick={() => updateQuantity(line.variantId, line.quantity + 1)}
                  >
                    +
                  </button>
                </div>
                <span className="register-cart__line-total">
                  {lineTotals(line.price, line.quantity, line.taxRate, line.taxInclusive, line.discount).gross.toFixed(2)}
                </span>
                {!resumingSale && (
                  <button
                    type="button"
                    className="register-cart__remove"
                    onClick={() => updateQuantity(line.variantId, 0)}
                    aria-label={t("register.remove")}
                  >
                    ×
                  </button>
                )}
              </div>
            ))}
          </div>
        )}

        {!resumingSale && cart.length > 0 && (
          <div className="app-form-field">
            <label htmlFor="order-discount">{t("register.orderDiscount")}</label>
            <input
              id="order-discount"
              type="number"
              min="0"
              max="100"
              step="0.01"
              value={orderDiscountPercent}
              onChange={(e) => setOrderDiscountPercent(e.target.value)}
            />
          </div>
        )}

        <div className="register-cart__totals">
          <div className="register-cart__totals-row">
            <span>{t("register.subtotal")}</span>
            <span>{grandTotals.net.toFixed(2)}</span>
          </div>
          <div className="register-cart__totals-row">
            <span>{t("register.tax")}</span>
            <span>{grandTotals.tax.toFixed(2)}</span>
          </div>
          <div className="register-cart__totals-row register-cart__totals-row--total">
            <span>{t("register.total")}</span>
            <span>
              {grandTotals.gross.toFixed(2)}
              {cartCount > 0 && <span className="register-cart__count"> · {cartCount}</span>}
            </span>
          </div>
        </div>

        {checkoutError && <div className="app-error-banner">{checkoutError}</div>}

        <div className="register-payment-toggle">
          <button
            type="button"
            className={"register-payment-btn" + (paymentMethod === "Cash" ? " register-payment-btn--active" : "")}
            onClick={() => setPaymentMethod("Cash")}
          >
            💵 Cash
          </button>
          <button
            type="button"
            className={"register-payment-btn" + (paymentMethod === "Card" ? " register-payment-btn--active" : "")}
            onClick={() => setPaymentMethod("Card")}
          >
            💳 Card
          </button>
        </div>

        {paymentMethod === "Cash" && (
          <>
            <div className="app-form-field">
              <label htmlFor="cash-tendered">{t("register.cashTendered")}</label>
              <input
                id="cash-tendered"
                type="number"
                min="0"
                step="0.01"
                value={cashTendered}
                onChange={(e) => setCashTendered(e.target.value)}
                disabled={cart.length === 0}
              />
            </div>

            {cart.length > 0 && (
              <div className="register-quick-cash">
                {quickCashAmounts(grandTotals.gross).map((amount) => (
                  <button
                    key={amount}
                    type="button"
                    className="register-quick-cash__btn"
                    onClick={() => setCashTendered(amount.toFixed(2))}
                  >
                    {amount.toFixed(2)}
                  </button>
                ))}
              </div>
            )}

            {changePreview > 0 && (
              <p className="register-change-preview">
                {t("register.changeDue")}: <strong>{changePreview.toFixed(2)}</strong>
              </p>
            )}
          </>
        )}

        {holdError && <div className="app-error-banner">{holdError}</div>}

        <div className="register-action-row">
          {!resumingSale && (
            <button
              type="button"
              className="app-button app-button--ghost"
              disabled={cart.length === 0 || isHolding}
              onClick={handleHold}
            >
              {isHolding ? t("register.holding") : t("register.hold")}
            </button>
          )}
          <button
            type="button"
            className="app-button register-charge-button"
            disabled={cart.length === 0 || isCharging}
            onClick={handleCheckout}
          >
            {isCharging ? t("register.charging") : `${t("register.charge")} — ${grandTotals.gross.toFixed(2)}`}
          </button>
        </div>

        <p className="register-cashier">{session?.displayName}</p>
      </div>
    </div>
  );
}
