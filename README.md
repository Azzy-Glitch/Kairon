#Kairon

## Quick Start

### 1. AI Service (Python)
```bash
cd ai-service
python -m venv venv && source venv/bin/activate
pip install -r requirements.txt
uvicorn main:app --reload --port=8001
```

### 2. Backend (.NET 10)
```bash
cd backend
dotnet restore
dotnet run --urls http://localhost:8000
```

### 3. Frontend (React + Vite)
```bash
cd frontend
npm install
npm run dev
# Open http://localhost:5173
```

## Test (no API key needed)
The AI service has a built-in mock mode. Leave `QWEN_API_KEY=your-key` to use mock responses.

## curl Test
```bash
curl -X POST http://localhost:8000/api/analyze-error   -H "Content-Type: application/json"   -d '{"log":"TypeError: Cannot read properties of undefined"}'
```
