"""
Self-running entrypoint for the packaged Kairon.AI executable (docs/DESKTOP_SHELL.md).

`main.py` only defines `app` for an external `uvicorn main:app` process to import - that's the
right shape for local dev (`python -m uvicorn main:app --port 8001`), but a PyInstaller --onefile
build has no such external process to invoke it. This is the actual thing PyInstaller packages;
`main.py` itself is unchanged.
"""

import os

import uvicorn

from main import app

if __name__ == "__main__":
    port = int(os.environ.get("KAIRON_AI_PORT", "8001"))
    uvicorn.run(app, host="127.0.0.1", port=port, log_level="info")
