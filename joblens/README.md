# JobLens AI — Integrity Monitor

An AI-powered web application that monitors user behavior in real-time to detect potential cheating during exams or interviews. Built on top of a desktop Python system, converted into a full-stack web app with persistent storage.

---

## What It Does

The system uses computer vision to analyze a live camera feed and flag suspicious behavior. It tracks eye movement, gaze direction, and head pose simultaneously, calculates a suspicion score, and stores all events in a database for later review.

**Core detection features:**
- **Eye movement tracking** — detects when eyes move away from the screen
- **Gaze estimation** — determines where the user is looking based on iris position
- **Head pose estimation** — detects head turns and tilts using 3D face modeling
- **Multi-face detection** — flags when more than one person is in the frame
- **No-face detection** — flags when the user leaves the frame entirely
- **Calibration system** — personalizes detection thresholds to each user before monitoring begins
- **Suspicion scoring** — weighted scoring system that accumulates alert events over a 60-second rolling window

**Recommendation levels:**
| Score | Result |
|-------|--------|
| 0–29 | ✅ Accept |
| 30–69 | ⚠️ Review |
| 70–100 | ❌ Flag for review |

---

## Architecture

The server opens the camera directly and runs all processing in a background thread — the same way the original desktop code worked. The browser only receives the processed frame and stats, with no upload latency.

```
Server → Camera (direct) → MediaPipe → Processed Frame → Browser
```

---

## Setup

```bash
cd joblens
pip install -r requirements.txt
```

## Run

```bash
python -m uvicorn main:app --host 0.0.0.0 --port 8000 --reload
```

Then open: **http://localhost:8000**

---

## Project Structure

```
joblens/
├── main.py              # FastAPI backend + all ML processing
├── requirements.txt
├── joblens.db           # SQLite database (auto-created on first run)
├── templates/
│   └── index.html       # Frontend UI
├── static/              # Static assets
└── outputs/             # Auto-generated reports and alert frames
    └── alert_frames/
```

---

## Database

| Table | Description |
|-------|-------------|
| `sessions` | One record per monitoring session with final score and recommendation |
| `alerts` | Every alert event with type and timestamp |
| `score_history` | Suspicion score sampled throughout the session |

---

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/` | Web interface |
| POST | `/api/sessions/start` | Start a new session |
| POST | `/api/sessions/{id}/end` | End session and save to database |
| GET | `/api/sessions` | List all past sessions |
| GET | `/api/sessions/{id}/report` | Full report for a session |
| WS | `/ws/{session_id}` | Live stream of processed frames and stats |

---

## Tech Stack

- **Backend** — FastAPI, Python
- **Computer Vision** — OpenCV, MediaPipe Face Mesh
- **Database** — SQLite via SQLAlchemy
- **Frontend** — Vanilla HTML/CSS/JS over WebSocket
