import axios from 'axios';

/**
 * The single HTTP client for the whole dashboard.
 *
 * Every request the UI makes goes through here, which is what lets error handling, base URL and
 * timeouts live in one place instead of being re-invented in each component (frontend PRD 16).
 */
const client = axios.create({
  // Production is served by the backend itself, so relative /api keeps the WebView2 page and
  // API same-origin (127.0.0.1 stays 127.0.0.1). Vite proxies /api during local development;
  // VITE_API_URL remains available for an explicitly configured remote backend.
  baseURL: import.meta.env.VITE_API_URL || '/api',
  timeout: 30000,
  headers: { 'Content-Type': 'application/json' }
});

/**
 * Turns any axios failure into a predictable, operator-facing shape.
 *
 * Two rules from the frontend PRD (section 18): the message has to be useful to an operator, and
 * it must never expose a stack trace or a secret. Backend error bodies are already scrubbed, so
 * the worst case here is a generic message rather than a leak.
 */
export function toOperatorError(error) {
  if (error?.response) {
    const { status, data } = error.response;
    const message =
      data?.error ||
      data?.message ||
      data?.title ||
      defaultMessageForStatus(status);

    const fieldErrors = problemDetailsErrors(data);
    return {
      kind: kindForStatus(status),
      status,
      code: data?.errorCode || data?.code || null,
      // ASP.NET Core model-binding failures arrive as RFC 7807 ProblemDetails whose `title` is the
      // generic "One or more validation errors occurred." - the useful part is the `errors`
      // dictionary. Flattened here so every caller can show what was actually wrong.
      message: fieldErrors.length > 0 && !data?.error ? fieldErrors.join(' ') : message,
      fieldErrors
    };
  }

  if (error?.code === 'ECONNABORTED') {
    return {
      kind: 'timeout',
      status: null,
      code: 'TIMEOUT',
      message: 'The request took too long. The backend may be busy.'
    };
  }

  // No response at all means the backend is unreachable, which is a distinct operator situation
  // from "the backend said no".
  return {
    kind: 'offline',
    status: null,
    code: 'BACKEND_UNREACHABLE',
    message: 'Cannot reach the Kairon backend. Check that it is running on port 8000.'
  };
}

/** Flattens a ProblemDetails `errors` dictionary ({ field: [messages] }) into readable lines.
 * Field names are kept only when the message doesn't already say what it is about. */
export function problemDetailsErrors(data) {
  const errors = data?.errors;
  if (!errors || typeof errors !== 'object' || Array.isArray(errors)) return [];
  const lines = [];
  for (const [field, value] of Object.entries(errors)) {
    const messages = Array.isArray(value) ? value : [value];
    for (const m of messages) {
      if (typeof m !== 'string' || !m.trim()) continue;
      const name = field.replace(/^\$\.?/, '');
      lines.push(name && !m.toLowerCase().includes(name.toLowerCase()) ? `${name}: ${m}` : m);
    }
  }
  return lines;
}

function kindForStatus(status) {
  if (status === 404) return 'not-found';
  if (status === 401 || status === 403) return 'unauthorized';
  if (status === 409) return 'conflict';
  if (status >= 500) return 'server';
  return 'client';
}

function defaultMessageForStatus(status) {
  switch (status) {
    case 400: return 'The request was rejected as invalid.';
    case 401: return 'An operator key is required for this action.';
    case 403: return 'This action is not permitted.';
    case 404: return 'Not found.';
    case 409: return 'That action is not valid in the current state.';
    case 503: return 'The service is temporarily unavailable.';
    default: return 'The request failed.';
  }
}

/** Unwraps a response body, normalizing errors on the way out. */
export async function request(promise) {
  try {
    const response = await promise;
    return response.data;
  } catch (error) {
    throw toOperatorError(error);
  }
}

export default client;
