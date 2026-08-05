import type { ReactNode } from "react";
import { NavLink, Outlet } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLanguage } from "../i18n/LanguageContext";
import { useTheme } from "../theme/ThemeContext";
import type { TranslationKey } from "../i18n/translations";

const ICONS: Record<string, ReactNode> = {
  register: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3.5" y="8.5" width="17" height="12" rx="1.8" />
      <path d="M7 8.5V6a2.5 2.5 0 0 1 2.5-2.5h5A2.5 2.5 0 0 1 17 6v2.5" />
      <path d="M8 13.5h2M8 17h2M14 13.5h2M14 17h2" />
    </svg>
  ),
  dashboard: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3.5" y="3.5" width="7" height="8.5" rx="1.5" />
      <rect x="13.5" y="3.5" width="7" height="5.5" rx="1.5" />
      <rect x="13.5" y="12.5" width="7" height="8" rx="1.5" />
      <rect x="3.5" y="15" width="7" height="5.5" rx="1.5" />
    </svg>
  ),
  products: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M20.5 7.5 12 3 3.5 7.5 12 12l8.5-4.5Z" />
      <path d="M3.5 7.5v9L12 21l8.5-4.5v-9" />
      <path d="M12 12v9" />
    </svg>
  ),
  purchasing: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M3.5 4.5h2l1.2 11.2a1.8 1.8 0 0 0 1.8 1.6h9a1.8 1.8 0 0 0 1.78-1.53L20.5 8H6.2" />
      <circle cx="9.5" cy="20" r="1.35" />
      <circle cx="17" cy="20" r="1.35" />
    </svg>
  ),
  inventory: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3.5" y="3.5" width="17" height="17" rx="2" />
      <path d="M3.5 9.5h17M9.5 3.5v17" />
    </svg>
  ),
  expenses: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 3v18M17.5 6.8c0-1.8-2.46-3.3-5.5-3.3S6.5 5 6.5 6.8c0 3.7 11 1.7 11 6.4 0 1.98-2.46 3.6-5.5 3.6s-5.5-1.62-5.5-3.6" />
    </svg>
  ),
  reconciliation: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M7 3.5v13a2 2 0 0 0 2 2h9" />
      <path d="M4.5 8h13a2 2 0 0 1 2 2v10.5" />
      <path d="m4 12.5-1.5 1.5L4 15.5M20 15.5 21.5 14 20 12.5" />
    </svg>
  ),
  users: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="9" cy="8" r="3.4" />
      <path d="M2.8 20a6.2 6.2 0 0 1 12.4 0" />
      <path d="M16 4.3a3.4 3.4 0 0 1 0 6.6M21.2 20a5.6 5.6 0 0 0-4.4-5.5" />
    </svg>
  ),
  settings: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="12" r="3.2" />
      <path d="M19.4 13.5a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V19.5a2 2 0 1 1-4 0v-.09a1.65 1.65 0 0 0-1.08-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H4.5a2 2 0 1 1 0-4h.09a1.65 1.65 0 0 0 1.51-1.08 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9.6a1.65 1.65 0 0 0 1-1.51V4.5a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v.09a1.65 1.65 0 0 0 1.51 1H19.5a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z" />
    </svg>
  ),
};

const NAV_ITEMS: { to: string; labelKey: TranslationKey; icon: string; end?: boolean }[] = [
  { to: "/", labelKey: "nav.dashboard", icon: "dashboard", end: true },
  { to: "/register", labelKey: "nav.register", icon: "register" },
  { to: "/products", labelKey: "nav.products", icon: "products" },
  { to: "/purchasing", labelKey: "nav.purchasing", icon: "purchasing" },
  { to: "/inventory", labelKey: "nav.inventory", icon: "inventory" },
  { to: "/expenses", labelKey: "nav.expenses", icon: "expenses" },
  { to: "/reconciliation", labelKey: "nav.reconciliation", icon: "reconciliation" },
  { to: "/users", labelKey: "nav.users", icon: "users" },
  { to: "/settings", labelKey: "nav.settings", icon: "settings" },
];

function initialsOf(name: string | undefined): string {
  if (!name) return "?";
  const parts = name.trim().split(/\s+/);
  const initials = parts.length > 1 ? parts[0][0] + parts[parts.length - 1][0] : parts[0].slice(0, 2);
  return initials.toUpperCase();
}

function ThemeIcon({ theme }: { theme: "light" | "dark" }) {
  if (theme === "dark") {
    return (
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <path d="M20 14.5A8.5 8.5 0 0 1 9.5 4a8.5 8.5 0 1 0 10.5 10.5Z" />
      </svg>
    );
  }
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="12" r="4.2" />
      <path d="M12 2.5v2.2M12 19.3v2.2M4.5 12H2.3M21.7 12h-2.2M5.6 5.6l1.55 1.55M16.85 16.85l1.55 1.55M18.4 5.6l-1.55 1.55M7.15 16.85l-1.55 1.55" />
    </svg>
  );
}

export function AppLayout() {
  const { session, logout } = useAuth();
  const { t } = useLanguage();
  const { theme, toggleTheme } = useTheme();

  return (
    <div className="app-shell">
      <aside className="app-sidebar">
        <div className="app-sidebar__brand">
          <span className="app-sidebar__mark">
            <svg viewBox="0 0 40 40" fill="none" xmlns="http://www.w3.org/2000/svg">
              <rect width="40" height="40" rx="11" fill="url(#sidebar-mark-gradient)" />
              <path
                d="M12.5 27.5v-15h6.75a4.75 4.75 0 1 1 0 9.5H14.5"
                stroke="#fff"
                strokeWidth="2.15"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
              <defs>
                <linearGradient id="sidebar-mark-gradient" x1="0" y1="0" x2="40" y2="40" gradientUnits="userSpaceOnUse">
                  <stop stopColor="#5b8cff" />
                  <stop offset="1" stopColor="#1c2ec7" />
                </linearGradient>
              </defs>
            </svg>
          </span>
          <span className="app-sidebar__brand-text">{t("sidebar.brand")}</span>
        </div>

        <nav className="app-sidebar__nav">
          {NAV_ITEMS.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              className={({ isActive }) => "app-nav-link" + (isActive ? " app-nav-link--active" : "")}
            >
              <span className="app-nav-link__icon">{ICONS[item.icon]}</span>
              <span>{t(item.labelKey)}</span>
            </NavLink>
          ))}
        </nav>

        <div className="app-sidebar__footer">
          <span className="app-sidebar__footer-dot" />
          {t("sidebar.liveSync")}
        </div>
      </aside>

      <div className="app-main">
        <header className="app-topbar">
          <div className="app-topbar__workspace">
            <span className="app-topbar__workspace-label">{t("topbar.workspace")}</span>
            <span className="app-topbar__workspace-name">{session?.subdomain}</span>
          </div>
          <div className="app-topbar__user">
            <LanguageSwitch />
            <button
              type="button"
              className="app-icon-button"
              onClick={toggleTheme}
              aria-label={theme === "dark" ? t("theme.toggleToLight") : t("theme.toggleToDark")}
              title={theme === "dark" ? t("theme.toggleToLight") : t("theme.toggleToDark")}
            >
              <ThemeIcon theme={theme} />
            </button>
            <div className="app-avatar">{initialsOf(session?.displayName)}</div>
            <div className="app-topbar__user-info">
              <span className="app-topbar__user-name">{session?.displayName}</span>
              <span className="app-topbar__user-email">{session?.email}</span>
            </div>
            <button type="button" className="app-button app-button--ghost" onClick={logout}>
              {t("topbar.signOut")}
            </button>
          </div>
        </header>
        <main className="app-content">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

function LanguageSwitch() {
  const { language, toggleLanguage } = useLanguage();

  return (
    <button type="button" className="app-lang-switch" onClick={toggleLanguage}>
      {language === "en" ? "AR" : "EN"}
    </button>
  );
}
