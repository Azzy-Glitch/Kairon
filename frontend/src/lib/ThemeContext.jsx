import React, { createContext, useContext, useEffect, useState } from 'react';

const STORAGE_KEY = 'kairon.theme';
const THEMES = ['system', 'light', 'dark'];

function readInitialTheme() {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    if (THEMES.includes(stored)) return stored;
  } catch {
    // localStorage can be unavailable (private mode, non-browser test environment).
  }
  // Light is the default until the operator makes an explicit choice - a demo should never
  // accidentally render dark just because the presenter's OS happens to be in dark mode.
  // "System" stays a selectable option in Settings, it just isn't the unset default.
  return 'light';
}

function applyTheme(theme) {
  try {
    if (theme === 'system') delete document.documentElement.dataset.theme;
    else document.documentElement.dataset.theme = theme;
  } catch {
    // document can be unavailable in non-browser test environments.
  }
}

const ThemeCtx = createContext({ theme: 'system', setTheme: () => {} });

/**
 * Theme preference (Settings page - redesign brief section 8). "System" (the default) leaves no
 * [data-theme] attribute, so tokens.css's prefers-color-scheme media query decides; an explicit
 * Light/Dark choice stamps the attribute and always wins over the OS setting, in both directions.
 */
export function ThemeProvider({ children }) {
  const [theme, setThemeState] = useState(readInitialTheme);

  useEffect(() => {
    applyTheme(theme);
  }, [theme]);

  const setTheme = (value) => {
    setThemeState(value);
    try {
      window.localStorage.setItem(STORAGE_KEY, value);
    } catch {
      // Non-fatal: the in-memory preference (and its effect on this render) still applies.
    }
  };

  return <ThemeCtx.Provider value={{ theme, setTheme }}>{children}</ThemeCtx.Provider>;
}

export function useTheme() {
  return useContext(ThemeCtx);
}
