import cv2
import json
import time
from datetime import datetime
from ultralytics import YOLO

# ==============================
# إعدادات
# ==============================
SESSION_ID = datetime.now().strftime("%Y%m%d_%H%M%S")

OUTPUT_JSON = f"cheating_log_{SESSION_ID}.json"

# ==============================
# تحميل الموديلات
# ==============================
try:
    model_coco = YOLO('yolov8n.pt')
    model_custom = YOLO('phones.pt')
except Exception as e:
    print(f"Error loading models: {e}")
    exit()

videoCap = cv2.VideoCapture(0)

COCO_CLASSES = {0: 'Person'}

# ==============================
# بيانات الجلسة
# ==============================
session_data = {
    "session_id": SESSION_ID,
    "start_time": datetime.now().isoformat(),
    "end_time": None,
    "total_alerts": 0,
    "events": [],
    "frame_statistics": {}   # هيتحط في النهاية
}

# للتحكم في التسجيل
COOLDOWN_SECONDS = 2
last_alert_type = None
last_alert_logged_time = 0

# ==============================
# Frame Counters
# ==============================
frame_counters = {
    "total_frames": 0,
    "secure_frames": 0,
    "cheating_frames": 0,         # أي نوع غش
    "MULTIPLE_PEOPLE_frames": 0,
    "NO_CANDIDATE_frames": 0,
    "CHEATING_ITEM_MOBILE_frames": 0,
}

def log_event(alert_type: str, details: dict):
    """تسجيل حدث غش في الـ session data"""
    global session_data
    
    event = {
        "event_id": len(session_data["events"]) + 1,
        "timestamp": datetime.now().isoformat(),
        "elapsed_seconds": round(time.time() - start_time, 2),
        "alert_type": alert_type,
        "details": details
    }
    session_data["events"].append(event)
    session_data["total_alerts"] = len(session_data["events"])
    print(f"[LOG] {event['timestamp']} | {alert_type} | {details}")
    return event

def save_json():
    """حساب إحصائيات الفرامز وحفظ الـ JSON"""
    session_data["end_time"] = datetime.now().isoformat()

    total = frame_counters["total_frames"]

    def pct(n):
        return round((n / total * 100), 2) if total > 0 else 0.0

    session_data["frame_statistics"] = {
        "total_frames": total,
        "secure_frames": frame_counters["secure_frames"],
        "cheating_frames": frame_counters["cheating_frames"],
        "cheating_percentage": pct(frame_counters["cheating_frames"]),
        "secure_percentage": pct(frame_counters["secure_frames"]),
        "breakdown": {
            "MULTIPLE_PEOPLE": {
                "frames": frame_counters["MULTIPLE_PEOPLE_frames"],
                "percentage": pct(frame_counters["MULTIPLE_PEOPLE_frames"])
            },
            "NO_CANDIDATE": {
                "frames": frame_counters["NO_CANDIDATE_frames"],
                "percentage": pct(frame_counters["NO_CANDIDATE_frames"])
            },
            "CHEATING_ITEM_MOBILE": {
                "frames": frame_counters["CHEATING_ITEM_MOBILE_frames"],
                "percentage": pct(frame_counters["CHEATING_ITEM_MOBILE_frames"])
            }
        }
    }

    with open(OUTPUT_JSON, "w", encoding="utf-8") as f:
        json.dump(session_data, f, ensure_ascii=False, indent=2)

    stats = session_data["frame_statistics"]
    print(f"\n✅ تم حفظ السجل في: {OUTPUT_JSON}")
    print(f"📊 إجمالي الفرامز     : {stats['total_frames']}")
    print(f"🟢 فرامز آمنة         : {stats['secure_frames']} ({stats['secure_percentage']}%)")
    print(f"🔴 فرامز غش           : {stats['cheating_frames']} ({stats['cheating_percentage']}%)")
    print(f"   ↳ أكتر من شخص     : {stats['breakdown']['MULTIPLE_PEOPLE']['frames']} ({stats['breakdown']['MULTIPLE_PEOPLE']['percentage']}%)")
    print(f"   ↳ مفيش مرشح       : {stats['breakdown']['NO_CANDIDATE']['frames']} ({stats['breakdown']['NO_CANDIDATE']['percentage']}%)")
    print(f"   ↳ موبايل           : {stats['breakdown']['CHEATING_ITEM_MOBILE']['frames']} ({stats['breakdown']['CHEATING_ITEM_MOBILE']['percentage']}%)")

# ==============================
# الـ Main Loop
# ==============================
start_time = time.time()

while videoCap.isOpened():
    ret, frame = videoCap.read()
    if not ret:
        break

    person_count = 0
    cheating_items = []
    detected_objects = []

    # --- كشف الأشخاص ---
    results_coco = model_coco.predict(frame, conf=0.6, verbose=False)
    for result in results_coco:
        for box in result.boxes:
            cls = int(box.cls[0])
            if cls in COCO_CLASSES:
                label = COCO_CLASSES[cls]
                x1, y1, x2, y2 = map(int, box.xyxy[0])
                conf = round(float(box.conf[0]), 2)

                if cls == 0:
                    person_count += 1
                    color = (0, 255, 0)
                    detected_objects.append({"type": "Person", "confidence": conf})
                else:
                    cheating_items.append(label)
                    color = (0, 0, 255)

                cv2.rectangle(frame, (x1, y1), (x2, y2), color, 2)
                cv2.putText(frame, f"{label} {conf}", (x1, y1 - 10),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.5, color, 2)

    # --- كشف الموبايلات ---
    results_custom = model_custom.predict(frame, conf=0.5, verbose=False)
    for result in results_custom:
        for box in result.boxes:
            x1, y1, x2, y2 = map(int, box.xyxy[0])
            conf = round(float(box.conf[0]), 2)
            cheating_items.append("Mobile")
            detected_objects.append({"type": "Mobile", "confidence": conf})

            cv2.rectangle(frame, (x1, y1), (x2, y2), (0, 0, 255), 2)
            cv2.putText(frame, f"Mobile {conf}", (x1, y1 - 10),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 2)

    # --- منطق التنبيه ---
    alert_msg = "Status: Secure"
    msg_color = (0, 255, 0)
    current_alert_type = None
    alert_details = {}

    if person_count > 1:
        current_alert_type = "MULTIPLE_PEOPLE"
        alert_msg = f"ALERT: {person_count} PEOPLE DETECTED!"
        msg_color = (0, 0, 255)
        alert_details = {"person_count": person_count, "detected": detected_objects}

    elif person_count == 0:
        current_alert_type = "NO_CANDIDATE"
        alert_msg = "ALERT: NO CANDIDATE!"
        msg_color = (0, 0, 255)
        alert_details = {"person_count": 0}

    elif len(cheating_items) > 0:
        current_alert_type = f"CHEATING_ITEM_{cheating_items[0].upper()}"
        alert_msg = f"ALERT: {cheating_items[0]} DETECTED!"
        msg_color = (0, 0, 255)
        alert_details = {
            "items_detected": cheating_items,
            "person_count": person_count,
            "detected_objects": detected_objects
        }

    # --- عدّ الفرامز ---
    frame_counters["total_frames"] += 1
    if current_alert_type is None:
        frame_counters["secure_frames"] += 1
    else:
        frame_counters["cheating_frames"] += 1
        key = f"{current_alert_type}_frames"
        if key in frame_counters:
            frame_counters[key] += 1

    # --- تسجيل الأحداث (مع cooldown عشان متكررش كتير) ---
    current_time = time.time()
    if current_alert_type is not None:
        time_since_last = current_time - last_alert_logged_time
        is_new_type = current_alert_type != last_alert_type

        if is_new_type or time_since_last >= COOLDOWN_SECONDS:
            log_event(current_alert_type, alert_details)
            last_alert_type = current_alert_type
            last_alert_logged_time = current_time
    else:
        # رجع للوضع الطبيعي
        if last_alert_type is not None:
            last_alert_type = None

    # --- العرض ---
    cv2.putText(frame, alert_msg, (20, 50),
                cv2.FONT_HERSHEY_SIMPLEX, 0.9, msg_color, 3)

    elapsed = int(time.time() - start_time)
    total_f = frame_counters["total_frames"]
    cheat_pct = round(frame_counters["cheating_frames"] / total_f * 100, 1) if total_f > 0 else 0.0
    cv2.putText(frame, f"Session: {SESSION_ID} | Time: {elapsed}s | Cheat Frames: {cheat_pct}%",
                (10, frame.shape[0] - 10),
                cv2.FONT_HERSHEY_SIMPLEX, 0.4, (200, 200, 200), 1)

    cv2.imshow('Exam Monitor', frame)

    if cv2.waitKey(1) & 0xFF == ord('q'):
        break

# ==============================
# حفظ الـ JSON عند الانتهاء
# ==============================
videoCap.release()
cv2.destroyAllWindows()
save_json()