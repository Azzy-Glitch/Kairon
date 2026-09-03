import React, { createContext, useContext, useState } from 'react';

const STORAGE_KEY = 'kairon.pollingMultiplier';
const DEFAULT_MULTIPLIER = 1;

export const POLLING_PRESETS = [
  { id: 'fast', label: 'Fast', multiplier: 0.5, description: 'Poll twice as often - more current, more requests' },
  { id: 'normal', label: 'Normal', multiplier: 1, description: 'The default refresh rate for every live view' },
  { id: 'relaxed', label: 'Relaxed', multiplier: 2, description: 'Poll half as often - fewer requests, less current' }
];

function readInitialMultiplier() {
  try {
    const stored = Number(window.localStorage.getItem(STORAGE_KEY));
    if (POLLING_PRESETS.some((p) => p.multiplier === stored)) return stored;
  } catch {
    // localStorage can be unavailable (private mode, non-browser test environment).
  }
  return DEFAULT_MULTIPLIER;
}

// Not throwing when unwrapped is deliberate: usePolling (consumed widely, including in tests that
// render a single hook/component in isolation) must keep working at the default rate with no
// provider present, rather than every such test needing to know about a settings preference.
const PollingPreferenceCtx = createContext({ multiplier: DEFAULT_MULTIPLIER, setMultiplier: () => {} });

/**
 * Global polling-rate preference (Settings page - redesign brief section 8). A single multiplier
 * applied inside usePolling itself, so every existing poll interval across the app respects it
 * without each call site needing to read a setting individually.
 */
export function PollingPreferenceProvider({ children }) {
  const [multiplier, setMultiplierState] = useState(readInitialMultiplier);

  const setMultiplier = (value) => {
    setMultiplierState(value);
    try {
      window.localStorage.setItem(STORAGE_KEY, String(value));
    } catch {
      // Non-fatal: the in-memory preference still works for this session.
    }
  };

  return (
    <PollingPreferenceCtx.Provider value={{ multiplier, setMultiplier }}>{children}</PollingPreferenceCtx.Provider>
  );
}

export function usePollingPreference() {
  return useContext(PollingPreferenceCtx);
}
