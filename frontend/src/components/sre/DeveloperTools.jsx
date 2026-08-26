import React, { useState } from 'react';
import DashboardOverview from '../DashboardOverview';
import ErrorAnalyzer from '../ErrorAnalyzer';
import ApiValidator from '../ApiValidator';
import Predictor from '../Predictor';
import Recommender from '../Recommender';
import { IconDashboard, IconBug, IconLink, IconPredict, IconSparkles } from '../Icons';

const TOOLS = [
  { id: 'overview', label: 'Overview', icon: <IconDashboard className="w-4 h-4" /> },
  { id: 'error', label: 'Error Analyzer', icon: <IconBug className="w-4 h-4 text-rose-400" /> },
  { id: 'api', label: 'API Validator', icon: <IconLink className="w-4 h-4 text-cyan-400" /> },
  { id: 'predict', label: 'Predictor', icon: <IconPredict className="w-4 h-4 text-purple-400" /> },
  { id: 'rec', label: 'Recommender', icon: <IconSparkles className="w-4 h-4 text-amber-400" /> }
];

/**
 * Developer-facing diagnostic screens (frontend PRD section 29).
 *
 * These predate the autonomous SRE layer and stay fully functional - same components, same API
 * calls - just grouped behind one nav entry instead of competing with the primary SRE workflow for
 * top-level attention.
 */
export default function DeveloperTools() {
  const [tool, setTool] = useState('overview');

  return (
    <div className="animate-fade-in">
      <div className="dev-tools-subnav">
        {TOOLS.map((t) => (
          <button
            key={t.id}
            type="button"
            className={`tab-btn dev-tools-tab ${tool === t.id ? 'active' : ''}`}
            onClick={() => setTool(t.id)}
          >
            {t.icon}
            <span className="tab-label">{t.label}</span>
          </button>
        ))}
      </div>

      {tool === 'overview' && <DashboardOverview onSelectTab={setTool} />}
      {tool === 'error' && <ErrorAnalyzer />}
      {tool === 'api' && <ApiValidator />}
      {tool === 'predict' && <Predictor />}
      {tool === 'rec' && <Recommender />}
    </div>
  );
}
