/**
 * Shared AI-status wording for every health consumer (DashboardOverview, SreDashboard,
 * AiInsightsPage, SettingsPage, App.jsx's sidebar badge) - one place decides how
 * aiService/aiMode/aiProviderReachable become a human sentence, so no consumer can independently
 * (and inconsistently) imply "AI online/operational" while AI is actually unconfigured, and a real
 * provider outage always reads differently from "nothing is configured yet".
 *
 * aiService: process liveness only. aiMode: "mock" | "unconfigured" | a real provider name | "unknown".
 * aiProviderReachable: whether that configured provider was reachable last time it was used.
 */
export function describeAiStatus(health) {
  if (!health?.aiService) {
    return { label: 'Service offline', short: 'Offline', tone: 'urgent' };
  }
  if (health.aiMode === 'mock') {
    return { label: 'Mock mode - explicit test configuration', short: 'Mock mode', tone: 'attention' };
  }
  if (health.aiMode === 'unconfigured') {
    return { label: 'Service online - provider not configured', short: 'Not configured', tone: 'attention' };
  }
  if (!health.aiMode || health.aiMode === 'unknown') {
    return { label: 'Service online - status unknown', short: 'Status unknown', tone: 'attention' };
  }
  if (health.aiProviderReachable === false) {
    return {
      label: `Service online - provider unavailable (${health.aiMode})`,
      short: 'Provider unavailable',
      tone: 'urgent',
    };
  }
  return {
    label: `Service online - provider configured (${health.aiMode})`,
    short: `Provider: ${health.aiMode}`,
    tone: 'good',
  };
}
