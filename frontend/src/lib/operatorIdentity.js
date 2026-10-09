/**
 * Remembers the last operator name typed into an approval form, purely as a typing convenience.
 *
 * This is NOT authentication or authorization. The name is only pre-filled into the field, the
 * operator can change it, and the backend records and revalidates every approval on its own. Any
 * storage failure (private mode, blocked site data, non-browser environment) is silently ignored.
 */
const STORAGE_KEY = 'kairon.lastOperatorName';

export function loadLastOperator() {
  try {
    const value = window.localStorage.getItem(STORAGE_KEY);
    return typeof value === 'string' ? value : '';
  } catch {
    return '';
  }
}

export function saveLastOperator(name) {
  try {
    const trimmed = (name || '').trim();
    if (trimmed) window.localStorage.setItem(STORAGE_KEY, trimmed);
  } catch {
    // Storage unavailable - the convenience is simply lost.
  }
}

/**
 * Plain-language description of where an action will run, e.g.
 * `on ScmTestDependency (DESKTOP-ABC, Development)`. Empty string when nothing is known.
 */
export function describeActionTarget(action, environment) {
  if (action?.targetKind === 'AppProcess') {
    const program = action.targetProcessExecutable ? action.targetProcessExecutable.split(/[\\/]/).pop() : 'the application';
    const where = [action.targetHostName, environment].filter(Boolean).join(', ');
    return ` (restarts ${program}${action.targetProcessWorkingDirectory ? ` in ${action.targetProcessWorkingDirectory}` : ''}${where ? `, ${where}` : ''})`;
  }
  const service = action?.targetWindowsServiceName;
  const where = [action?.targetHostName, environment].filter(Boolean).join(', ');
  if (service) return ` on ${service}${where ? ` (${where})` : ''}`;
  return where ? ` (${where})` : '';
}
