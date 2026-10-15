# Resona Analysis Server

This FastAPI service accepts an audio file at `POST /analyze` and returns the analysis data consumed by the Resona desktop app. `GET /health` verifies that the script and every required model file are present.

Build and start the analyzer from this directory:

```powershell
Copy-Item src\AnalysisServer\.env.example src\AnalysisServer\.env
docker compose --env-file src\AnalysisServer\.env -f src\AnalysisServer\compose.yaml up -d --build
Invoke-RestMethod http://127.0.0.1:8000/health
```

For a direct local start with the Python dependencies installed:

```powershell
$env:RESONA_MODELS_ROOT = "C:\path\to\music\models\Essentia"
python api.py
```

Configuration is read from environment variables:

- `ANALYZER_HOST` and `ANALYZER_PORT` (defaults: `0.0.0.0:8000`)
- `MAX_CONCURRENT_ANALYSES` (default: `1`)
- `ANALYSIS_TIMEOUT_SECONDS` (default: `1800`)
- `MAX_UPLOAD_BYTES` (default: 1 GiB)
- `RESONA_MODELS_ROOT` and `RESONA_TEMP_DIRECTORY`

Only the models currently used by Resona are executed: Discogs MAEST for genres and MusicNN MIREX for emotional character. BPM and EBU R128 values are calculated directly with Essentia.
