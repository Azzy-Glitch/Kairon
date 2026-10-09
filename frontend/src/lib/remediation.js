/**
 * Plain-language vocabulary for remediation targets and remediation-action outcomes.
 *
 * Every key here is a value the backend actually emits (TargetReadiness names on
 * RemediationTargetResponse.readiness / RemediationActionDto.targetReadiness, and the
 * classification prefix on RemediationActionDto.executionError). Pure mapping only - the UI never
 * decides readiness or success itself; it only describes what the backend reported.
 */

/** TargetReadiness -> { label, tone, explanation }. tone matches ui/Badge tones. */
export const READINESS = {
  Ready: { label: 'Ready', tone: 'healthy', explanation: 'KAIRON can run the allowed operations on this target once you approve them.' },
  Disabled: { label: 'Disabled', tone: 'neutral', explanation: 'This target is switched off. Nothing will run against it.' },
  PermissionMissing: {
    label: 'Needs Permission',
    tone: 'high',
    explanation: 'Windows has not granted KAIRON the rights this target needs on this service.'
  },
  ServiceMissing: { label: 'Service Missing', tone: 'critical', explanation: 'The Windows service no longer exists on this machine.' },
  MachineOffline: { label: 'Machine Offline', tone: 'medium', explanation: 'The KAIRON Agent on this machine has not reported in recently.' },
  AwaitingAgentConfirmation: {
    label: 'Waiting for app telemetry',
    tone: 'medium',
    explanation: 'The Agent has not yet confirmed that the application is running on this machine. Run the application and send it a request.'
  },
  StaleTarget: {
    label: 'Needs re-confirmation',
    tone: 'medium',
    explanation: 'Something this target depends on changed since it was set up. Review and save it again.'
  },
  ServiceIdentityChanged: {
    label: 'Needs re-confirmation',
    tone: 'high',
    explanation: 'The Windows service now points at a different program or account than when it was confirmed. Review it before enabling again.'
  },
  Denylisted: { label: 'Blocked', tone: 'critical', explanation: 'This service is protected and can never be remediated by KAIRON.' },
  RemoteNotSupported: {
    label: 'Blocked',
    tone: 'critical',
    explanation: 'Only services on the machine running KAIRON itself can be remediated.'
  },
  UnsupportedPlatform: { label: 'Unsupported', tone: 'neutral', explanation: 'Windows service remediation needs a Windows machine.' },
  ProjectInactive: { label: 'Project inactive', tone: 'neutral', explanation: 'The project this target belongs to is no longer active.' },
  CredentialInvalid: {
    label: 'App credential invalid',
    tone: 'high',
    explanation: 'The application credential bound to this target was revoked or no longer exists. Re-pair the application.'
  },
  MachineMismatch: {
    label: 'Different machine',
    tone: 'high',
    explanation: 'The application is reporting from a different machine than the one selected here.'
  },
  InvalidTarget: { label: 'Invalid', tone: 'critical', explanation: 'This target is not valid. Edit it to fix the problem.' },
  OperationNotAllowed: {
    label: 'Operation not allowed',
    tone: 'medium',
    explanation: 'This target does not allow that operation.'
  },
  ProcessUnknown: {
    label: 'Waiting for the app',
    tone: 'medium',
    explanation: 'KAIRON has not yet identified which process is the application. Run it (with an up-to-date SDK) and send it a request.'
  },
  UserAgentOffline: {
    label: 'User not signed in',
    tone: 'medium',
    explanation: 'The application can only be restarted while the Windows user who runs it is signed in (the KAIRON UserAgent starts at sign-in).'
  }
};

export function describeReadiness(readiness) {
  if (!readiness) return { label: 'Unknown', tone: 'neutral', explanation: 'Readiness has not been reported.' };
  return READINESS[readiness] || { label: humanizeIdentifier(readiness), tone: 'neutral', explanation: '' };
}

/** Operations a target can allow, in the order the wizard offers them (least risky first). Must
 * match backend ServiceToolNames exactly - the API rejects anything else. */
export const OPERATIONS = [
  { id: 'RunHealthCheck', label: 'Run a health check', description: 'Reads the service state only. Changes nothing.', risk: 'Low' },
  { id: 'StartService', label: 'Start the service', description: 'Starts the service if it has stopped.', risk: 'Medium' },
  { id: 'RestartService', label: 'Restart the service', description: 'Stops and starts the service. Brief downtime.', risk: 'Medium' },
  { id: 'StopService', label: 'Stop the service', description: 'Stops the service and leaves it stopped. Causes an outage until started again.', risk: 'High' }
];

/** The only operation an application-process target carries: the UserAgent restarts the app's
 * own process (same program, arguments and folder) after approval. */
export const APP_OPERATIONS = [
  {
    id: 'RestartApplication',
    label: 'Restart the application',
    description: 'Stops the app and starts it again exactly as it was started. Brief downtime.',
    risk: 'Medium'
  }
];

export const TARGET_KINDS = {
  AppProcess: { label: 'App process', description: 'The connected application itself, restarted by KAIRON as its own user.' },
  WindowsService: { label: 'Windows service', description: 'An existing Windows service, controlled through the Service Control Manager.' }
};

export function operationLabel(id) {
  return [...OPERATIONS, ...APP_OPERATIONS].find((o) => o.id === id)?.label || humanizeIdentifier(id);
}

/** Windows rights (as the pre-flight reports them) -> what they let KAIRON do. */
export const RIGHT_LABELS = {
  Query: 'query',
  Start: 'start',
  Stop: 'stop'
};

/** executionError classification prefix -> plain-language headline. */
export const EXECUTION_FAILURE = {
  PermissionMissing: 'Windows permission missing',
  ServiceMissing: 'Windows service not found',
  ServiceIdentityChanged: 'The Windows service changed since it was confirmed',
  Denylisted: 'This service is protected and cannot be remediated',
  DependentServicesRunning: 'Other services depend on this one and are still running',
  MachineOffline: 'The machine is offline',
  RemoteNotSupported: 'Only services on the KAIRON machine can be remediated',
  AwaitingAgentConfirmation: 'The application has not been confirmed on this machine yet',
  StaleTarget: 'The remediation target needs re-confirmation',
  OperationNotAllowed: 'The remediation target does not allow this operation',
  UnsupportedPlatform: 'Windows service remediation is not supported here',
  Timeout: 'The service did not respond in time',
  InvalidTarget: 'The remediation target is not valid',
  CredentialInvalid: 'The application credential is no longer valid',
  MachineMismatch: 'The application is running on a different machine',
  ProcessRestartFailed: 'The application could not be restarted',
  ProcessRestartUnconfirmed: 'The restart was started but its outcome was not confirmed',
  UserAgentOffline: 'The app owner is not signed in, so nothing was restarted'
};

/**
 * Splits "PermissionMissing: Access is denied." into { code, headline, detail }. An error with no
 * recognised prefix is returned as-is with no headline, so nothing is ever re-labelled by guess.
 */
export function classifyExecutionError(error) {
  if (!error || typeof error !== 'string') return null;
  const match = /^([A-Z][A-Za-z]+):\s*(.*)$/s.exec(error.trim());
  if (match && EXECUTION_FAILURE[match[1]]) {
    return { code: match[1], headline: EXECUTION_FAILURE[match[1]], detail: match[2] || '', raw: error };
  }
  return { code: null, headline: null, detail: error, raw: error };
}

function humanizeIdentifier(value) {
  return String(value).replace(/([a-z])([A-Z])/g, '$1 $2');
}
