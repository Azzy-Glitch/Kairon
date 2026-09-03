import React, { useState } from 'react';
import DashboardOverview from '../DashboardOverview';
import ErrorAnalyzer from '../ErrorAnalyzer';
import ApiValidator from '../ApiValidator';
import Predictor from '../Predictor';
import Recommender from '../Recommender';
import Tabs from '../ui/Tabs';

const TOOLS = [
  { id: 'overview', label: 'Overview' },
  { id: 'error', label: 'Error Analyzer' },
  { id: 'api', label: 'API Validator' },
  { id: 'predict', label: 'Predictor' },
  { id: 'rec', label: 'Recommender' }
];

/**
 * Developer-facing diagnostic screens (frontend PRD section 29).
 *
 * These predate the autonomous SRE layer and stay fully functional - same components, same API
 * calls - just grouped behind one nav entry instead of competing with the primary SRE workflow for
 * top-level attention. The sub-nav is the shared Tabs component (redesign brief section 4) rather
 * than a native, unstyled <button class="tab-btn"> with no matching CSS anywhere.
 */
export default function DeveloperTools() {
  const [tool, setTool] = useState('overview');

  return (
    <div className="animate-fade-in">
      <Tabs items={TOOLS} activeId={tool} onChange={setTool} className="dev-tools-subnav" />

      {tool === 'overview' && <DashboardOverview onSelectTab={setTool} />}
      {tool === 'error' && <ErrorAnalyzer />}
      {tool === 'api' && <ApiValidator />}
      {tool === 'predict' && <Predictor />}
      {tool === 'rec' && <Recommender />}
    </div>
  );
}
