import os, json, re
from fastapi import FastAPI
from pydantic import BaseModel
from fastapi.middleware.cors import CORSMiddleware
import httpx
from dotenv import load_dotenv

load_dotenv()
app = FastAPI()
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])

QWEN_KEY = os.getenv("QWEN_API_KEY", "")
QWEN_URL = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"

class LogReq(BaseModel):
    log: str

class PredictReq(BaseModel):
    recent_logs: list
    current_log: str

class ContextReq(BaseModel):
    context: str

class MismatchReq(BaseModel):
    mismatches: list

def extract_json(text: str):
    text = text.strip()
    if text.startswith("```"):
        text = re.sub(r"^```json\s*|^```\s*|\s*```$", "", text, flags=re.MULTILINE)
    return json.loads(text)

async def qwen_chat(system: str, user: str):
    if not QWEN_KEY or QWEN_KEY == "your-key":
        if "root_cause" in system:
            return {"root_cause": "Mock: Null reference before initialization", "severity": "high", "severity_score": 78, "fixes": ["Add null check", "Initialize default value"], "prevention": "Use TypeScript interfaces"}
        elif "risk_level" in system:
            return {"failure_risk_score": 65, "risk_level": "moderate", "reasoning": "Mock: Timeout cascade pattern suggests downstream service degradation"}
        elif "recommendations" in system:
            return {"recommendations": [{"category": "performance", "suggestion": "Mock: Add Redis caching for session store"}, {"category": "security", "suggestion": "Mock: Add rate limiting to /login"}]}
        else:
            return {"suggestions": [{"path": "mock", "explanation": "Mock: Cast string to integer using parseInt()"}]}

    async with httpx.AsyncClient(timeout=30.0) as client:
        r = await client.post(
            QWEN_URL,
            headers={"Authorization": f"Bearer {QWEN_KEY}", "Content-Type": "application/json"},
            json={
                "model": "qwen-plus",
                "messages": [
                    {"role": "system", "content": system},
                    {"role": "user", "content": user}
                ],
                "temperature": 0.2
            }
        )
        return extract_json(r.json()["choices"][0]["message"]["content"])

@app.post("/analyze-error")
async def analyze(req: LogReq):
    system = 'You are an expert debugger. ALWAYS respond with valid JSON only: {"root_cause":"string","severity":"low|medium|high|critical","severity_score":0-100,"fixes":["string"],"prevention":"string"}'
    return await qwen_chat(system, f"Analyze this error:{req.log}")

@app.post("/predict")
async def predict(req: PredictReq):
    system = 'You are a reliability analyst. ALWAYS respond with valid JSON only: {"failure_risk_score":0-100,"risk_level":"low|moderate|high","reasoning":"string"}'
    return await qwen_chat(system, "Recent logs:" + "".join(req.recent_logs) + f"Current:{req.current_log}")

@app.post("/recommend")
async def recommend(req: ContextReq):
    system = 'You are a senior engineer. ALWAYS respond with valid JSON only: {"recommendations":[{"category":"performance|security|maintainability","suggestion":"string"}]}'
    return await qwen_chat(system, f"Context:{req.context}")

@app.post("/suggest-fixes")
async def suggest_fixes(req: MismatchReq):
    system = 'You are an API designer. ALWAYS respond with valid JSON only: {"suggestions":[{"path":"string","explanation":"string"}]}'
    return await qwen_chat(system, f"Fix these mismatches:{json.dumps(req.mismatches)}")
