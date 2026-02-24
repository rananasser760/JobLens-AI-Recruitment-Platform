# 🎯 JobLens — AI Cheating Detection Engine

> **Part of the [JobLens](https://github.com/) recruitment platform** — an end-to-end AI-powered hiring suite integrating candidate automation, smart interviews, and integrity monitoring.

---

## 📌 Overview

**JobLens Integrity Monitor** is a real-time, computer-vision-based cheating detection engine designed for remote hiring assessments. It continuously analyzes the candidate's video feed to detect suspicious behaviors using a multi-layered approach: facial landmark tracking, gaze estimation, head pose analysis, eye movement calibration, and YOLO-based object detection.

Sessions are logged to a SQLite database, scored using an intelligent exponential decay model, and served through a FastAPI web backend with live WebSocket streaming — giving recruiters a clear, auditable record of each candidate's behavior.

---

## ✨ Key Features

| Feature | Description |
|---|---|
| 🧠 **Gaze Estimation** | Tracks where the candidate is looking using iris landmark positions |
| 👁️ **Eye Movement Tracking** | Calibrated baseline detection for left/right/up/down eye shifts |
| 🗣️ **Head Pose Estimation** | 6-DOF head pose via PnP solver (yaw, pitch, roll) with per-session calibration |
| 📱 **YOLO Object Detection** | Detects mobile phones and multiple people using YOLOv8 + custom model |
| 📊 **Exponential Decay Scoring** | Recent alerts are weighted more heavily; score decays slowly over time |
| 🔒 **Privacy-Preserving Keyframes** | Blurred, thumbnail-sized JPEG snapshots saved per alert event |
| 👤 **Candidate Profiles** | Name and ID stored per session for recruiter-facing reporting |
| ⏱️ **Session Timeline Replay** | Full alert timeline with keyframe previews for post-session review |
| 🌐 **WebSocket Live Feed** | Real-time frame streaming and live log broadcast to the dashboard |
| 📄 **Session Reports** | Auto-generated text reports with score breakdown and recommendation |

---

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────┐
│                   FastAPI Backend                   │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────┐  │
│  │  Camera Loop │  │  YOLO Thread │  │ REST API │  │
│  │  (MediaPipe) │  │  (YOLOv8n)   │  │ /ws/{id} │  │
│  └──────┬───────┘  └──────┬───────┘  └────┬─────┘  │
│         │                 │               │         │
│  ┌──────▼─────────────────▼───────────────▼──────┐  │
│  │              AlertManager                     │  │
│  │   • Exponential Decay Scoring                 │  │
│  │   • Cooldown Gating + Score Floors            │  │
│  │   • Keyframe Capture (blurred JPEG)           │  │
│  └──────────────────────┬────────────────────────┘  │
│                         │                           │
│              ┌──────────▼──────────┐                │
│              │   SQLite Database   │                │
│              │  sessions / alerts  │                │
│              │  keyframes / scores │                │
│              └─────────────────────┘                │
└─────────────────────────────────────────────────────┘
```

---

## 🔍 Detection Modules

### 1. Face Presence
- **No face detected** → `NO_FACE` alert (weight: 6)
- **Multiple faces in frame** → `MULTIPLE_FACES` alert (weight: 9)

### 2. Gaze Estimation
Estimates combined gaze direction from both irises using MediaPipe landmarks. Alerts are triggered when gaze deviates beyond configurable thresholds.

| Alert | Condition |
|---|---|
| `LOOKING_LEFT` / `LOOKING_RIGHT` | Yaw deviation > ±20° |
| `LOOKING_UP` | Pitch < −18° |
| `LOOKING_DOWN` | Pitch > 20° |

### 3. Head Pose (PnP)
6-point head pose solved via `solvePnP` against a normalized 3D face model. Per-session baseline calibration eliminates natural posture bias.

| Alert | Condition |
|---|---|
| `HEAD_TURNED_LEFT` / `HEAD_TURNED_RIGHT` | Yaw > ±35° |
| `HEAD_TILTED_UP` / `HEAD_TILTED_DOWN` | Pitch > ±18° |
| `HEAD_TILTED_SIDE` | Roll > ±22° |

### 4. Eye Movement (Calibrated)
Iris position is tracked relative to a per-session baseline captured during the calibration phase. A 3-frame weighted smoothing window reduces noise.

| Alert | Condition |
|---|---|
| `EYE_LEFT` / `EYE_RIGHT` | Horizontal shift > ±0.22 |
| `EYE_UP` | Vertical shift > −0.20 |
| `EYE_DOWN` | Vertical shift > 0.20 |

### 5. YOLO Object Detection
Two YOLOv8 models run in a dedicated thread:
- `yolov8n.pt` — COCO person detection
- `phones.pt` — Custom mobile phone detection

A **sliding-window vote** confirms alerts across multiple frames to prevent single-frame false positives. IoU-based deduplication prevents the same object from being counted twice.

| Alert | Trigger | Weight |
|---|---|---|
| `MULTIPLE_PEOPLE` | ≥2 persons confirmed | 20 |
| `CHEATING_ITEM_MOBILE` | Phone ≥ 0.3% of frame area, confirmed | 25 |

---

## 📐 Scoring System

The suspicion score is a normalized value between 0–100 based on three components working together:

### 1. Sliding Window + Exponential Decay
Only alerts within the last **180 seconds** are counted. Each alert's contribution decays exponentially with a half-life of **200 seconds**, so recent behavior matters more than old behavior.

```
score_contribution = weight × e^(−λ × age)
where λ = ln(2) / 200
```

### 2. Cooldown Freeze
For **30 seconds** after the last alert, the score is held steady rather than immediately decaying. This reflects that a candidate who just got caught doesn't instantly become trustworthy.

### 3. Behavior Memory (Score Floors)
Once a candidate's score reaches certain peak thresholds, the score can never fall below a floor — representing permanent behavioral history.

| Peak Reached | Score Floor |
|---|---|
| ≥ 80 | 78 |
| ≥ 60 | 58 |
| ≥ 40 | 38 |

### Recommendations

| Score Range | Recommendation |
|---|---|
| 0 – 29 | ✅ **ACCEPT** — Low suspicion |
| 30 – 69 | ⚠️ **REVIEW** — Manual review recommended |
| 70 – 100 | ❌ **REJECT** — High suspicion, likely cheating |

---

## 🛠️ Tech Stack

| Layer | Technology |
|---|---|
| Computer Vision | [MediaPipe](https://google.github.io/mediapipe/) Face Mesh + Iris |
| Object Detection | [Ultralytics YOLOv8](https://docs.ultralytics.com/) |
| Image Processing | OpenCV |
| Web Backend | [FastAPI](https://fastapi.tiangolo.com/) |
| Database | SQLite via [SQLAlchemy](https://www.sqlalchemy.org/) |
| Real-time Streaming | WebSocket |
| Charting | Matplotlib |
| Numerical Computing | NumPy |

---

## 🚀 Getting Started

### Prerequisites

- Python 3.9+
- Webcam
- `phones.pt` — custom YOLO phone detection model (place in project root)

### Installation

```bash
# Clone the repository
git clone https://github.com/your-org/joblens.git
cd joblens/integrity-monitor

# Install dependencies
pip install -r requirements.txt
```

**`requirements.txt` (core dependencies)**
```
fastapi
uvicorn
opencv-python
mediapipe
numpy
matplotlib
sqlalchemy
ultralytics
pydantic
```

### Running the Web App

```bash
uvicorn main:app --reload --host 0.0.0.0 --port 8000
```

Then open your browser at `http://localhost:8000`.

### Running in Desktop Mode

```bash
python main.py
```

**Keyboard shortcuts (desktop mode):**

| Key | Action |
|---|---|
| `q` | Quit and save final report |
| `r` | Export session report |
| `t` | Show timeline chart |
| `d` | Toggle debug mode |

---

## 🗂️ Project Structure

```
integrity-monitor/
├── main.py                    # Core application (all modules)
├── templates/
│   ├── index.html             # Live monitoring UI
│   └── dashboard.html         # Session history dashboard
├── static/                    # Frontend assets
├── outputs/                   # Generated reports, alert frames, JSON logs
│   ├── alert_frames/          # Saved alert frame images
│   └── session_*_report.txt   # Per-session text reports
└── joblens.db                 # SQLite database
```

---

## 🌐 API Reference

### Sessions

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/api/sessions/start` | Start a new monitoring session |
| `POST` | `/api/sessions/{id}/end` | End a session and finalize score |
| `GET` | `/api/sessions` | List recent sessions |
| `GET` | `/api/sessions/{id}/report` | Full session report with timeline |
| `GET` | `/api/sessions/{id}/pdf` | Download session report as text |

### Keyframes

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/keyframes/{id}` | Fetch a blurred keyframe JPEG |

### WebSocket

| Endpoint | Description |
|---|---|
| `WS /ws/{session_id}` | Live frame + state stream (20ms interval) |
| `WS /ws/logs/{session_id}` | Live alert log stream with history replay |

### Start Session Request Body

```json
{
  "candidate_name": "John smith",
  "candidate_id": "John-2026"
}
```

---

## ⚙️ Configuration

All parameters are centralized in the `Config` class in `main.py`. Key settings:

```python
# Detection thresholds
GAZE_YAW_THRESHOLD       = 20    # degrees
HEAD_YAW_THRESHOLD       = 35    # degrees
EYE_MOVEMENT_LEFT_THRESHOLD = 0.22

# Scoring
SCORING_WINDOW_SECONDS   = 180   # seconds of history considered
DECAY_HALF_LIFE          = 200.0 # seconds
SCORE_COOLDOWN_SECS      = 30    # freeze duration after last alert

# YOLO
PHONE_CONFIDENCE         = 0.80
PERSON_CONFIDENCE        = 0.65

# Privacy
BLUR_FACE                = True  # blur faces in saved frames
KEYFRAME_JPEG_QUALITY    = 40    # lower = smaller DB footprint
KEYFRAME_BLUR_KERNEL     = 35    # Gaussian blur strength
```

---

## 🔐 Privacy & Consent

- All saved alert frames are **Gaussian-blurred** before storage to protect candidate identity.
- Keyframe thumbnails are capped at **320px width** to minimize data footprint.
- A **consent notice** is displayed before monitoring begins in desktop mode.
- Face blur can be disabled via `Config.BLUR_FACE = False` for environments where it is not required.

---

## 📊 Database Schema

```
sessions         → id, candidate_name, candidate_id, started_at, ended_at,
                   final_score, recommendation, duration_seconds

alerts           → id, session_id, alert_type, timestamp, elapsed_secs, metadata_json

yolo_alerts      → id, session_id, alert_type, timestamp, elapsed_secs, details_json

score_history    → id, session_id, timestamp, score

keyframes        → id, session_id, alert_type, timestamp, elapsed_secs, image_data (BLOB)
```

---

## 🗺️ Roadmap

- [ ] Audio anomaly detection (voice recognition of off-screen speakers)
- [ ] Multi-camera support
- [ ] LLM-generated natural language session summaries
- [ ] Export to PDF with embedded timeline charts
- [ ] Admin dashboard with bulk session comparison
- [ ] Integration with JobLens ATS for automatic disqualification workflows

---

## 🤝 Contributing

This module is part of the closed **JobLens** platform. For contribution guidelines, internal access, or licensing inquiries, please contact the project maintainers.

---

*Built with ❤️ as part of the JobLens AI Recruitment Platform.*
