import { useState, type FormEvent } from "react";
import { Navigate, useLocation, useNavigate } from "react-router-dom";
import { ApiError, useAuth } from "../auth/AuthContext";
import { VantaBackground } from "../components/VantaBackground";
import { useLanguage } from "../i18n/LanguageContext";

export function LoginPage() {
  const { isAuthenticated, isLoading, login } = useAuth();
  const { t, language, toggleLanguage } = useLanguage();
  const navigate = useNavigate();
  const location = useLocation();

  const [subdomain, setSubdomain] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  // Same reasoning as ProtectedRoute: don't decide "not authenticated, show the
  // form" until the silent-refresh bootstrap has actually resolved, or a direct
  // visit to /login while already signed in would flash the form first.
  if (isLoading) {
    return null;
  }

  if (isAuthenticated) {
    const from = (location.state as { from?: Location })?.from?.pathname ?? "/";
    return <Navigate to={from} replace />;
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setIsSubmitting(true);

    try {
      await login(subdomain, email, password);
      navigate("/", { replace: true });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t("login.genericError"));
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <div className="login-page">
      <VantaBackground />
      <div className="login-page__scrim" />

      <button type="button" className="login-lang-switch" onClick={toggleLanguage}>
        {language === "en" ? "العربية" : "English"}
      </button>

      <div className="login-layout">
        <section className="login-brand">
          <div className="login-brand__mark">
            <svg viewBox="0 0 40 40" fill="none" xmlns="http://www.w3.org/2000/svg">
              <rect width="40" height="40" rx="11" fill="url(#mark-gradient)" />
              <path
                d="M12.5 27.5v-15h6.75a4.75 4.75 0 1 1 0 9.5H14.5"
                stroke="#fff"
                strokeWidth="2.15"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
              <defs>
                <linearGradient id="mark-gradient" x1="0" y1="0" x2="40" y2="40" gradientUnits="userSpaceOnUse">
                  <stop stopColor="#5b8cff" />
                  <stop offset="1" stopColor="#1c2ec7" />
                </linearGradient>
              </defs>
            </svg>
          </div>

          <span className="login-brand__eyebrow">{t("login.eyebrow")}</span>
          <h1>
            {t("login.title1")}
            <br />
            {t("login.title2")}
          </h1>
          <p>{t("login.subtitle")}</p>

          <ul className="login-brand__stats">
            <li>
              <strong>9</strong>
              <span>{t("login.statModules")}</span>
            </li>
            <li>
              <strong>100%</strong>
              <span>{t("login.statOffline")}</span>
            </li>
            <li>
              <strong>{t("login.statSyncValue")}</strong>
              <span>{t("login.statSync")}</span>
            </li>
          </ul>
        </section>

        <form className="login-card" onSubmit={handleSubmit} noValidate>
          <div className="login-card__header">
            <h2>{t("login.welcomeBack")}</h2>
            <p>{t("login.subheading")}</p>
          </div>

          {error && <div className="login-card__error">{error}</div>}

          <div className="app-form-field">
            <label htmlFor="subdomain">{t("login.workspace")}</label>
            <input
              id="subdomain"
              value={subdomain}
              onChange={(e) => setSubdomain(e.target.value)}
              placeholder={t("login.workspacePlaceholder")}
              autoComplete="organization"
              required
            />
          </div>

          <div className="app-form-field">
            <label htmlFor="email">{t("login.email")}</label>
            <input
              id="email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              autoComplete="username"
              placeholder={t("login.emailPlaceholder")}
              required
            />
          </div>

          <div className="app-form-field">
            <label htmlFor="password">{t("login.password")}</label>
            <input
              id="password"
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
              placeholder="••••••••"
              required
            />
          </div>

          <button type="submit" className="login-card__submit" disabled={isSubmitting}>
            <span>{isSubmitting ? t("login.signingIn") : t("login.signIn")}</span>
          </button>

          <p className="login-card__footnote">{t("login.footnote")}</p>
        </form>
      </div>
    </div>
  );
}
