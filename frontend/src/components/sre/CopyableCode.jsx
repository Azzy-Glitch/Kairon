import React, { useState } from 'react';
import { useToast } from '../Toast';
import { IconCopy, IconCheck } from '../Icons';

/** Clipboard copy that reports failure honestly instead of claiming success. */
export function useCopy() {
  const toast = useToast();
  const [copiedKey, setCopiedKey] = useState(null);

  const copy = async (text, key) => {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      toast.addToast('Copy failed. Select the text and copy it manually.', 'error');
      return;
    }
    setCopiedKey(key);
    setTimeout(() => setCopiedKey(null), 2000);
    toast.addToast('Copied to clipboard', 'info');
  };

  return { copiedKey, copy };
}

/** A code block with a copy button (the SDK pages' existing sdk-code-block styling). */
export function CodeBlock({ code, copyKey, label }) {
  const { copiedKey, copy } = useCopy();
  return (
    <div className="sdk-code-block">
      <button
        type="button"
        className="small-btn sdk-code-copy"
        aria-label={label || `Copy ${copyKey} example`}
        onClick={() => copy(code, copyKey)}
      >
        {copiedKey === copyKey ? <IconCheck className="w-3 h-3" /> : <IconCopy className="w-3 h-3" />}
      </button>
      <pre><code>{code}</code></pre>
    </div>
  );
}
