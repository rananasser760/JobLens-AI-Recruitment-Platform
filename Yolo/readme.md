# JobLens Online Interview Monitoring using YOLOv8

This project is part of the **JobLens AI Recruitment Platform** and implements an **Automated Online Interview Monitoring System** using **YOLOv8**.

It monitors candidates during online interviews in real-time, detecting:

- Multiple candidates in front of the camera
- No candidate detected
- Cheating items (e.g., mobile phones)

All events are logged in a JSON file with timestamps and detailed frame statistics.

---

## Features

- Real-time video capture and analysis
- Person detection using YOLOv8 (COCO model)
- Custom object detection for mobile phones
- Automatic alerts for:
  - Multiple people
  - No candidate
  - Mobile phone detected
- Frame-by-frame statistics:
  - Total frames
  - Secure frames
  - Cheating frames
  - Breakdown by alert type
- Cooldown system to avoid repeated alerts in short time
- Live annotated display for monitoring
- JSON export for audit and review

---

## Requirements

Install the required Python packages:

```bash
pip install -r requirements.txt
```

---

## Usage

- Ensure a working webcam is connected. 
- Place the YOLO models in the project directory:
- yolov8n.pt (person detection)
- phones.pt (mobile detection)
- Run the monitoring script:
```bash
python yolo.py
```
---
