import { useEffect, useState, type CSSProperties } from "react";
import { Link } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { apiGet } from "../api/client";
import { useLanguage } from "../i18n/LanguageContext";
import type { TranslationKey } from "../i18n/translations";
import { VantaHeroBackground } from "../components/VantaHeroBackground";
import { useCountUp } from "../hooks/useCountUp";

interface ProductListResponse {
  items: unknown[];
}

interface StatTile {
  labelKey: TranslationKey;
  value: number | null;
}

function StatCard({ labelKey, value }: StatTile) {
  const { t } = useLanguage();
  const animated = useCountUp(value);

  return (
    <div className="dashboard-stat">
      <span className="dashboard-stat__value">{value === null ? "—" : animated}</span>
      <span className="dashboard-stat__label">{t(labelKey)}</span>
    </div>
  );
}

export function DashboardPage() {
  const { session } = useAuth();
  const { t } = useLanguage();

  const [stats, setStats] = useState<StatTile[]>([
    { labelKey: "dashboard.statProducts", value: null },
    { labelKey: "dashboard.statSuppliers", value: null },
    { labelKey: "dashboard.statExpenses", value: null },
  ]);

  useEffect(() => {
    let cancelled = false;

    Promise.allSettled([
      apiGet<ProductListResponse>("/api/v1/catalog/products"),
      apiGet<unknown[]>("/api/v1/purchasing/suppliers"),
      apiGet<unknown[]>("/api/v1/expenses"),
    ]).then(([products, suppliers, expenses]) => {
      if (cancelled) return;
      setStats([
        {
          labelKey: "dashboard.statProducts",
          value: products.status === "fulfilled" ? products.value.items.length : 0,
        },
        {
          labelKey: "dashboard.statSuppliers",
          value: suppliers.status === "fulfilled" ? suppliers.value.length : 0,
        },
        {
          labelKey: "dashboard.statExpenses",
          value: expenses.status === "fulfilled" ? expenses.value.length : 0,
        },
      ]);
    });

    return () => {
      cancelled = true;
    };
  }, []);

  const quickLinks: { to: string; labelKey: TranslationKey; hintKey: TranslationKey; icon: string }[] = [
    { to: "/register", labelKey: "dashboard.quickRegister", hintKey: "dashboard.quickRegisterHint", icon: "🧮" },
    { to: "/products", labelKey: "dashboard.quickProducts", hintKey: "dashboard.quickProductsHint", icon: "📦" },
    { to: "/purchasing", labelKey: "dashboard.quickPurchasing", hintKey: "dashboard.quickPurchasingHint", icon: "🧾" },
    { to: "/inventory", labelKey: "dashboard.quickInventory", hintKey: "dashboard.quickInventoryHint", icon: "🏬" },
    { to: "/expenses", labelKey: "dashboard.quickExpenses", hintKey: "dashboard.quickExpensesHint", icon: "💳" },
  ];

  return (
    <div className="dashboard">
      <section className="dashboard-hero">
        <VantaHeroBackground />
        <div className="dashboard-hero__scrim" />

        <div className="dashboard-hero__content">
          <span className="dashboard-hero__eyebrow">{t("dashboard.eyebrow")}</span>
          <h1 className="dashboard-hero__title">
            {t("dashboard.welcome")}, {session?.displayName?.split(" ")[0] ?? "—"}
          </h1>
          <p className="dashboard-hero__lead">
            {t("dashboard.signedInAs")} <strong>{session?.displayName}</strong> ({session?.email}){" "}
            {t("dashboard.inWorkspace")} <strong>{session?.subdomain}</strong>.
          </p>
          <p className="dashboard-hero__sub">{t("dashboard.helpText")}</p>
        </div>

        <div className="dashboard-hero__stats">
          {stats.map((stat) => (
            <StatCard key={stat.labelKey} labelKey={stat.labelKey} value={stat.value} />
          ))}
        </div>
      </section>

      <div className="app-quick-grid app-quick-grid--dashboard">
        {quickLinks.map((link, i) => (
          <Link
            key={link.to}
            to={link.to}
            className="app-quick-card app-quick-card--glow"
            style={{ "--card-delay": `${i * 70}ms` } as CSSProperties}
          >
            <span className="app-quick-card__icon" aria-hidden="true">
              {link.icon}
            </span>
            <span className="app-quick-card__label">{t(link.labelKey)}</span>
            <span className="app-quick-card__hint">{t(link.hintKey)}</span>
            <span className="app-quick-card__arrow" aria-hidden="true">
              →
            </span>
          </Link>
        ))}
      </div>
    </div>
  );
}
