# ══════════════════════════════════════════════════════════════════════════════
#  JobLens AI — Web App
#  Original detection code preserved exactly as-is.
#  Only the FastAPI / Database / WebSocket layer is added around it.
# ══════════════════════════════════════════════════════════════════════════════ 

import cv2
import mediapipe as mp
import numpy as np
import time
import os
import math
from datetime import datetime
import matplotlib
matplotlib.use('Agg')  
import matplotlib.pyplot as plt
from matplotlib.backends.backend_agg import FigureCanvasAgg

# ── Web / DB imports (added for web app) ─────────────────────────────────────
import json
import asyncio
from fastapi import FastAPI, WebSocket, WebSocketDisconnect, HTTPException
from fastapi.staticfiles import StaticFiles
from fastapi.responses import HTMLResponse
from fastapi.middleware.cors import CORSMiddleware
from sqlalchemy import create_engine, Column, Integer, Float, String, DateTime, ForeignKey, Text
from sqlalchemy.ext.declarative import declarative_base
from sqlalchemy.orm import sessionmaker, relationship, Session as DBSessionType

# ══════════════════════════════════════════════════════════════════════════════
#  ORIGINAL CODE — NOT MODIFIED
# ══════════════════════════════════════════════════════════════════════════════

class Config:
    CAMERA_INDEX = 0
    FRAME_WIDTH = 640
    FRAME_HEIGHT = 480
    FPS = 30

    MIN_DETECTION_CONFIDENCE = 0.5
    MIN_TRACKING_CONFIDENCE = 0.5
    MAX_NUM_FACES = 2
    REFINE_LANDMARKS = True

    GAZE_YAW_THRESHOLD = 20  
    GAZE_PITCH_UP_THRESHOLD = 18  
    GAZE_PITCH_DOWN_THRESHOLD = 20  

    HEAD_YAW_THRESHOLD = 35  
    HEAD_PITCH_UP_THRESHOLD = 18 
    HEAD_PITCH_DOWN_THRESHOLD = 18  
    HEAD_ROLL_THRESHOLD = 22  

    
    EYE_MOVEMENT_LEFT_THRESHOLD = 0.22   
    EYE_MOVEMENT_RIGHT_THRESHOLD = 0.22  
    EYE_MOVEMENT_UP_THRESHOLD = 0.20     
    EYE_MOVEMENT_DOWN_THRESHOLD = 0.20   

    ALERT_COOLDOWN = 2 
    SCORING_WINDOW_SECONDS = 60

    ALERT_WEIGHTS = {
        'NO_FACE':          12,
        'MULTIPLE_FACES':   18,
        'LOOKING_LEFT':      5,  
        'LOOKING_RIGHT':     5,
        'LOOKING_UP':        6,
        'LOOKING_DOWN':      5,
        'HEAD_TURNED_LEFT':  5,
        'HEAD_TURNED_RIGHT': 5,
        'HEAD_TILTED_UP':    6,
        'HEAD_TILTED_DOWN':  5,
        'HEAD_TILTED_SIDE':  3,
        'EYE_LEFT':          8,  
        'EYE_RIGHT':         8,
        'EYE_UP':            7,
        'EYE_DOWN':          6,
    }

    MAX_RAW_SCORE_FOR_NORMALIZATION = 150
    EAR_THRESHOLD = 0.25

    GREEN  = (0, 220, 0)
    YELLOW = (0, 210, 210)
    RED    = (0, 0, 220)
    WHITE  = (240, 240, 240)
    PANEL_BG = (40, 42, 46)

    PANEL_WIDTH = 220
    INFO_FONT = 0.48
    INFO_THICKNESS = 1
    TITLE_FONT = 0.6
    TITLE_THICKNESS = 2

    LEFT_EYE        = [33, 160, 158, 133, 153, 144]
    LEFT_EYE_IRIS   = [468, 469, 470, 471, 472]
    RIGHT_EYE       = [362, 385, 387, 263, 373, 380]
    RIGHT_EYE_IRIS  = [473, 474, 475, 476, 477]

    NOSE_TIP         = 1
    CHIN             = 152
    LEFT_EYE_CORNER  = 33
    RIGHT_EYE_CORNER = 263
    LEFT_MOUTH       = 61
    RIGHT_MOUTH      = 291

    SAVE_ALERTS = True
    ALERT_FRAMES_DIR = "outputs/alert_frames"

    CALIBRATION_FRAMES = 70 

    BLUR_FACE = True
    BLUR_KERNEL_SIZE = 51

    CONSENT_NOTICE = True
    
    DEBUG_MODE = False


class FaceDetector:
    def __init__(self):
        self._mesh = mp.solutions.face_mesh.FaceMesh(
            max_num_faces=Config.MAX_NUM_FACES,
            refine_landmarks=Config.REFINE_LANDMARKS,
            min_detection_confidence=Config.MIN_DETECTION_CONFIDENCE,
            min_tracking_confidence=Config.MIN_TRACKING_CONFIDENCE,
        )
        self._drawing = mp.solutions.drawing_utils
        self._styles  = mp.solutions.drawing_styles

    def detect(self, frame):
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        rgb.flags.writeable = False
        return self._mesh.process(rgb)

    @staticmethod
    def face_count(results) -> int:
        if results.multi_face_landmarks:
            return len(results.multi_face_landmarks)
        return 0

    def get_eye_data(self, face_landmarks, img_w, img_h) -> dict:
        def _pts(indices):
            out = []
            for i in indices:
                lm = face_landmarks.landmark[i]
                out.append((int(lm.x * img_w), int(lm.y * img_h)))
            return np.array(out)
        return {
            'left_eye':   _pts(Config.LEFT_EYE),
            'right_eye':  _pts(Config.RIGHT_EYE),
            'left_iris':  _pts(Config.LEFT_EYE_IRIS),
            'right_iris': _pts(Config.RIGHT_EYE_IRIS),
        }

    def get_face_bbox(self, face_landmarks, img_w, img_h):
        xs = [int(lm.x * img_w) for lm in face_landmarks.landmark]
        ys = [int(lm.y * img_h) for lm in face_landmarks.landmark]
        x_min, x_max = max(0, min(xs) - 20), min(img_w, max(xs) + 20)
        y_min, y_max = max(0, min(ys) - 20), min(img_h, max(ys) + 20)
        return x_min, y_min, x_max, y_max

    def draw_mesh(self, frame, results):
        if not results.multi_face_landmarks:
            return frame
        for lms in results.multi_face_landmarks:
            self._drawing.draw_landmarks(frame, lms,
                mp.solutions.face_mesh.FACEMESH_CONTOURS,
                landmark_drawing_spec=None,
                connection_drawing_spec=self._styles.get_default_face_mesh_contours_style())
            self._drawing.draw_landmarks(frame, lms,
                mp.solutions.face_mesh.FACEMESH_IRISES,
                landmark_drawing_spec=None,
                connection_drawing_spec=self._styles.get_default_face_mesh_iris_connections_style())
        return frame

    def close(self):
        self._mesh.close()


class EyeMovementTracker:
    
    def __init__(self):
        self._baseline_left = None
        self._baseline_right = None
        self._cal_samples_left = []
        self._cal_samples_right = []
        self._calibrated = False
        self._history_size = 3  
        self._history = []
        self._last_movement = {"h": 0.0, "v": 0.0}

    def _compute_iris_position(self, eye_pts, iris_pts):
        
        e_left   = eye_pts[:, 0].min()
        e_right  = eye_pts[:, 0].max()
        e_top    = eye_pts[:, 1].min()
        e_bottom = eye_pts[:, 1].max()
        
        e_w = e_right - e_left
        e_h = e_bottom - e_top
        
        if e_w == 0 or e_h == 0:
            return 0.0, 0.0
        
        
        iris_cx = np.median(iris_pts[:, 0])
        iris_cy = np.median(iris_pts[:, 1])
        
        eye_center_x = (e_left + e_right) / 2.0
        eye_center_y = (e_top + e_bottom) / 2.0
        
        
        h = (iris_cx - eye_center_x) / (e_w / 2.0)
        v = (iris_cy - eye_center_y) / (e_h / 2.0)
        
        return float(np.clip(h, -1, 1)), float(np.clip(v, -1, 1))

    def calibrate(self, left_eye, left_iris, right_eye, right_iris):
        
        lh, lv = self._compute_iris_position(left_eye, left_iris)
        rh, rv = self._compute_iris_position(right_eye, right_iris)
        
        self._cal_samples_left.append((lh, lv))
        self._cal_samples_right.append((rh, rv))
        
        if len(self._cal_samples_left) >= Config.CALIBRATION_FRAMES:
            
            left_h = [s[0] for s in self._cal_samples_left]
            left_v = [s[1] for s in self._cal_samples_left]
            right_h = [s[0] for s in self._cal_samples_right]
            right_v = [s[1] for s in self._cal_samples_right]
            
            self._baseline_left = (
                float(np.median(left_h)),
                float(np.median(left_v))
            )
            self._baseline_right = (
                float(np.median(right_h)),
                float(np.median(right_v))
            )
            
            self._calibrated = True
            print(f"✓ Eye calibration complete: Left={self._baseline_left}, Right={self._baseline_right}")
            return True
        return False

    @property
    def is_calibrated(self):
        return self._calibrated

    @property
    def calibration_progress(self):
        return min(len(self._cal_samples_left) / Config.CALIBRATION_FRAMES, 1.0)

    def track(self, left_eye, left_iris, right_eye, right_iris):
        
        if not self._calibrated:
            return 0.0, 0.0, "CENTER", None

        lh, lv = self._compute_iris_position(left_eye, left_iris)
        rh, rv = self._compute_iris_position(right_eye, right_iris)

        
        lh_delta = lh - self._baseline_left[0]
        lv_delta = lv - self._baseline_left[1]
        rh_delta = rh - self._baseline_right[0]
        rv_delta = rv - self._baseline_right[1]

        
        h_movement = (lh_delta + rh_delta) / 2.0
        v_movement = (lv_delta + rv_delta) / 2.0

        
        self._history.append((h_movement, v_movement))
        if len(self._history) > self._history_size:
            self._history.pop(0)

        
        weights = np.array([0.2, 0.3, 0.5])[:len(self._history)]
        weights = weights / weights.sum()
        
        smooth_h = float(np.average([p[0] for p in self._history], weights=weights))
        smooth_v = float(np.average([p[1] for p in self._history], weights=weights))

        direction, alert = self._classify_movement(smooth_h, smooth_v)
        
        
        self._last_movement = {"h": smooth_h, "v": smooth_v}
        
        if Config.DEBUG_MODE and alert:
            print(f"EYE ALERT: {alert} | H={smooth_h:.3f}, V={smooth_v:.3f}")
        
        return smooth_h, smooth_v, direction, alert

    @staticmethod
    def _classify_movement(h_movement, v_movement):
        
        if abs(h_movement) > abs(v_movement):
            if h_movement > Config.EYE_MOVEMENT_RIGHT_THRESHOLD:
                return "RIGHT", "EYE_RIGHT"
            if h_movement < -Config.EYE_MOVEMENT_LEFT_THRESHOLD:
                return "LEFT", "EYE_LEFT"
        
        
        if v_movement < -Config.EYE_MOVEMENT_UP_THRESHOLD:
            return "UP", "EYE_UP"
        if v_movement > Config.EYE_MOVEMENT_DOWN_THRESHOLD:
            return "DOWN", "EYE_DOWN"
        
        return "CENTER", None


class GazeEstimator:

    def __init__(self):
        self._history = []
        self._history_size = 5  

    def estimate(self, eye_pts, iris_pts):
        e_left   = eye_pts[:, 0].min()
        e_right  = eye_pts[:, 0].max()
        e_top    = eye_pts[:, 1].min()
        e_bottom = eye_pts[:, 1].max()
        e_w = e_right - e_left
        e_h = e_bottom - e_top
        if e_w == 0 or e_h == 0:
            return 0.0, 0.0
        
        iris_cx = np.median(iris_pts[:, 0])  
        iris_cy = np.median(iris_pts[:, 1])
        
        h = (iris_cx - (e_left + e_right) / 2.0) / (e_w / 2.0)
        v = (iris_cy - (e_top + e_bottom) / 2.0) / (e_h / 2.0)
        return float(np.clip(h, -1, 1)), float(np.clip(v, -1, 1))

    def combined_gaze(self, left_eye, left_iris, right_eye, right_iris):
        lh, lv = self.estimate(left_eye, left_iris)
        rh, rv = self.estimate(right_eye, right_iris)
        
        
        yaw   = ((lh + rh) / 2.0) * 35.0  
        pitch = ((lv + rv) / 2.0) * 30.0  
        
        self._history.append((yaw, pitch))
        if len(self._history) > self._history_size:
            self._history.pop(0)
        
        sy = float(np.mean([p[0] for p in self._history]))
        sp = float(np.mean([p[1] for p in self._history]))
        return sy, sp

    @staticmethod
    def classify(yaw, pitch):
        
        if abs(yaw) > abs(pitch):
            if yaw > Config.GAZE_YAW_THRESHOLD:
                return "RIGHT", "LOOKING_RIGHT"
            if yaw < -Config.GAZE_YAW_THRESHOLD:
                return "LEFT",  "LOOKING_LEFT"
        
        if pitch < -Config.GAZE_PITCH_UP_THRESHOLD:
            return "UP",    "LOOKING_UP"
        if pitch > Config.GAZE_PITCH_DOWN_THRESHOLD:
            return "DOWN",  "LOOKING_DOWN"
        
        return "CENTER", None


class HeadPoseEstimator:
    _MODEL_3D = np.array([
        (0.0,    0.0,    0.0),
        (0.0, -330.0,  -65.0),
        (-225.0, 170.0, -135.0),
        (225.0,  170.0, -135.0),
        (-150.0,-150.0, -125.0),
        (150.0, -150.0, -125.0),
    ], dtype=np.float64)

    _LANDMARK_INDICES = [
        Config.NOSE_TIP, Config.CHIN,
        Config.LEFT_EYE_CORNER, Config.RIGHT_EYE_CORNER,
        Config.LEFT_MOUTH, Config.RIGHT_MOUTH,
    ]

    def __init__(self):
        self._history = []
        self._history_size = 4  
        self._baseline = None
        self._cal_samples = []
        self._calibrated = False

    @staticmethod
    def _normalize_angle(a):
        while a > 180:  a -= 360
        while a < -180: a += 360
        return a

    @staticmethod
    def _compute_angles(rvec):
        rmat, _ = cv2.Rodrigues(rvec)
        nose = rmat @ np.array([0.0, 0.0, -1.0])
        yaw   = math.degrees(math.atan2(-nose[0], -nose[2]))
        pitch = math.degrees(math.atan2(nose[1], math.sqrt(nose[0]**2 + nose[2]**2)))
        up = rmat @ np.array([0.0, -1.0, 0.0])
        roll = math.degrees(math.atan2(-up[0], -up[1]))
        return pitch, yaw, roll

    def _solve(self, face_landmarks, img_w, img_h):
        pts_2d = []
        for idx in self._LANDMARK_INDICES:
            lm = face_landmarks.landmark[idx]
            pts_2d.append([lm.x * img_w, lm.y * img_h])
        pts_2d = np.array(pts_2d, dtype=np.float64)

        cam = np.array([
            [img_w, 0, img_w / 2.0],
            [0, img_w, img_h / 2.0],
            [0, 0, 1.0],
        ], dtype=np.float64)

        ok, rvec, tvec = cv2.solvePnP(
            self._MODEL_3D, pts_2d, cam, np.zeros((4,1)),
            flags=cv2.SOLVEPNP_ITERATIVE
        )
        if not ok:
            return None, None
        return rvec, tvec

    def calibrate(self, face_landmarks, img_w, img_h):
        rvec, _ = self._solve(face_landmarks, img_w, img_h)
        if rvec is None:
            return False
        p, y, r = self._compute_angles(rvec)
        self._cal_samples.append((p, y, r))
        
        if len(self._cal_samples) >= Config.CALIBRATION_FRAMES:
            # استخدام median للدقة
            self._baseline = (
                float(np.median([s[0] for s in self._cal_samples])),
                float(np.median([s[1] for s in self._cal_samples])),
                float(np.median([s[2] for s in self._cal_samples])),
            )
            self._calibrated = True
            print(f"✓ Head calibration complete: {self._baseline}")
            return True
        return False

    @property
    def is_calibrated(self):
        return self._calibrated

    @property
    def calibration_progress(self):
        return min(len(self._cal_samples) / Config.CALIBRATION_FRAMES, 1.0)

    def estimate(self, face_landmarks, img_w, img_h):
        rvec, tvec = self._solve(face_landmarks, img_w, img_h)
        if rvec is None:
            return None, None, None, None, None

        pitch, yaw, roll = self._compute_angles(rvec)

        if self._calibrated and self._baseline:
            pitch = self._normalize_angle(pitch - self._baseline[0])
            yaw   = self._normalize_angle(yaw   - self._baseline[1])
            roll  = self._normalize_angle(roll  - self._baseline[2])

        self._history.append((pitch, yaw, roll))
        if len(self._history) > self._history_size:
            self._history.pop(0)

        sp = float(np.mean([x[0] for x in self._history]))
        sy = float(np.mean([x[1] for x in self._history]))
        sr = float(np.mean([x[2] for x in self._history]))

        return sp, sy, sr, rvec, tvec

    @staticmethod
    def classify(pitch, yaw, roll):
        
        if abs(yaw) > abs(pitch) and abs(yaw) > abs(roll):
            if yaw > Config.HEAD_YAW_THRESHOLD:
                return "TURNED RIGHT", "HEAD_TURNED_RIGHT"
            if yaw < -Config.HEAD_YAW_THRESHOLD:
                return "TURNED LEFT",  "HEAD_TURNED_LEFT"
        
        if abs(pitch) > abs(roll):
            if pitch < -Config.HEAD_PITCH_UP_THRESHOLD:
                return "TILTED UP",    "HEAD_TILTED_UP"
            if pitch > Config.HEAD_PITCH_DOWN_THRESHOLD:
                return "TILTED DOWN",  "HEAD_TILTED_DOWN"
        
        if abs(roll) > Config.HEAD_ROLL_THRESHOLD:
            return "TILTED SIDE",  "HEAD_TILTED_SIDE"
        
        return "FORWARD", None

    @staticmethod
    def draw_axes(frame, rvec, tvec, img_w, img_h):
        cam = np.array([
            [img_w, 0, img_w/2.0],
            [0, img_w, img_h/2.0],
            [0, 0, 1.0],
        ], dtype=np.float64)
        dist = np.zeros((4,1))
        length = 80
        axes_3d = np.float32([[length,0,0],[0,length,0],[0,0,length]])
        origin_2d, _ = cv2.projectPoints(np.array([[0.0,0.0,0.0]]), rvec, tvec, cam, dist)
        o = tuple(origin_2d[0].ravel().astype(int))
        axes_2d, _ = cv2.projectPoints(axes_3d, rvec, tvec, cam, dist)
        cv2.line(frame, o, tuple(axes_2d[0].ravel().astype(int)), (0,0,200), 2)
        cv2.line(frame, o, tuple(axes_2d[1].ravel().astype(int)), (0,200,0), 2)
        cv2.line(frame, o, tuple(axes_2d[2].ravel().astype(int)), (200,0,0), 2)


class AlertManager:
    def __init__(self):
        self._alerts = []
        self._last_time = {}
        self._session_start = time.time()
        self._score_history = []
        if Config.SAVE_ALERTS:
            os.makedirs(Config.ALERT_FRAMES_DIR, exist_ok=True)

    def add(self, alert_type, frame=None, metadata=None):
        now = time.time()
        if alert_type in self._last_time:
            if (now - self._last_time[alert_type]) < Config.ALERT_COOLDOWN:
                return False
        self._last_time[alert_type] = now
        record = {'type': alert_type, 'time': now, 'metadata': metadata or {}}
        if Config.SAVE_ALERTS and frame is not None:
            fname = f"{alert_type}_{int(now*1000)}.jpg"
            path = os.path.join(Config.ALERT_FRAMES_DIR, fname)
            cv2.imwrite(path, frame)
            record['frame_path'] = path
        self._alerts.append(record)
        return True

    def update_score_history(self, score):
        now = time.time()
        self._score_history.append((now, score))
        cutoff = now - Config.SCORING_WINDOW_SECONDS
        self._score_history = [(t, s) for t, s in self._score_history if t >= cutoff]

    def suspicion_score(self):
        now = time.time()
        cutoff = now - Config.SCORING_WINDOW_SECONDS
        raw = 0.0
        for a in self._alerts:
            if a['time'] < cutoff:
                continue
            raw += Config.ALERT_WEIGHTS.get(a['type'], 3)
        return min((raw / Config.MAX_RAW_SCORE_FOR_NORMALIZATION) * 100.0, 100.0)

    def recommendation(self):
        s = self.suspicion_score()
        if s < 30:  return "ACCEPT", "Low suspicion"
        if s < 70:  return "REVIEW", "Medium suspicion — manual review recommended"
        return "REJECT", "High suspicion — potential cheating detected"

    def summary(self):
        now = time.time()
        cutoff = now - Config.SCORING_WINDOW_SECONDS
        breakdown = {}
        for a in self._alerts:
            if a['time'] < cutoff:
                continue
            breakdown[a['type']] = breakdown.get(a['type'], 0) + 1
        return {
            'total_in_window': sum(breakdown.values()),
            'breakdown': breakdown,
            'score': self.suspicion_score(),
            'session_duration': now - self._session_start,
        }

    def get_timeline_data(self):
        return [(a['time'], a['type']) for a in self._alerts]

    def generate_timeline_chart(self):
        if len(self._alerts) == 0:
            return None

        fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(12, 8))
        fig.patch.set_facecolor('#2a2d32')
        
        timeline_data = self.get_timeline_data()
        times = [t - self._session_start for t, _ in timeline_data]
        alert_types = [atype for _, atype in timeline_data]
        
        unique_types = list(set(alert_types))
        colors = plt.cm.Set3(range(len(unique_types)))
        type_to_color = {t: colors[i] for i, t in enumerate(unique_types)}
        
        ax1.set_facecolor('#2a2d32')
        for i, (t, atype) in enumerate(zip(times, alert_types)):
            ax1.scatter(t, i, c=[type_to_color[atype]], s=100, alpha=0.7, edgecolors='white', linewidth=1.5)
        
        ax1.set_xlabel('Time (seconds)', color='white', fontsize=11)
        ax1.set_ylabel('Alert Events', color='white', fontsize=11)
        ax1.set_title('Alert Timeline', color='white', fontsize=13, fontweight='bold')
        ax1.tick_params(colors='white')
        ax1.spines['bottom'].set_color('white')
        ax1.spines['left'].set_color('white')
        ax1.spines['top'].set_visible(False)
        ax1.spines['right'].set_visible(False)
        ax1.grid(True, alpha=0.2, color='white')
        
        if len(self._score_history) > 0:
            score_times = [t - self._session_start for t, _ in self._score_history]
            scores = [s for _, s in self._score_history]
            
            ax2.set_facecolor('#2a2d32')
            ax2.plot(score_times, scores, color='#00dcff', linewidth=2.5, alpha=0.9)
            ax2.fill_between(score_times, scores, alpha=0.3, color='#00dcff')
            
            ax2.axhline(y=30, color='#00dc00', linestyle='--', linewidth=1.5, alpha=0.6, label='Low Threshold')
            ax2.axhline(y=70, color='#dcdc00', linestyle='--', linewidth=1.5, alpha=0.6, label='High Threshold')
            
            ax2.set_xlabel('Time (seconds)', color='white', fontsize=11)
            ax2.set_ylabel('Suspicion Score (%)', color='white', fontsize=11)
            ax2.set_title('Suspicion Score Over Time', color='white', fontsize=13, fontweight='bold')
            ax2.set_ylim(0, 100)
            ax2.tick_params(colors='white')
            ax2.spines['bottom'].set_color('white')
            ax2.spines['left'].set_color('white')
            ax2.spines['top'].set_visible(False)
            ax2.spines['right'].set_visible(False)
            ax2.grid(True, alpha=0.2, color='white')
            ax2.legend(facecolor='#2a2d32', edgecolor='white', labelcolor='white')
        
        plt.tight_layout()
        
        canvas = FigureCanvasAgg(fig)
        canvas.draw()
        buf = np.frombuffer(canvas.buffer_rgba(), dtype=np.uint8)
        w, h = fig.canvas.get_width_height()
        img = buf.reshape(h, w, 4)
        img = cv2.cvtColor(img, cv2.COLOR_RGBA2BGR)
        
        plt.close(fig)
        return img

    def export_report(self, filename="final_report.txt"):
        summ = self.summary()
        rec, reason = self.recommendation()
        lines = [
            "=" * 55,
            "  CHEATING DETECTION — SESSION REPORT",
            "=" * 55,
            f"  Generated : {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
            f"  Duration  : {summ['session_duration']:.1f} s",
            "",
            f"  Score          : {summ['score']:.1f} / 100",
            f"  Recommendation : {rec}",
            f"  Reason         : {reason}",
            "",
            "  Alerts (last 60 s):",
        ]
        for t, c in summ['breakdown'].items():
            lines.append(f"    {t:.<30} {c}")
        lines.append("=" * 55)
        os.makedirs("outputs", exist_ok=True)
        path = os.path.join("outputs", filename)
        with open(path, 'w', encoding='utf-8') as f:
            f.write('\n'.join(lines))
        
        timeline_img = self.generate_timeline_chart()
        if timeline_img is not None:
            chart_path = os.path.join("outputs", "timeline_chart.png")
            cv2.imwrite(chart_path, timeline_img)
        
        return path


class UIRenderer:
    def __init__(self, frame_w, frame_h):
        self.panel_x = frame_w - Config.PANEL_WIDTH
        self.frame_w = frame_w
        self.frame_h = frame_h
        self._consent_accepted = False

    def draw(self, frame, face_count, gaze_dir, gaze_yaw, gaze_pitch,
             head_dir, head_pitch, head_yaw, head_roll, 
             eye_dir, eye_h, eye_v,
             score, current_alert, fps,
             calibrating=False, cal_progress=0.0, show_consent=False):
        
        if show_consent and not self._consent_accepted:
            self._draw_consent_notice(frame)
            return frame
        
        cv2.rectangle(frame, (self.panel_x, 0), (self.frame_w, self.frame_h), Config.PANEL_BG, -1)
        cv2.line(frame, (self.panel_x, 0), (self.panel_x, self.frame_h), (60,60,60), 1)
        y = self._draw_header(frame, fps)
        if calibrating:
            self._draw_calibration(frame, y, cal_progress)
        else:
            y = self._draw_face_status(frame, y, face_count)
            y = self._draw_eye_section(frame, y, eye_dir, eye_h, eye_v)
            y = self._draw_gaze_section(frame, y, gaze_dir, gaze_yaw, gaze_pitch)
            y = self._draw_head_section(frame, y, head_dir, head_pitch, head_yaw, head_roll)
            y = self._draw_score_section(frame, y, score)
            self._draw_alert_bar(frame, current_alert)
        return frame

    def _draw_consent_notice(self, frame):
        overlay = frame.copy()
        cv2.rectangle(overlay, (0, 0), (self.frame_w, self.frame_h), (0, 0, 0), -1)
        cv2.addWeighted(overlay, 0.85, frame, 0.15, 0, frame)
        
        y = 100
        cv2.putText(frame, "CONSENT NOTICE", (self.frame_w//2 - 150, y), 
                    cv2.FONT_HERSHEY_SIMPLEX, 0.9, Config.YELLOW, 2)
        
        y += 50
        lines = [
            "This system monitors behavior for",
            "academic integrity.",
            "",
            "Collected data:",
            "- Facial landmarks & gaze",
            "- Alert events & timestamps",
            "",
            "Privacy:",
            "- No video stored",
            "- Faces blurred",
            "- Local processing only",
            "",
            "",
            "SPACE = Accept & Continue",
            "ESC = Decline & Exit"
        ]
        
        for line in lines:
            cv2.putText(frame, line, (100, y), cv2.FONT_HERSHEY_SIMPLEX, 
                       0.50, Config.WHITE, 1, cv2.LINE_AA)
            y += 22

    def accept_consent(self):
        self._consent_accepted = True

    @property
    def consent_accepted(self):
        return self._consent_accepted

    def _draw_header(self, frame, fps):
        x = self.panel_x + 8
        cv2.putText(frame, "JobLens AI", (x, 22), cv2.FONT_HERSHEY_SIMPLEX, Config.TITLE_FONT, Config.WHITE, Config.TITLE_THICKNESS)
        cv2.putText(frame, "Integrity Monitor", (x, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.35, (140,140,140), 1)
        fps_text = f"FPS {fps:.0f}"
        tw, _ = cv2.getTextSize(fps_text, cv2.FONT_HERSHEY_SIMPLEX, 0.38, 1)
        cv2.putText(frame, fps_text, (self.frame_w - tw[0] - 8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.38, (100,100,100), 1)
        cv2.line(frame, (self.panel_x+6, 52), (self.frame_w-6, 52), (60,60,60), 1)
        return 70

    def _draw_calibration(self, frame, y, progress):
        x = self.panel_x + 10
        cv2.putText(frame, "Calibrating...", (x, y), cv2.FONT_HERSHEY_SIMPLEX, 0.55, Config.YELLOW, 2)
        y += 28
        cv2.putText(frame, "Look straight ahead", (x, y), cv2.FONT_HERSHEY_SIMPLEX, 0.40, (200,200,200), 1)
        y += 22
        cv2.putText(frame, "Keep your head still", (x, y), cv2.FONT_HERSHEY_SIMPLEX, 0.38, (180,180,180), 1)
        y += 30
        bar_w = Config.PANEL_WIDTH - 22
        cv2.rectangle(frame, (x, y), (x+bar_w, y+10), (60,60,60), -1)
        fill = int(bar_w * min(progress, 1.0))
        if fill > 0:
            cv2.rectangle(frame, (x, y), (x+fill, y+10), Config.YELLOW, -1)
        y += 22
        cv2.putText(frame, f"{int(progress*100)}%", (x, y), cv2.FONT_HERSHEY_SIMPLEX, 0.45, Config.YELLOW, 1)

        overlay = frame.copy()
        cv2.rectangle(overlay, (0, self.frame_h-40), (self.panel_x, self.frame_h), (0,140,180), -1)
        cv2.addWeighted(overlay, 0.5, frame, 0.5, 0, frame)
        cv2.putText(frame, "CALIBRATING: Look straight at the camera", (12, self.frame_h-15),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, Config.WHITE, 2, cv2.LINE_AA)

    def _draw_face_status(self, frame, y, face_count):
        x = self.panel_x + 10
        if face_count == 1:   color, status = Config.GREEN, "Detected"
        elif face_count == 0: color, status = Config.RED, "Not Found"
        else:                 color, status = Config.RED, f"Multiple ({face_count})"
        cv2.putText(frame, "Faces", (x, y), cv2.FONT_HERSHEY_SIMPLEX, Config.INFO_FONT, (140,140,140), Config.INFO_THICKNESS)
        cv2.putText(frame, status, (x+70, y), cv2.FONT_HERSHEY_SIMPLEX, Config.INFO_FONT, color, Config.INFO_THICKNESS)
        return y + 28

    def _draw_eye_section(self, frame, y, direction, h_movement, v_movement):
        x = self.panel_x + 10
        cv2.putText(frame, "— Eye Movement", (x, y), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (100,100,100), 1)
        y += 20
        color = Config.RED if direction != "CENTER" else Config.GREEN
        for label, val in [("Direction", direction), ("Horizontal", f"{h_movement:+.2f}"), ("Vertical", f"{v_movement:+.2f}")]:
            cv2.putText(frame, label, (x, y), cv2.FONT_HERSHEY_SIMPLEX, Config.INFO_FONT, (140,140,140), Config.INFO_THICKNESS)
            cv2.putText(frame, str(val), (x+95, y), cv2.FONT_HERSHEY_SIMPLEX, Config.INFO_FONT, color, Config.INFO_THICKNESS)
            y += 22
        return y + 6

    def _draw_gaze_section(self, frame, y, direction, yaw, pitch):
        x = self.panel_x + 10
        cv2.putText(frame, "— Gaze", (x, y), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (100,100,100), 1)
        y += 20
        color = Config.RED if direction != "CENTER" else Config.GREEN
        for label, val in [("Direction", direction), ("Yaw", f"{yaw:+.1f} deg"), ("Pitch", f"{pitch:+.1f} deg")]:
            cv2.putText(frame, label, (x, y), cv2.FONT_HERSHEY_SIMPLEX, Config.INFO_FONT, (140,140,140), Config.INFO_THICKNESS)
            cv2.putText(frame, str(val), (x+95, y), cv2.FONT_HERSHEY_SIMPLEX, Config.INFO_FONT, color, Config.INFO_THICKNESS)
            y += 22
        return y + 6

    def _draw_head_section(self, frame, y, direction, pitch, yaw, roll):
        x = self.panel_x + 10
        cv2.putText(frame, "— Head Pose", (x, y), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (100,100,100), 1)
        y += 20
        color = Config.RED if direction != "FORWARD" else Config.GREEN
        for label, val in [("Direction", direction), ("Pitch", f"{pitch:+.1f}"), ("Yaw", f"{yaw:+.1f}"), ("Roll", f"{roll:+.1f}")]:
            cv2.putText(frame, label, (x, y), cv2.FONT_HERSHEY_SIMPLEX, Config.INFO_FONT, (140,140,140), Config.INFO_THICKNESS)
            cv2.putText(frame, str(val), (x+95, y), cv2.FONT_HERSHEY_SIMPLEX, Config.INFO_FONT, color, Config.INFO_THICKNESS)
            y += 22
        return y + 6

    def _draw_score_section(self, frame, y, score):
        x = self.panel_x + 10
        cv2.putText(frame, "— Suspicion Score", (x, y), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (100,100,100), 1)
        y += 22
        if score < 30:   s_color, label = Config.GREEN,  "Low"
        elif score < 70: s_color, label = Config.YELLOW, "Medium"
        else:            s_color, label = Config.RED,    "High"

        cv2.putText(frame, f"{score:.0f}%", (x, y), cv2.FONT_HERSHEY_SIMPLEX, 0.7, s_color, 2)
        cv2.putText(frame, label, (x+52, y), cv2.FONT_HERSHEY_SIMPLEX, 0.38, s_color, 1)
        y += 10
        bar_w = Config.PANEL_WIDTH - 22
        cv2.rectangle(frame, (x, y), (x+bar_w, y+7), (60,60,60), -1)
        fill = int(bar_w * min(score/100.0, 1.0))
        if fill > 0:
            cv2.rectangle(frame, (x, y), (x+fill, y+7), s_color, -1)
        y += 27
        rec = "Accept" if score < 30 else ("Review needed" if score < 70 else "Flag for review")
        cv2.putText(frame, f"-> {rec}", (x, y), cv2.FONT_HERSHEY_SIMPLEX, 0.36, s_color, 1)
        return y + 24

    def _draw_alert_bar(self, frame, alert_type):
        if alert_type is None:
            return
        overlay = frame.copy()
        cv2.rectangle(overlay, (0, self.frame_h-34), (self.panel_x, self.frame_h), Config.RED, -1)
        cv2.addWeighted(overlay, 0.4, frame, 0.6, 0, frame)
        texts = {
            'NO_FACE':'No face detected!','MULTIPLE_FACES':'Multiple faces detected!',
            'LOOKING_LEFT':'Looking left','LOOKING_RIGHT':'Looking right',
            'LOOKING_UP':'Looking up','LOOKING_DOWN':'Looking down',
            'HEAD_TURNED_LEFT':'Head turned left','HEAD_TURNED_RIGHT':'Head turned right',
            'HEAD_TILTED_UP':'Head tilted up','HEAD_TILTED_DOWN':'Head tilted down',
            'HEAD_TILTED_SIDE':'Head tilted sideways',
            'EYE_LEFT':'Eye movement LEFT','EYE_RIGHT':'Eye movement RIGHT',
            'EYE_UP':'Eye movement UP','EYE_DOWN':'Eye movement DOWN',
        }
        cv2.putText(frame, f"! {texts.get(alert_type, alert_type)}", (12, self.frame_h-13),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.55, Config.WHITE, 2, cv2.LINE_AA)

    @staticmethod
    def draw_hints(frame):
        h, w = frame.shape[:2]
        cv2.putText(frame, "q = quit   r = report   t = timeline   d = debug", (w-280, h-10), 
                    cv2.FONT_HERSHEY_SIMPLEX, 0.3, (90,90,90), 1)


# ══════════════════════════════════════════════════════════════════════════════
#  DATABASE LAYER (added for web app — does not touch original classes)
# ══════════════════════════════════════════════════════════════════════════════

DATABASE_URL = "sqlite:///./joblens.db"
engine = create_engine(DATABASE_URL, connect_args={"check_same_thread": False})
DBSessionLocal = sessionmaker(autocommit=False, autoflush=False, bind=engine)
Base = declarative_base()


class DBSession(Base):
    __tablename__ = "sessions"
    id               = Column(Integer, primary_key=True, index=True)
    started_at       = Column(DateTime, default=datetime.utcnow)
    ended_at         = Column(DateTime, nullable=True)
    final_score      = Column(Float, default=0.0)
    recommendation   = Column(String, default="PENDING")
    duration_seconds = Column(Float, default=0.0)
    alerts           = relationship("DBAlert",        back_populates="session", cascade="all, delete-orphan")
    score_history    = relationship("DBScoreHistory", back_populates="session", cascade="all, delete-orphan")


class DBAlert(Base):
    __tablename__ = "alerts"
    id            = Column(Integer, primary_key=True, index=True)
    session_id    = Column(Integer, ForeignKey("sessions.id"))
    alert_type    = Column(String)
    timestamp     = Column(DateTime, default=datetime.utcnow)
    metadata_json = Column(Text, default="{}")
    session       = relationship("DBSession", back_populates="alerts")


class DBScoreHistory(Base):
    __tablename__ = "score_history"
    id         = Column(Integer, primary_key=True, index=True)
    session_id = Column(Integer, ForeignKey("sessions.id"))
    timestamp  = Column(DateTime, default=datetime.utcnow)
    score      = Column(Float)
    session    = relationship("DBSession", back_populates="score_history")


Base.metadata.create_all(bind=engine)


# ══════════════════════════════════════════════════════════════════════════════
#  WEB SESSION PROCESSOR
#  ─ السيرفر يفتح الكاميرا مباشرة (زي الكود الأصلي بالظبط)
#  ─ بدون أي JPEG compression من المتصفح = نفس دقة الكود الأصلي
#  ─ الـ processed frame بيتبعت للمتصفح بدل ما المتصفح يبعت فريمات
# ══════════════════════════════════════════════════════════════════════════════

import threading
import base64

class WebSessionProcessor:
    """
    السيرفر بيفتح الكاميرا مباشرة ويشغّل نفس الكود الأصلي بالظبط.
    المتصفح بيستقبل النتايج فقط — مفيش latency من upload.
    """
    def __init__(self, session_id: int):
        self.session_id     = session_id
        # original classes — untouched
        self.face_det       = FaceDetector()
        self.gaze_est       = GazeEstimator()
        self.head_est       = HeadPoseEstimator()
        self.eye_tracker    = EyeMovementTracker()
        self.alerts         = AlertManager()
        self._session_start = time.time()
        self._frame_count   = 0
        self.calibrated     = False
        self._running       = False
        self._thread        = None
        self._lock          = threading.Lock()

        # latest state shared between camera thread and WebSocket
        self._latest_state  = {
            "face_count": 0, "calibrated": False, "cal_progress": 0.0,
            "gaze_dir": "—", "gaze_yaw": 0.0, "gaze_pitch": 0.0,
            "head_dir": "—", "head_pitch": 0.0, "head_yaw": 0.0, "head_roll": 0.0,
            "eye_dir": "—", "eye_h": 0.0, "eye_v": 0.0,
            "score": 0.0, "current_alert": None, "alert_breakdown": {},
            "frame_b64": None,
        }

        # open camera exactly like CheatingDetectionSystem.__init__
        self.cap = cv2.VideoCapture(Config.CAMERA_INDEX)
        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH,  Config.FRAME_WIDTH)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, Config.FRAME_HEIGHT)
        self.cap.set(cv2.CAP_PROP_FPS,          Config.FPS)

        if not self.cap.isOpened():
            raise RuntimeError("Cannot open camera")

        # fps tracking
        self._fps         = 0.0
        self._fps_prev    = time.time()
        self._fps_counter = 0

        # ui renderer (used to draw on frames like original)
        self._ui = UIRenderer(Config.FRAME_WIDTH, Config.FRAME_HEIGHT)
        self._ui.accept_consent()  # consent handled in browser

    def _blur_face(self, frame, face_landmarks, img_w, img_h):
        x_min, y_min, x_max, y_max = self.face_det.get_face_bbox(face_landmarks, img_w, img_h)
        face_roi = frame[y_min:y_max, x_min:x_max]
        if face_roi.size > 0:
            blurred = cv2.GaussianBlur(face_roi,
                                       (Config.BLUR_KERNEL_SIZE, Config.BLUR_KERNEL_SIZE), 0)
            frame[y_min:y_max, x_min:x_max] = blurred
        return frame

    def _tick_fps(self):
        self._fps_counter += 1
        if self._fps_counter >= 15:
            now = time.time()
            elapsed = now - self._fps_prev
            if elapsed > 0:
                self._fps = self._fps_counter / elapsed
            self._fps_prev    = now
            self._fps_counter = 0

    def _camera_loop(self, db_factory):
        """
        Runs in background thread.
        Mirrors CheatingDetectionSystem._process() exactly.
        Draws on frame with UIRenderer (same as original).
        Encodes frame to JPEG and stores in _latest_state.
        """
        while self._running:
            ret, frame = self.cap.read()
            if not ret:
                time.sleep(0.01)
                continue

            img_h, img_w = frame.shape[:2]
            results    = self.face_det.detect(frame)
            face_count = self.face_det.face_count(results)

            gaze_dir, gaze_yaw, gaze_pitch = "—", 0.0, 0.0
            head_dir, head_pitch, head_yaw, head_roll = "—", 0.0, 0.0, 0.0
            eye_dir, eye_h, eye_v = "—", 0.0, 0.0
            current_alert = None

            # ── calibration phase (exact copy of original) ────────────────
            if not self.head_est.is_calibrated or not self.eye_tracker.is_calibrated:
                if results.multi_face_landmarks and face_count == 1:
                    lms = results.multi_face_landmarks[0]

                    if not self.head_est.is_calibrated:
                        self.head_est.calibrate(lms, img_w, img_h)

                    if not self.eye_tracker.is_calibrated:
                        eyes = self.face_det.get_eye_data(lms, img_w, img_h)
                        self.eye_tracker.calibrate(
                            eyes['left_eye'],  eyes['left_iris'],
                            eyes['right_eye'], eyes['right_iris']
                        )

                    if self.head_est.is_calibrated and self.eye_tracker.is_calibrated:
                        self.calibrated = True
                        print("=" * 60)
                        print("✓ CALIBRATION COMPLETE - Monitoring started")
                        print("=" * 60)

                progress = max(
                    self.head_est.calibration_progress,
                    self.eye_tracker.calibration_progress
                )

                # draw calibration overlay on frame (exact original)
                frame = self._ui.draw(
                    frame, face_count, "—", 0, 0, "—", 0, 0, 0, "—", 0, 0, 0,
                    None, self._fps, calibrating=True, cal_progress=progress
                )
                UIRenderer.draw_hints(frame)

                _, jpg = cv2.imencode('.jpg', frame, [cv2.IMWRITE_JPEG_QUALITY, 75])
                b64 = base64.b64encode(jpg).decode('utf-8')

                with self._lock:
                    self._latest_state.update({
                        "calibrated"  : False,
                        "cal_progress": progress,
                        "face_count"  : face_count,
                        "frame_b64"   : b64,
                        "fps"         : round(self._fps, 1),
                    })
                self._tick_fps()
                continue

            # ── monitoring phase (exact copy of original _process) ─────────
            if face_count == 0:
                current_alert = "NO_FACE"
                self.alerts.add(current_alert, frame)
            elif face_count > 1:
                current_alert = "MULTIPLE_FACES"
                self.alerts.add(current_alert, frame)

            if results.multi_face_landmarks and face_count >= 1:
                lms  = results.multi_face_landmarks[0]
                eyes = self.face_det.get_eye_data(lms, img_w, img_h)

                eye_h, eye_v, eye_dir, eye_alert = self.eye_tracker.track(
                    eyes['left_eye'],  eyes['left_iris'],
                    eyes['right_eye'], eyes['right_iris']
                )

                gaze_yaw, gaze_pitch = self.gaze_est.combined_gaze(
                    eyes['left_eye'],  eyes['left_iris'],
                    eyes['right_eye'], eyes['right_iris']
                )
                gaze_dir, gaze_alert = self.gaze_est.classify(gaze_yaw, gaze_pitch)

                h_pitch, h_yaw, h_roll, rvec, tvec = self.head_est.estimate(lms, img_w, img_h)
                head_alert = None
                if h_pitch is not None:
                    head_pitch, head_yaw, head_roll = h_pitch, h_yaw, h_roll
                    head_dir, head_alert = self.head_est.classify(h_pitch, h_yaw, h_roll)
                    self.head_est.draw_axes(frame, rvec, tvec, img_w, img_h)

                alert_frame = frame.copy()
                if Config.BLUR_FACE:
                    alert_frame = self._blur_face(alert_frame, lms, img_w, img_h)

                if current_alert is None:
                    if eye_alert:
                        current_alert = eye_alert
                        self.alerts.add(current_alert, alert_frame,
                                        {'h_movement': eye_h, 'v_movement': eye_v})
                    elif gaze_alert:
                        current_alert = gaze_alert
                        self.alerts.add(current_alert, alert_frame,
                                        {'yaw': gaze_yaw, 'pitch': gaze_pitch})
                    elif head_alert:
                        current_alert = head_alert
                        self.alerts.add(current_alert, alert_frame,
                                        {'pitch': head_pitch, 'yaw': head_yaw, 'roll': head_roll})

            score = self.alerts.suspicion_score()
            self.alerts.update_score_history(score)

            # ── draw UI on frame exactly like original ─────────────────────
            frame = self._ui.draw(
                frame, face_count,
                gaze_dir, gaze_yaw, gaze_pitch,
                head_dir, head_pitch, head_yaw, head_roll,
                eye_dir, eye_h, eye_v,
                score, current_alert, self._fps
            )
            UIRenderer.draw_hints(frame)

            # ── save alert to DB ───────────────────────────────────────────
            if current_alert:
                db = db_factory()
                try:
                    existing = (db.query(DBAlert)
                                .filter(DBAlert.session_id == self.session_id,
                                        DBAlert.alert_type == current_alert)
                                .order_by(DBAlert.timestamp.desc()).first())
                    now_dt     = datetime.utcnow()
                    should_save = True
                    if existing:
                        if (now_dt - existing.timestamp).total_seconds() < Config.ALERT_COOLDOWN:
                            should_save = False
                    if should_save:
                        db.add(DBAlert(session_id=self.session_id,
                                       alert_type=current_alert,
                                       metadata_json=json.dumps({})))
                        db.commit()
                finally:
                    db.close()

            # ── save score history every 30 frames ────────────────────────
            self._frame_count += 1
            if self._frame_count % 30 == 0:
                db = db_factory()
                try:
                    db.add(DBScoreHistory(session_id=self.session_id, score=score))
                    db.commit()
                finally:
                    db.close()

            # ── alert breakdown ────────────────────────────────────────────
            now_t  = time.time()
            cutoff = now_t - Config.SCORING_WINDOW_SECONDS
            breakdown = {}
            for a in self.alerts._alerts:
                if a['time'] >= cutoff:
                    breakdown[a['type']] = breakdown.get(a['type'], 0) + 1

            # ── encode frame to JPEG for browser ──────────────────────────
            _, jpg = cv2.imencode('.jpg', frame, [cv2.IMWRITE_JPEG_QUALITY, 75])
            b64    = base64.b64encode(jpg).decode('utf-8')

            with self._lock:
                self._latest_state = {
                    "calibrated"     : True,
                    "cal_progress"   : 1.0,
                    "face_count"     : face_count,
                    "gaze_dir"       : gaze_dir,
                    "gaze_yaw"       : round(float(gaze_yaw),   1),
                    "gaze_pitch"     : round(float(gaze_pitch),  1),
                    "head_dir"       : head_dir,
                    "head_pitch"     : round(float(head_pitch),  1),
                    "head_yaw"       : round(float(head_yaw),    1),
                    "head_roll"      : round(float(head_roll),   1),
                    "eye_dir"        : eye_dir,
                    "eye_h"          : round(float(eye_h), 3),
                    "eye_v"          : round(float(eye_v), 3),
                    "score"          : round(score, 1),
                    "current_alert"  : current_alert,
                    "alert_breakdown": breakdown,
                    "frame_b64"      : b64,
                    "fps"            : round(self._fps, 1),
                }

            self._tick_fps()

    def start(self):
        self._running = True
        self._thread  = threading.Thread(
            target=self._camera_loop,
            args=(DBSessionLocal,),
            daemon=True
        )
        self._thread.start()

    def get_state(self) -> dict:
        with self._lock:
            return dict(self._latest_state)

    def finalize(self, db: DBSessionType):
        self._running = False
        if self._thread:
            self._thread.join(timeout=3)
        self.cap.release()

        score    = self.alerts.suspicion_score()
        rec, _   = self.alerts.recommendation()
        duration = time.time() - self._session_start

        for t, s in self.alerts._score_history:
            db.add(DBScoreHistory(session_id=self.session_id, score=s))

        db.query(DBSession).filter(DBSession.id == self.session_id).update({
            "ended_at"        : datetime.utcnow(),
            "final_score"     : score,
            "recommendation"  : rec,
            "duration_seconds": duration,
        })
        db.commit()

        self.alerts.export_report(f"session_{self.session_id}_report.txt")
        self.face_det.close()


# ══════════════════════════════════════════════════════════════════════════════
#  FASTAPI APP
# ══════════════════════════════════════════════════════════════════════════════

active_processors: dict[int, WebSessionProcessor] = {}

app = FastAPI(title="JobLens AI — Integrity Monitor")
app.add_middleware(CORSMiddleware, allow_origins=["*"],
                   allow_methods=["*"], allow_headers=["*"])
app.mount("/static", StaticFiles(directory="static"), name="static")


@app.get("/", response_class=HTMLResponse)
async def index():
    with open("templates/index.html", encoding="utf-8") as f:
        return HTMLResponse(f.read())


@app.post("/api/sessions/start")
def start_session():
    db = DBSessionLocal()
    try:
        session = DBSession()
        db.add(session)
        db.commit()
        db.refresh(session)
        processor = WebSessionProcessor(session.id)
        processor.start()
        active_processors[session.id] = processor
        return {"session_id": session.id,
                "started_at": session.started_at.isoformat()}
    finally:
        db.close()


@app.post("/api/sessions/{session_id}/end")
def end_session(session_id: int):
    db = DBSessionLocal()
    try:
        if session_id not in active_processors:
            raise HTTPException(status_code=404, detail="Session not found or already ended")
        processor = active_processors.pop(session_id)
        processor.finalize(db)
        session = db.query(DBSession).filter(DBSession.id == session_id).first()
        rec, reason = processor.alerts.recommendation()
        return {
            "session_id"      : session_id,
            "final_score"     : session.final_score,
            "recommendation"  : session.recommendation,
            "reason"          : reason,
            "duration_seconds": session.duration_seconds,
        }
    finally:
        db.close()


@app.get("/api/sessions")
def list_sessions():
    db = DBSessionLocal()
    try:
        sessions = (db.query(DBSession)
                    .order_by(DBSession.started_at.desc())
                    .limit(20).all())
        return [
            {
                "id"              : s.id,
                "started_at"      : s.started_at.isoformat() if s.started_at else None,
                "ended_at"        : s.ended_at.isoformat()   if s.ended_at   else None,
                "final_score"     : s.final_score,
                "recommendation"  : s.recommendation,
                "duration_seconds": s.duration_seconds,
                "alert_count"     : len(s.alerts),
            }
            for s in sessions
        ]
    finally:
        db.close()


@app.get("/api/sessions/{session_id}/report")
def get_report(session_id: int):
    db = DBSessionLocal()
    try:
        session = db.query(DBSession).filter(DBSession.id == session_id).first()
        if not session:
            raise HTTPException(status_code=404, detail="Session not found")
        breakdown = {}
        for a in session.alerts:
            breakdown[a.alert_type] = breakdown.get(a.alert_type, 0) + 1
        score_hist = [
            {"timestamp": sh.timestamp.isoformat(), "score": sh.score}
            for sh in session.score_history
        ]
        return {
            "session_id"      : session_id,
            "started_at"      : session.started_at.isoformat() if session.started_at else None,
            "ended_at"        : session.ended_at.isoformat()   if session.ended_at   else None,
            "final_score"     : session.final_score,
            "recommendation"  : session.recommendation,
            "duration_seconds": session.duration_seconds,
            "alert_breakdown" : breakdown,
            "total_alerts"    : len(session.alerts),
            "score_history"   : score_hist,
        }
    finally:
        db.close()


@app.websocket("/ws/{session_id}")
async def websocket_endpoint(websocket: WebSocket, session_id: int):
    """
    الـ WebSocket دلوقتي بس بيبعت النتايج للمتصفح.
    المعالجة بتتم في الـ camera thread على السيرفر مباشرة.
    """
    await websocket.accept()
    try:
        processor = active_processors.get(session_id)
        if not processor:
            await websocket.send_json({"error": "Session not found"})
            return

        while True:
            try:
                # بعت آخر state كل ~33ms (30fps)
                state = processor.get_state()
                await websocket.send_json(state)
                await asyncio.sleep(0.033)
            except WebSocketDisconnect:
                break
            except Exception:
                break

    except WebSocketDisconnect:
        pass


# ══════════════════════════════════════════════════════════════════════════════
#  STANDALONE DESKTOP MODE (original behaviour — not changed)
# ══════════════════════════════════════════════════════════════════════════════

class CheatingDetectionSystem:
    def __init__(self):
        print("Initializing Enhanced Eye Tracking System...")
        print("=" * 60)
        self.face_det    = FaceDetector()
        self.gaze_est    = GazeEstimator()
        self.head_est    = HeadPoseEstimator()
        self.eye_tracker = EyeMovementTracker()
        self.alerts      = AlertManager()

        self.cap = cv2.VideoCapture(Config.CAMERA_INDEX)
        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH,  Config.FRAME_WIDTH)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, Config.FRAME_HEIGHT)
        self.cap.set(cv2.CAP_PROP_FPS,          Config.FPS)

        self.ui           = UIRenderer(Config.FRAME_WIDTH, Config.FRAME_HEIGHT)
        self._fps         = 0.0
        self._fps_prev    = time.time()
        self._fps_counter = 0

        print("✓ System ready")
        print("=" * 60)
        print("IMPROVED FEATURES:")
        print("  • Eye movement thresholds reduced (0.35 → 0.15)")
        print("  • Gaze thresholds reduced (25° → 15°)")
        print("  • Calibration frames increased (45 → 90)")
        print("  • Alert cooldown reduced (2.0s → 1.5s)")
        print("  • Faster response with less smoothing")
        print("=" * 60)
        print("\nCONTROLS:")
        print("  q = Quit")
        print("  r = Generate report")
        print("  t = Show timeline chart")
        print("  d = Toggle debug mode")
        print("\nLook straight at the camera for calibration...\n")

    def _blur_face(self, frame, face_landmarks, img_w, img_h):
        x_min, y_min, x_max, y_max = self.face_det.get_face_bbox(face_landmarks, img_w, img_h)
        face_roi = frame[y_min:y_max, x_min:x_max]
        if face_roi.size > 0:
            blurred = cv2.GaussianBlur(face_roi, (Config.BLUR_KERNEL_SIZE, Config.BLUR_KERNEL_SIZE), 0)
            frame[y_min:y_max, x_min:x_max] = blurred
        return frame

    def _process(self, frame):
        img_h, img_w = frame.shape[:2]
        results    = self.face_det.detect(frame)
        face_count = self.face_det.face_count(results)

        gaze_dir, gaze_yaw, gaze_pitch = "—", 0.0, 0.0
        head_dir, head_pitch, head_yaw, head_roll = "—", 0.0, 0.0, 0.0
        eye_dir, eye_h, eye_v = "—", 0.0, 0.0
        current_alert = None

        if not self.head_est.is_calibrated or not self.eye_tracker.is_calibrated:
            if results.multi_face_landmarks and face_count == 1:
                lms = results.multi_face_landmarks[0]
                
                head_done = self.head_est.is_calibrated
                if not head_done:
                    head_done = self.head_est.calibrate(lms, img_w, img_h)
                
                eye_done = self.eye_tracker.is_calibrated
                if not eye_done:
                    eyes = self.face_det.get_eye_data(lms, img_w, img_h)
                    eye_done = self.eye_tracker.calibrate(
                        eyes['left_eye'], eyes['left_iris'],
                        eyes['right_eye'], eyes['right_iris']
                    )
                
                if head_done and eye_done:
                    print("=" * 60)
                    print("✓ CALIBRATION COMPLETE - Monitoring started")
                    print("=" * 60)
            
            progress = max(self.head_est.calibration_progress, self.eye_tracker.calibration_progress)
            frame = self.ui.draw(frame, face_count, "—", 0, 0, "—", 0, 0, 0, "—", 0, 0, 0, None, self._fps,
                                 calibrating=True, cal_progress=progress)
            UIRenderer.draw_hints(frame)
            return frame

        if face_count == 0:
            current_alert = "NO_FACE"
            self.alerts.add(current_alert, frame)
        elif face_count > 1:
            current_alert = "MULTIPLE_FACES"
            self.alerts.add(current_alert, frame)

        if results.multi_face_landmarks and face_count >= 1:
            lms = results.multi_face_landmarks[0]
            eyes = self.face_det.get_eye_data(lms, img_w, img_h)

            eye_h, eye_v, eye_dir, eye_alert = self.eye_tracker.track(
                eyes['left_eye'], eyes['left_iris'],
                eyes['right_eye'], eyes['right_iris']
            )

            gaze_yaw, gaze_pitch = self.gaze_est.combined_gaze(
                eyes['left_eye'], eyes['left_iris'], eyes['right_eye'], eyes['right_iris'])
            gaze_dir, gaze_alert = self.gaze_est.classify(gaze_yaw, gaze_pitch)

            h_pitch, h_yaw, h_roll, rvec, tvec = self.head_est.estimate(lms, img_w, img_h)
            head_alert = None
            if h_pitch is not None:
                head_pitch, head_yaw, head_roll = h_pitch, h_yaw, h_roll
                head_dir, head_alert = self.head_est.classify(h_pitch, h_yaw, h_roll)
                self.head_est.draw_axes(frame, rvec, tvec, img_w, img_h)

            alert_frame = frame.copy()
            if Config.BLUR_FACE:
                alert_frame = self._blur_face(alert_frame, lms, img_w, img_h)

            if current_alert is None:
                if eye_alert:
                    current_alert = eye_alert
                    self.alerts.add(current_alert, alert_frame, {'h_movement': eye_h, 'v_movement': eye_v})
                elif gaze_alert:
                    current_alert = gaze_alert
                    self.alerts.add(current_alert, alert_frame, {'yaw': gaze_yaw, 'pitch': gaze_pitch})
                elif head_alert:
                    current_alert = head_alert
                    self.alerts.add(current_alert, alert_frame, {'pitch': head_pitch, 'yaw': head_yaw, 'roll': head_roll})

        score = self.alerts.suspicion_score()
        self.alerts.update_score_history(score)
        
        frame = self.ui.draw(frame, face_count, gaze_dir, gaze_yaw, gaze_pitch,
                             head_dir, head_pitch, head_yaw, head_roll,
                             eye_dir, eye_h, eye_v, score, current_alert, self._fps)
        UIRenderer.draw_hints(frame)
        return frame

    def _tick_fps(self):
        self._fps_counter += 1
        if self._fps_counter >= 15:
            now = time.time()
            elapsed = now - self._fps_prev
            if elapsed > 0:
                self._fps = self._fps_counter / elapsed
            self._fps_prev = now
            self._fps_counter = 0

    def run(self):
        if Config.CONSENT_NOTICE:
            while True:
                ret, frame = self.cap.read()
                if not ret:
                    print("Camera error.")
                    return
                
                frame = self.ui.draw(frame, 0, "—", 0, 0, "—", 0, 0, 0, "—", 0, 0, 0, None, 0,
                                     show_consent=True)
                cv2.imshow("JobLens AI — Integrity Monitor", frame)
                
                key = cv2.waitKey(1) & 0xFF
                if key == 32:
                    self.ui.accept_consent()
                    print("✓ Consent accepted. Starting system...\n")
                    break
                elif key == 27:
                    print("Consent declined. Exiting...\n")
                    self.cap.release()
                    cv2.destroyAllWindows()
                    return

        while True:
            ret, frame = self.cap.read()
            if not ret:
                print("Camera error.")
                break
            frame = self._process(frame)
            self._tick_fps()
            cv2.imshow("JobLens AI — Integrity Monitor", frame)
            key = cv2.waitKey(1) & 0xFF
            if key == ord('q'):
                break
            if key == ord('r'):
                path = self.alerts.export_report()
                print(f"\n✓ Report saved → {path}")
                summ = self.alerts.summary()
                rec, reason = self.alerts.recommendation()
                print(f"  Score: {summ['score']:.0f}%  |  {rec}: {reason}\n")
            if key == ord('t'):
                timeline_img = self.alerts.generate_timeline_chart()
                if timeline_img is not None:
                    cv2.imshow("Timeline & Score Chart", timeline_img)
                    print("✓ Timeline chart displayed\n")
                else:
                    print("No data available for timeline chart.\n")
            if key == ord('d'):
                Config.DEBUG_MODE = not Config.DEBUG_MODE
                status = "ON" if Config.DEBUG_MODE else "OFF"
                print(f"\n✓ Debug mode: {status}\n")
        self._shutdown()

    def _shutdown(self):
        path = self.alerts.export_report("final_report.txt")
        print("\n" + "=" * 60)
        print("FINAL REPORT")
        print("=" * 60)
        print(f"Report saved: {path}")
        summ = self.alerts.summary()
        rec, reason = self.alerts.recommendation()
        print(f"Score: {summ['score']:.0f}% | {rec}: {reason}")
        print(f"Session duration: {summ['session_duration']:.1f}s")
        print("=" * 60)
        self.cap.release()
        cv2.destroyAllWindows()
        self.face_det.close()
        print("✓ System closed.\n")


if __name__ == "__main__":
    # run as standalone desktop app (original behaviour)
    desktop = CheatingDetectionSystem()
    desktop.run()
