# 🎯 JobLens — AI Cheating Detection Engine

> **Part of the [JobLens](https://github.com/) recruitment platform** — an end-to-end AI-powered hiring suite integrating candidate automation, smart interviews, and integrity monitoring.

---

## 📌 Overview

**JobLens Integrity Monitor** is a real-time, computer-vision-based cheating detection engine designed for remote hiring assessments. It continuously analyzes the candidate's video feed to detect suspicious behaviors using a multi-layered approach: facial landmark tracking, gaze estimation, head pose analysis, eye movement calibration, and YOLO-based object detection.

Sessions are logged to a SQLite database, scored using an intelligent exponential decay model, and served through a FastAPI web backend with live WebSocket streaming — giving recruiters a clear, auditable record of each candidate's behavior.

---

## ✨ Key Features

### 👤 Candidate Side
- ATS-friendly CV analysis and feedback  
- Automated job application assistance  
- Skill extraction and profile optimization  

### 🧠 AI Interview System
- Speech-to-Text (STT) for candidate responses  
- LLM-based dynamic question generation  
- Text-to-Speech (TTS) for conversational interviews  

### 🛡️ Integrity Monitoring
- Computer vision-based gaze and behavior analysis  
- Browser activity monitoring during assessments  
- Fair and secure remote evaluation  

---

## 🌿 Repository Branches

| Branch | Description |
|------|------------|
| `main` | Stable version of the project |
| `feature/candidate-automation` | Candidate-side automation & ATS features |
| `feature/ai-interview-engine` | STT, TTS, and AI question generation |
| `feature/integrity-monitoring` | Cheating detection & proctoring system |

---

## 🛠️ Tech Stack

**Backend**
- Python (FastAPI)

**Frontend**
- React
- JavaScript

**AI & NLP**
- SBERT, Hugging Face  
- Large Language Models (OpenAI / LLaMA)

**Speech Processing**
- Whisper / Google STT  
- ElevenLabs / Google TTS  

**Computer Vision**
- OpenCV  
- MediaPipe  

**Databases**
- PostgreSQL  
- ChromaDB (Vector Search)

**DevOps**
- Docker  
- Git & GitHub  

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
