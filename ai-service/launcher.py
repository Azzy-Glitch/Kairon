"""Self-contained Windows entry point used by the KAIRON installer build."""

import uvicorn
from main import app

if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=8001, log_level="warning")
