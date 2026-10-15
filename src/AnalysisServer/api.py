import asyncio
import json
import logging
import os
import sys
import tempfile
from pathlib import Path

import uvicorn
from fastapi import FastAPI, File, HTTPException, UploadFile
from fastapi.responses import JSONResponse


BASE_DIRECTORY = Path(__file__).resolve().parent
SCRIPT_PATH = BASE_DIRECTORY / "analyze.py"
MODELS_ROOT = Path(
    os.environ.get("RESONA_MODELS_ROOT", BASE_DIRECTORY / "models" / "Essentia")
).resolve()
TEMP_DIRECTORY = Path(os.environ.get("RESONA_TEMP_DIRECTORY", tempfile.gettempdir())).resolve()
SUPPORTED_EXTENSIONS = {".flac", ".m4a", ".mp3", ".wav"}
UPLOAD_CHUNK_SIZE = 1024 * 1024
MAX_UPLOAD_BYTES = int(os.environ.get("MAX_UPLOAD_BYTES", str(1024 * 1024 * 1024)))
ANALYSIS_TIMEOUT_SECONDS = int(os.environ.get("ANALYSIS_TIMEOUT_SECONDS", "1800"))
MAX_CONCURRENT_ANALYSES = max(1, int(os.environ.get("MAX_CONCURRENT_ANALYSES", "1")))
ANALYZER_HOST = os.environ.get("ANALYZER_HOST", "0.0.0.0")
ANALYZER_PORT = int(os.environ.get("ANALYZER_PORT", "8000"))
ANALYSIS_SLOTS = asyncio.Semaphore(MAX_CONCURRENT_ANALYSES)
LOGGER = logging.getLogger("resona.analyzer")

REQUIRED_FILES = (
    SCRIPT_PATH,
    MODELS_ROOT / "DiscogsMAEST" / "discogs-maest-30s-pw-519l-2.pb",
    MODELS_ROOT / "DiscogsMAEST" / "discogs-maest-30s-pw-519l-2.json",
    MODELS_ROOT / "MusicNN" / "Extractor" / "msd-musicnn-1.pb",
    MODELS_ROOT / "MusicNN" / "Extractor" / "msd-musicnn-1.json",
    MODELS_ROOT / "MusicNN" / "Heads" / "Mood" / "moods_mirex-msd-musicnn-1.pb",
    MODELS_ROOT / "MusicNN" / "Heads" / "Mood" / "moods_mirex-msd-musicnn-1.json",
)

app = FastAPI(title="Resona Analysis API", docs_url=None, redoc_url=None)


@app.get("/health")
def health() -> dict:
    missing = [str(path.relative_to(BASE_DIRECTORY)) if path.is_relative_to(BASE_DIRECTORY) else str(path)
               for path in REQUIRED_FILES if not path.is_file()]
    if missing:
        raise HTTPException(
            status_code=503,
            detail={"message": "Analyzer files are incomplete", "missing": missing},
        )

    return {
        "status": "ok",
        "service": "resona-analysis",
        "maxConcurrentAnalyses": MAX_CONCURRENT_ANALYSES,
    }


@app.post("/analyze")
async def analyze(file: UploadFile = File(...)):
    suffix = Path(file.filename or "").suffix.lower()
    if suffix not in SUPPORTED_EXTENSIONS:
        raise HTTPException(status_code=415, detail="Unsupported audio format")

    TEMP_DIRECTORY.mkdir(parents=True, exist_ok=True)
    temporary_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            delete=False, suffix=suffix, dir=TEMP_DIRECTORY
        ) as temporary_file:
            temporary_path = Path(temporary_file.name)
            uploaded_bytes = 0
            while chunk := await file.read(UPLOAD_CHUNK_SIZE):
                uploaded_bytes += len(chunk)
                if uploaded_bytes > MAX_UPLOAD_BYTES:
                    raise HTTPException(status_code=413, detail="Audio file is too large")
                temporary_file.write(chunk)

        if uploaded_bytes == 0:
            raise HTTPException(status_code=400, detail="Audio file is empty")

        async with ANALYSIS_SLOTS:
            payload, return_code, stderr = await run_analyzer(temporary_path)

        if return_code != 0 or payload.get("success") is not True:
            if stderr:
                LOGGER.error("Analyzer process failed: %s", stderr[-4000:])
            error_payload = dict(payload)
            error_payload.setdefault("success", False)
            error_payload.setdefault("error", "Analysis failed")
            return JSONResponse(status_code=500, content=error_payload)

        return payload
    finally:
        await file.close()
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)


async def run_analyzer(track_path: Path) -> tuple[dict, int, str]:
    process = await asyncio.create_subprocess_exec(
        sys.executable,
        str(SCRIPT_PATH),
        str(track_path),
        "--models-root",
        str(MODELS_ROOT),
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
        cwd=BASE_DIRECTORY,
    )

    try:
        stdout, stderr = await asyncio.wait_for(
            process.communicate(), timeout=ANALYSIS_TIMEOUT_SECONDS
        )
    except TimeoutError:
        process.kill()
        await process.wait()
        raise HTTPException(status_code=504, detail="Analysis timed out")
    except asyncio.CancelledError:
        process.kill()
        await process.wait()
        raise

    stdout_text = stdout.decode("utf-8", errors="replace").strip()
    stderr_text = stderr.decode("utf-8", errors="replace").strip()
    try:
        payload = json.loads(stdout_text)
    except json.JSONDecodeError:
        if stderr_text:
            LOGGER.error("Analyzer returned invalid JSON: %s", stderr_text[-4000:])
        raise HTTPException(status_code=500, detail="Analyzer returned invalid JSON")

    if not isinstance(payload, dict):
        raise HTTPException(status_code=500, detail="Analyzer returned an invalid response")
    return payload, process.returncode or 0, stderr_text


if __name__ == "__main__":
    logging.basicConfig(level=os.environ.get("LOG_LEVEL", "INFO").upper())
    uvicorn.run(app, host=ANALYZER_HOST, port=ANALYZER_PORT, access_log=False)
