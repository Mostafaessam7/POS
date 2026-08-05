import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { translations, type Language, type TranslationKey } from "./translations";

const STORAGE_KEY = "pos.language";

interface LanguageContextValue {
  language: Language;
  setLanguage: (language: Language) => void;
  toggleLanguage: () => void;
  t: (key: TranslationKey, vars?: Record<string, string | number>) => string;
  dir: "ltr" | "rtl";
}

const LanguageContext = createContext<LanguageContextValue | undefined>(undefined);

function loadInitialLanguage(): Language {
  const stored = localStorage.getItem(STORAGE_KEY);
  if (stored === "en" || stored === "ar") return stored;
  return "en";
}

export function LanguageProvider({ children }: { children: ReactNode }) {
  const [language, setLanguageState] = useState<Language>(loadInitialLanguage);

  const dir = language === "ar" ? "rtl" : "ltr";

  useEffect(() => {
    document.documentElement.lang = language;
    document.documentElement.dir = dir;
  }, [language, dir]);

  const value = useMemo<LanguageContextValue>(
    () => ({
      language,
      dir,
      setLanguage(next: Language) {
        setLanguageState(next);
        localStorage.setItem(STORAGE_KEY, next);
      },
      toggleLanguage() {
        const next = language === "en" ? "ar" : "en";
        setLanguageState(next);
        localStorage.setItem(STORAGE_KEY, next);
      },
      t(key: TranslationKey, vars?: Record<string, string | number>) {
        const template = translations[language][key] ?? translations.en[key] ?? key;
        if (!vars) return template;
        return template.replace(/\{\{(\w+)\}\}/g, (match, name: string) =>
          name in vars ? String(vars[name]) : match,
        );
      },
    }),
    [language, dir],
  );

  return <LanguageContext.Provider value={value}>{children}</LanguageContext.Provider>;
}

export function useLanguage(): LanguageContextValue {
  const context = useContext(LanguageContext);
  if (!context) throw new Error("useLanguage must be used within a LanguageProvider");
  return context;
}
