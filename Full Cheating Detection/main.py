# ══════════════════════════════════════════════════════════════════════════════
#  JobLens AI — Web App  (ENHANCED VERSION)
#  New Features:
#  1. Session Timeline Replay — blurred keyframe snapshots at alert events
#  2. Candidate Profile — name/ID stored per session
#  3. Exponential Decay Scoring — recent alerts weighted more heavily
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

import json
import asyncio
from fastapi import FastAPI, WebSocket, WebSocketDisconnect, HTTPException
from fastapi.staticfiles import StaticFiles
from fastapi.responses import HTMLResponse
from fastapi.middleware.cors import CORSMiddleware
from sqlalchemy import create_engine, Column, Integer, Float, String, DateTime, ForeignKey, Text, LargeBinary
from sqlalchemy.ext.declarative import declarative_base
from sqlalchemy.orm import sessionmaker, relationship, Session as DBSessionType
from pydantic import BaseModel
from typing import Optional

# ══════════════════════════════════════════════════════════════════════════════
#  CONFIG
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

    ALERT_COOLDOWN = 5   # اليرت مش بيتطلق أكتر من مرة كل 5 ثواني لنفس النوع

    # ══ Intelligent Scoring System ══════════════════════════════════════════
    #  1. SLIDING WINDOW  - اخر 180 ثانية
    #  2. SLOW DECAY      - half-life 200 ثانية (السكور بياخد وقت عشان ينزل)
    #  3. SCORE FLOOR     - behavior memory
    #  4. COOLDOWN TIMER  - بعد اخر اليرت, السكور يفضل ثابت 30 ثانية
    # =====================================================================
    SCORING_WINDOW_SECONDS = 180
    DECAY_HALF_LIFE        = 200.0
    SCORE_COOLDOWN_SECS    = 30          # 30 ثانية freeze بعد آخر اليرت
    # لو وصل peak >= key, السكور مش ينزل تحت value ابدا في الجلسة
    SCORE_FLOORS = {
        80: 78,
        60: 58,
        40: 38,
    }

    ALERT_WEIGHTS = {
        # ── حركات العين والرأس (خفيفة) ──────────────────────────────
        'NO_FACE':           6,
        'MULTIPLE_FACES':    9,
        'LOOKING_LEFT':      2,
        'LOOKING_RIGHT':     2,
        'LOOKING_UP':        3,
        'LOOKING_DOWN':      2,
        'HEAD_TURNED_LEFT':  2,
        'HEAD_TURNED_RIGHT': 2,
        'HEAD_TILTED_UP':    3,
        'HEAD_TILTED_DOWN':  2,
        'HEAD_TILTED_SIDE':  1,
        'EYE_LEFT':          3,
        'EYE_RIGHT':         3,
        'EYE_UP':            3,
        'EYE_DOWN':          2,
        # ── YOLO (ثقيلة - غش حقيقي) ─────────────────────────────────
        'MULTIPLE_PEOPLE'      : 20,
        'CHEATING_ITEM_MOBILE' : 25,
    }

    PHONE_CONFIDENCE    = 0.80
    PERSON_CONFIDENCE   = 0.65
    MAX_RAW_SCORE_FOR_NORMALIZATION = 100  # معايرة عشان السكور يكون منطقي
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

    # ── Feature 1: Keyframe settings ──────────────────────────────────────
    KEYFRAME_JPEG_QUALITY = 40       # Lower quality = smaller DB footprint
    KEYFRAME_MAX_WIDTH    = 320      # Thumbnail width
    KEYFRAME_BLUR_KERNEL  = 35       # Blur kernel for privacy


# ══════════════════════════════════════════════════════════════════════════════
#  FACE DETECTOR
# ══════════════════════════════════════════════════════════════════════════════

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


# ══════════════════════════════════════════════════════════════════════════════
#  EYE MOVEMENT TRACKER
# ══════════════════════════════════════════════════════════════════════════════

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
            self._baseline_left = (float(np.median(left_h)), float(np.median(left_v)))
            self._baseline_right = (float(np.median(right_h)), float(np.median(right_v)))
            self._calibrated = True
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


# ══════════════════════════════════════════════════════════════════════════════
#  GAZE ESTIMATOR
# ══════════════════════════════════════════════════════════════════════════════

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


# ══════════════════════════════════════════════════════════════════════════════
#  HEAD POSE ESTIMATOR
# ══════════════════════════════════════════════════════════════════════════════

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
            self._baseline = (
                float(np.median([s[0] for s in self._cal_samples])),
                float(np.median([s[1] for s in self._cal_samples])),
                float(np.median([s[2] for s in self._cal_samples])),
            )
            self._calibrated = True
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


# ══════════════════════════════════════════════════════════════════════════════
#  ALERT MANAGER  (Feature 3: Exponential Decay Scoring)
# ══════════════════════════════════════════════════════════════════════════════

class AlertManager:
    def __init__(self):
        self._alerts = []
        self._last_time = {}
        self._session_start = time.time()
        self._score_history = []
        # Feature 1: keyframe storage {alert_index: jpeg_bytes}
        self._keyframes = {}  # type: dict
        if Config.SAVE_ALERTS:
            os.makedirs(Config.ALERT_FRAMES_DIR, exist_ok=True)

    def add(self, alert_type, frame=None, metadata=None):
        """Add an alert. Optionally captures a blurred keyframe (Feature 1)."""
        now = time.time()
        if alert_type in self._last_time:
            if (now - self._last_time[alert_type]) < Config.ALERT_COOLDOWN:
                return False
        self._last_time[alert_type] = now

        alert_idx = len(self._alerts)
        record = {
            'type': alert_type,
            'time': now,
            'elapsed': now - self._session_start,
            'metadata': metadata or {},
            'keyframe_idx': alert_idx,
        }

        # Feature 1: Capture blurred keyframe thumbnail
        if frame is not None:
            try:
                keyframe_bytes = self._capture_keyframe(frame)
                if keyframe_bytes:
                    self._keyframes[alert_idx] = keyframe_bytes
            except Exception as e:
                print(f"Keyframe capture error: {e}")

        if Config.SAVE_ALERTS and frame is not None:
            fname = f"{alert_type}_{int(now*1000)}.jpg"
            path = os.path.join(Config.ALERT_FRAMES_DIR, fname)
            cv2.imwrite(path, frame)
            record['frame_path'] = path

        self._alerts.append(record)
        return True

    def _capture_keyframe(self, frame):
        """Resize + blur frame and encode as JPEG bytes for storage."""
        try:
            h, w = frame.shape[:2]
            # Resize to thumbnail
            scale = Config.KEYFRAME_MAX_WIDTH / w
            new_w = Config.KEYFRAME_MAX_WIDTH
            new_h = int(h * scale)
            small = cv2.resize(frame, (new_w, new_h))
            # Apply strong blur for privacy
            k = Config.KEYFRAME_BLUR_KERNEL
            blurred = cv2.GaussianBlur(small, (k, k), 0)
            # Encode to JPEG
            _, buf = cv2.imencode('.jpg', blurred,
                                  [cv2.IMWRITE_JPEG_QUALITY, Config.KEYFRAME_JPEG_QUALITY])
            return buf.tobytes()
        except Exception:
            return None

    def get_keyframe_b64(self, alert_idx: int):
        """Return base64-encoded keyframe for given alert index."""
        import base64
        data = self._keyframes.get(alert_idx)
        if data:
            return base64.b64encode(data).decode('utf-8')
        return None

    def update_score_history(self, score):
        now = time.time()
        self._score_history.append((now, score))
        cutoff = now - Config.SCORING_WINDOW_SECONDS
        self._score_history = [(t, s) for t, s in self._score_history if t >= cutoff]

    # ══ Intelligent Scoring System ══════════════════════════════════════════
    def suspicion_score(self):
        """
        نظام السكور المتكامل - 3 مكونات:

        1. SLIDING WINDOW + SLOW DECAY
           - بنحسب الاليرتات في اخر SCORING_WINDOW_SECONDS بس
           - كل اليرت بياخد weight * exp(-lambda * age)
           - لكن الـ lambda صغير (half-life = 90s) عشان الهبوط بطيء

        2. COOLDOWN FREEZE
           - بعد اخر اليرت، السكور مش بينزل لمدة SCORE_COOLDOWN_SECS
           - يعني لو بطلت تغش 5 ثواني، السكور بيفضل ثابت مش بينزل

        3. BEHAVIOR MEMORY (Score Floor)
           - لو السكور وصل 60+ في اي وقت في الجلسة،
             مش ممكن ينزل تحت 35 حتى لو بطلت تغش خالص
           - بيعكس ان الشخص ده غش فعلاً حتى لو وقف دلوقتي
        """
        now = time.time()
        cutoff = now - Config.SCORING_WINDOW_SECONDS
        lam = math.log(2) / Config.DECAY_HALF_LIFE

        # ── 1. احسب الـ raw decay score ─────────────────────────────────
        raw = 0.0
        last_alert_time = 0.0
        for a in self._alerts:
            if a['time'] < cutoff:
                continue
            age = now - a['time']
            decay_factor = math.exp(-lam * age)
            base_weight = Config.ALERT_WEIGHTS.get(a['type'], 3)
            raw += base_weight * decay_factor
            if a['time'] > last_alert_time:
                last_alert_time = a['time']

        # ── 2. COOLDOWN FREEZE ───────────────────────────────────────────
        # لو في اخر SCORE_COOLDOWN_SECS ثانية فيه اليرت، السكور ثابت تماماً
        # وبعد الـ cooldown ينزل ببطء شديد
        time_since_last = now - last_alert_time if last_alert_time > 0 else 9999
        if time_since_last < Config.SCORE_COOLDOWN_SECS:
            # في فترة الـ freeze: السكور ثابت بالكامل - مش بيتحرك خالص
            freeze = 1.0 - (time_since_last / Config.SCORE_COOLDOWN_SECS)
            # نعوّض الـ decay بالكامل خلال فترة الـ freeze
            raw = raw * (1.0 + freeze * 0.4)

        current_score = min((raw / Config.MAX_RAW_SCORE_FOR_NORMALIZATION) * 100.0, 100.0)

        # ── 3. BEHAVIOR MEMORY FLOOR ─────────────────────────────────────
        # تتبع الـ peak score طول الجلسة
        if not hasattr(self, '_peak_score'):
            self._peak_score = 0.0
        if current_score > self._peak_score:
            self._peak_score = current_score

        # طبّق الـ floor بناءا على الـ peak
        floor = 0.0
        for threshold in sorted(Config.SCORE_FLOORS.keys(), reverse=True):
            if self._peak_score >= threshold:
                floor = Config.SCORE_FLOORS[threshold]
                break

        final_score = max(current_score, floor)
        return min(final_score, 100.0)

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
        """Return alert timeline with keyframe availability flag."""
        return [
            {
                'elapsed': a['elapsed'],
                'type': a['type'],
                'time': a['time'],
                'keyframe_idx': a['keyframe_idx'],
                'has_keyframe': a['keyframe_idx'] in self._keyframes,
            }
            for a in self._alerts
        ]

    def generate_timeline_chart(self):
        if len(self._alerts) == 0:
            return None
        fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(12, 8))
        fig.patch.set_facecolor('#2a2d32')
        timeline_data = self.get_timeline_data()
        times = [d['elapsed'] for d in timeline_data]
        alert_types = [d['type'] for d in timeline_data]
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
            ax2.set_title('Suspicion Score Over Time (Exp. Decay)', color='white', fontsize=13, fontweight='bold')
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
            f"  Score (Exp. Decay) : {summ['score']:.1f} / 100",
            f"  Recommendation     : {rec}",
            f"  Reason             : {reason}",
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


# ══════════════════════════════════════════════════════════════════════════════
#  YOLO DETECTOR
# ══════════════════════════════════════════════════════════════════════════════

try:
    from ultralytics import YOLO as _YOLO
    _YOLO_AVAILABLE = True
except ImportError:
    _YOLO_AVAILABLE = False
    print("⚠️  ultralytics not installed — YOLO detection disabled")


class YOLODetector:
    """
    Accurate YOLO-based detector.

    Improvements over the original:
    ─────────────────────────────────────────────────────────────────────────
    1. HIGHER CONFIDENCE THRESHOLDS
       Person 0.60 → 0.70  |  Phone 0.50 → 0.75
       Fewer false positives from low-confidence detections.

    2. IoU DEDUPLICATION  (new)
       The same person/phone can produce 2–3 overlapping YOLO boxes.
       We keep only the highest-confidence box among overlapping ones,
       so person_count is never inflated by duplicate detections.

    3. MINIMUM AREA FILTER for phones  (improved)
       Old code only checked min_side of the box; this can still pass
       rectangular blobs that are tiny in area.
       New code checks box_area >= 0.3% of frame area — much stricter.

    4. SLIDING-WINDOW VOTE  (replaces consecutive count)
       Old system: needed N consecutive frames → a single "clean" frame
       resets the counter to 0, causing jitter.
       New system: ring buffer of the last WINDOW frames. Alert confirmed
       only when ≥ THRESHOLD frames inside the window contain that raw
       detection. Immune to single-frame noise without adding lag.

    5. DETECTION PRIORITY  (clarified)
       MULTIPLE_PEOPLE > CHEATING_ITEM_MOBILE
    ─────────────────────────────────────────────────────────────────────────
    """

    COOLDOWN_SECONDS = 2

    # ── confidence / geometry thresholds ────────────────────────────────
    PERSON_CONF      = 0.70
    PHONE_CONF       = 0.75
    PHONE_MIN_AREA   = 0.003   # phone box must cover ≥ 0.3% of frame area
    PERSON_IOU_MERGE = 0.45    # boxes with IoU ≥ this are the same person

    # ── sliding-window vote parameters ──────────────────────────────────
    # alert confirmed only when ≥ THRESHOLD of the last WINDOW frames contain it
    VOTE_WINDOW = {
        'MULTIPLE_PEOPLE'     : 5,
        'CHEATING_ITEM_MOBILE': 6,
    }
    VOTE_THRESHOLD = {
        'MULTIPLE_PEOPLE'     : 3,
        'CHEATING_ITEM_MOBILE': 4,
    }

    def __init__(self, session_id: str = None):
        import collections as _col
        self._available    = _YOLO_AVAILABLE
        self._model_coco   = None
        self._model_custom = None

        if self._available:
            try:
                self._model_coco   = _YOLO('yolov8n.pt')
                self._model_custom = _YOLO('phones.pt')
                print("✓ YOLO models loaded (yolov8n.pt + phones.pt)")
            except Exception as e:
                print(f"⚠️  YOLO model load error: {e} — YOLO detection disabled")
                self._available = False

        self.session_id   = session_id or datetime.now().strftime("%Y%m%d_%H%M%S")
        self.session_data = {
            "session_id"      : self.session_id,
            "start_time"      : datetime.now().isoformat(),
            "end_time"        : None,
            "total_alerts"    : 0,
            "events"          : [],
            "frame_statistics": {},
        }

        self.frame_counters = {
            "total_frames"               : 0,
            "secure_frames"              : 0,
            "cheating_frames"            : 0,
            "MULTIPLE_PEOPLE_frames"     : 0,
            "CHEATING_ITEM_MOBILE_frames": 0,
        }

        self._last_alert_type        = None
        self._last_alert_logged_time = 0.0
        self._start_time             = time.time()

        # ring buffers — one per alert type
        self._vote_buffers = {
            k: _col.deque(maxlen=self.VOTE_WINDOW[k])
            for k in self.VOTE_WINDOW
        }

    @property
    def available(self) -> bool:
        return self._available

    # ── helpers ──────────────────────────────────────────────────────────
    @staticmethod
    def _iou(a, b) -> float:
        ax1,ay1,ax2,ay2 = a
        bx1,by1,bx2,by2 = b
        ix1,iy1 = max(ax1,bx1), max(ay1,by1)
        ix2,iy2 = min(ax2,bx2), min(ay2,by2)
        inter   = max(0,ix2-ix1) * max(0,iy2-iy1)
        union   = (ax2-ax1)*(ay2-ay1) + (bx2-bx1)*(by2-by1) - inter
        return inter/union if union > 0 else 0.0

    @classmethod
    def _deduplicate(cls, boxes_confs: list, iou_thresh: float) -> list:
        """Keep highest-confidence box; discard overlapping duplicates."""
        sorted_bc = sorted(boxes_confs, key=lambda x: x[4], reverse=True)
        kept = []
        for cand in sorted_bc:
            if not any(cls._iou(cand[:4], k[:4]) >= iou_thresh for k in kept):
                kept.append(cand)
        return kept

    # ── logging ──────────────────────────────────────────────────────────
    def log_event(self, alert_type: str, details: dict) -> dict:
        event = {
            "event_id"       : len(self.session_data["events"]) + 1,
            "timestamp"      : datetime.now().isoformat(),
            "elapsed_seconds": round(time.time() - self._start_time, 2),
            "alert_type"     : alert_type,
            "details"        : details,
        }
        self.session_data["events"].append(event)
        self.session_data["total_alerts"] = len(self.session_data["events"])
        print(f"[YOLO LOG] {event['timestamp']} | {alert_type} | {details}")
        # ── broadcast to live log buffer (picked up by /ws/logs) ─────────
        _push_log(f"[YOLO] {alert_type}", details, self.session_id)
        return event

    # ── main detection ────────────────────────────────────────────────────
    def detect(self, frame):
        if not self._available:
            return None, "Status: Secure", frame

        img_h, img_w = frame.shape[:2]
        frame_area   = img_h * img_w
        person_boxes = []
        phone_boxes  = []
        detected_objects = []

        # 1. Person detection ─────────────────────────────────────────────
        results_coco = self._model_coco.predict(
            frame, conf=self.PERSON_CONF, iou=0.45, verbose=False
        )
        for result in results_coco:
            for box in result.boxes:
                if int(box.cls[0]) != 0:
                    continue
                x1,y1,x2,y2 = map(int, box.xyxy[0])
                conf = round(float(box.conf[0]), 2)
                person_boxes.append((x1,y1,x2,y2,conf))

        # Remove duplicate person boxes (same person detected by 2 anchors)
        person_boxes = self._deduplicate(person_boxes, self.PERSON_IOU_MERGE)
        person_count = len(person_boxes)

        for (x1,y1,x2,y2,conf) in person_boxes:
            detected_objects.append({"type": "Person", "confidence": conf})
            cv2.rectangle(frame, (x1,y1), (x2,y2), (0,220,0), 2)
            cv2.putText(frame, f"Person {conf}", (x1, y1-10),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0,220,0), 2)

        # 2. Phone detection ──────────────────────────────────────────────
        results_custom = self._model_custom.predict(
            frame, conf=self.PHONE_CONF, iou=0.40, verbose=False
        )
        for result in results_custom:
            for box in result.boxes:
                x1,y1,x2,y2 = map(int, box.xyxy[0])
                conf     = round(float(box.conf[0]), 2)
                box_area = (x2-x1) * (y2-y1)
                # Reject tiny blobs — strict area-based filter
                if box_area < frame_area * self.PHONE_MIN_AREA:
                    continue
                phone_boxes.append((x1,y1,x2,y2,conf))

        phone_boxes    = self._deduplicate(phone_boxes, 0.40)
        phone_detected = len(phone_boxes) > 0

        for (x1,y1,x2,y2,conf) in phone_boxes:
            detected_objects.append({"type": "Mobile", "confidence": conf})
            cv2.rectangle(frame, (x1,y1), (x2,y2), (0,0,220), 2)
            cv2.putText(frame, f"Mobile {conf}", (x1, y1-10),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0,0,220), 2)

        # 3. Raw alert for this frame ──────────────────────────────────────
        # Priority: MULTIPLE_PEOPLE > CHEATING_ITEM_MOBILE
        if person_count > 1:
            raw_alert   = "MULTIPLE_PEOPLE"
            raw_details = {"person_count": person_count, "detected": detected_objects}
        elif phone_detected:
            raw_alert   = "CHEATING_ITEM_MOBILE"
            raw_details = {"items_detected": ["Mobile"]*len(phone_boxes),
                           "person_count": person_count,
                           "detected_objects": detected_objects}
        else:
            raw_alert   = None
            raw_details = {}

        # 4. Sliding-window vote ──────────────────────────────────────────
        for atype, buf in self._vote_buffers.items():
            buf.append(raw_alert == atype)

        confirmed_alert_type = None
        confirmed_details    = {}
        for atype in ("MULTIPLE_PEOPLE", "CHEATING_ITEM_MOBILE"):
            if sum(self._vote_buffers[atype]) >= self.VOTE_THRESHOLD[atype]:
                confirmed_alert_type = atype
                confirmed_details    = raw_details if raw_alert == atype else {}
                break

        # 5. Build display message ────────────────────────────────────────
        if confirmed_alert_type == "MULTIPLE_PEOPLE":
            alert_msg = "ALERT: MULTIPLE PEOPLE DETECTED!"
            msg_color = (0, 0, 220)
        elif confirmed_alert_type == "CHEATING_ITEM_MOBILE":
            alert_msg = "ALERT: MOBILE PHONE DETECTED!"
            msg_color = (0, 0, 220)
        else:
            alert_msg = "Status: Secure"
            msg_color = (0, 220, 0)

        # 6. Frame counters ───────────────────────────────────────────────
        self.frame_counters["total_frames"] += 1
        if confirmed_alert_type is None:
            self.frame_counters["secure_frames"] += 1
        else:
            self.frame_counters["cheating_frames"] += 1
            key = f"{confirmed_alert_type}_frames"
            if key in self.frame_counters:
                self.frame_counters[key] += 1

        # 7. Cooldown-gated event logging ─────────────────────────────────
        current_time = time.time()
        if confirmed_alert_type is not None:
            time_since_last = current_time - self._last_alert_logged_time
            is_new_type     = confirmed_alert_type != self._last_alert_type
            if is_new_type or time_since_last >= self.COOLDOWN_SECONDS:
                self.log_event(confirmed_alert_type, confirmed_details)
                self._last_alert_type        = confirmed_alert_type
                self._last_alert_logged_time = current_time
        else:
            self._last_alert_type = None

        # 8. On-frame overlay ─────────────────────────────────────────────
        cv2.putText(frame, alert_msg, (20, 90),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.7, msg_color, 2)

        elapsed   = int(time.time() - self._start_time)
        total_f   = self.frame_counters["total_frames"]
        cheat_pct = round(
            self.frame_counters["cheating_frames"] / total_f * 100, 1
        ) if total_f > 0 else 0.0
        cv2.putText(frame,
                    f"YOLO | Time: {elapsed}s | Cheat: {cheat_pct}%",
                    (10, frame.shape[0] - 30),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.38, (200, 200, 200), 1)

        return confirmed_alert_type, alert_msg, frame

    def save_json(self, output_path: str = None) -> str:
        self.session_data["end_time"] = datetime.now().isoformat()
        total = self.frame_counters["total_frames"]

        def pct(n):
            return round((n / total * 100), 2) if total > 0 else 0.0

        self.session_data["frame_statistics"] = {
            "total_frames"       : total,
            "secure_frames"      : self.frame_counters["secure_frames"],
            "cheating_frames"    : self.frame_counters["cheating_frames"],
            "cheating_percentage": pct(self.frame_counters["cheating_frames"]),
            "secure_percentage"  : pct(self.frame_counters["secure_frames"]),
            "breakdown": {
                "MULTIPLE_PEOPLE": {
                    "frames"    : self.frame_counters["MULTIPLE_PEOPLE_frames"],
                    "percentage": pct(self.frame_counters["MULTIPLE_PEOPLE_frames"]),
                },
                "CHEATING_ITEM_MOBILE": {
                    "frames"    : self.frame_counters["CHEATING_ITEM_MOBILE_frames"],
                    "percentage": pct(self.frame_counters["CHEATING_ITEM_MOBILE_frames"]),
                },
            },
        }

        if output_path is None:
            os.makedirs("outputs", exist_ok=True)
            output_path = os.path.join("outputs", f"yolo_log_{self.session_id}.json")

        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(self.session_data, f, ensure_ascii=False, indent=2)

        return output_path


# ══════════════════════════════════════════════════════════════════════════════
#  UI RENDERER
# ══════════════════════════════════════════════════════════════════════════════

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
#  DATABASE LAYER  (Feature 2: Candidate Profile added)
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
    recommendation   = Column(String(50), default="PENDING")
    duration_seconds = Column(Float, default=0.0)
    # Feature 2: Candidate profile fields
    candidate_name   = Column(String(200), nullable=True)
    candidate_id     = Column(String(100), nullable=True)
    alerts           = relationship("DBAlert",        back_populates="session", cascade="all, delete-orphan")
    score_history    = relationship("DBScoreHistory", back_populates="session", cascade="all, delete-orphan")
    yolo_alerts      = relationship("DBYoloAlert",    back_populates="session", cascade="all, delete-orphan")
    # Feature 1: Keyframes
    keyframes        = relationship("DBKeyframe",     back_populates="session", cascade="all, delete-orphan")


class DBAlert(Base):
    __tablename__ = "alerts"
    id            = Column(Integer, primary_key=True, index=True)
    session_id    = Column(Integer, ForeignKey("sessions.id"))
    alert_type    = Column(String(100))
    timestamp     = Column(DateTime, default=datetime.utcnow)
    elapsed_secs  = Column(Float, default=0.0)
    metadata_json = Column(Text)
    session       = relationship("DBSession", back_populates="alerts")


class DBYoloAlert(Base):
    __tablename__ = "yolo_alerts"
    id            = Column(Integer, primary_key=True, index=True)
    session_id    = Column(Integer, ForeignKey("sessions.id"))
    alert_type    = Column(String(100))
    timestamp     = Column(DateTime, default=datetime.utcnow)
    elapsed_secs  = Column(Float, default=0.0)
    details_json  = Column(Text)
    session       = relationship("DBSession", back_populates="yolo_alerts")


class DBScoreHistory(Base):
    __tablename__ = "score_history"
    id         = Column(Integer, primary_key=True, index=True)
    session_id = Column(Integer, ForeignKey("sessions.id"))
    timestamp  = Column(DateTime, default=datetime.utcnow)
    score      = Column(Float)
    session    = relationship("DBSession", back_populates="score_history")


# Feature 1: Keyframe table — stores blurred JPEG thumbnail per alert event
class DBKeyframe(Base):
    __tablename__ = "keyframes"
    id            = Column(Integer, primary_key=True, index=True)
    session_id    = Column(Integer, ForeignKey("sessions.id"))
    alert_type    = Column(String(100))
    timestamp     = Column(DateTime, default=datetime.utcnow)
    elapsed_secs  = Column(Float, default=0.0)
    image_data    = Column(LargeBinary, nullable=True)  # JPEG bytes (blurred)
    session       = relationship("DBSession", back_populates="keyframes")


Base.metadata.create_all(bind=engine)


# ══════════════════════════════════════════════════════════════════════════════
#  AUTO MIGRATION
# ══════════════════════════════════════════════════════════════════════════════

def _run_migrations():
    """
    يفحص الاعمدة الموجودة في الـ DB ويضيف اللي ناقص تلقائيا.
    بيحل مشكلة الـ DB القديمة اللي مش فيها الاعمدة الجديدة.
    """
    import sqlite3
    db_path = "./joblens.db"

    migrations = [
        ("sessions",   "candidate_name",  "VARCHAR(200)"),
        ("sessions",   "candidate_id",    "VARCHAR(100)"),
        ("alerts",     "elapsed_secs",    "FLOAT DEFAULT 0.0"),
        ("yolo_alerts","elapsed_secs",    "FLOAT DEFAULT 0.0"),
    ]

    try:
        conn = sqlite3.connect(db_path)
        cursor = conn.cursor()

        for table, column, definition in migrations:
            cursor.execute("PRAGMA table_info({})".format(table))
            existing_cols = [row[1] for row in cursor.fetchall()]
            if column not in existing_cols:
                cursor.execute("ALTER TABLE {} ADD COLUMN {} {}".format(table, column, definition))
                print("Migration: added '{}' to '{}'".format(column, table))

        # جدول keyframes جديد كليا - اتأكد انه موجود
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS keyframes (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id   INTEGER REFERENCES sessions(id),
                alert_type   VARCHAR(100),
                timestamp    DATETIME DEFAULT CURRENT_TIMESTAMP,
                elapsed_secs FLOAT DEFAULT 0.0,
                image_data   BLOB
            )
        """)

        conn.commit()
        conn.close()
        print("Database migration complete")
    except Exception as e:
        print("Migration warning: {}".format(e))


_run_migrations()


# ══════════════════════════════════════════════════════════════════════════════
#  WEB SESSION PROCESSOR
# ══════════════════════════════════════════════════════════════════════════════

import threading
import base64


class WebSessionProcessor:
    def __init__(self, session_id: int):
        self.session_id     = session_id
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

        self._latest_state  = {
            "face_count": 0, "calibrated": False, "cal_progress": 0.0,
            "gaze_dir": "—", "gaze_yaw": 0.0, "gaze_pitch": 0.0,
            "head_dir": "—", "head_pitch": 0.0, "head_yaw": 0.0, "head_roll": 0.0,
            "eye_dir": "—", "eye_h": 0.0, "eye_v": 0.0,
            "score": 0.0, "current_alert": None, "alert_breakdown": {},
            "frame_b64": None,
            "yolo_alert": None,
            "fps": 0.0,
        }

        # Try to open camera — on Windows use CAP_DSHOW to avoid long init delays
        self.cap = None
        indices_to_try = [Config.CAMERA_INDEX] + [i for i in range(4) if i != Config.CAMERA_INDEX]
        for idx in indices_to_try:
            # Try DirectShow first (Windows), then fallback
            for backend in [cv2.CAP_DSHOW, cv2.CAP_ANY]:
                cap_attempt = cv2.VideoCapture(idx + backend)
                if cap_attempt.isOpened():
                    ret, test_frame = cap_attempt.read()
                    if ret and test_frame is not None:
                        self.cap = cap_attempt
                        print(f"✓ Camera opened: index={idx}, backend={'DSHOW' if backend == cv2.CAP_DSHOW else 'ANY'}")
                        break
                    cap_attempt.release()
                else:
                    cap_attempt.release()
            if self.cap is not None:
                break

        if self.cap is None or not self.cap.isOpened():
            raise RuntimeError(
                "Cannot open camera. Make sure: "
                "1) Camera is connected and not used by another app (Zoom, Teams, etc), "
                "2) Camera permissions are granted to Python, "
                "3) Try changing Config.CAMERA_INDEX"
            )

        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH,  Config.FRAME_WIDTH)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, Config.FRAME_HEIGHT)
        self.cap.set(cv2.CAP_PROP_FPS,          Config.FPS)

        self._yolo            = YOLODetector(session_id=str(session_id))
        self._yolo_latest_frame = None
        self._yolo_frame_lock  = threading.Lock()
        self._yolo_alert_type  = None
        self._yolo_thread      = None

        self._fps         = 0.0
        self._fps_prev    = time.time()
        self._fps_counter = 0

        self._ui = UIRenderer(Config.FRAME_WIDTH, Config.FRAME_HEIGHT)
        self._ui.accept_consent()

        # Feature 1: pending keyframes to persist to DB
        self._pending_keyframes = []
        self._keyframe_lock = threading.Lock()

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

    def _yolo_loop(self, db_factory):
        # بنرن YOLO كل 4 فريمات بدل 15 → استجابة أسرع للموبايل
        YOLO_EVERY_N = 4
        frame_counter = 0

        while self._running:
            with self._yolo_frame_lock:
                frame_copy = self._yolo_latest_frame.copy() \
                    if self._yolo_latest_frame is not None else None

            if frame_copy is None:
                time.sleep(0.05)
                continue

            frame_counter += 1
            if frame_counter % YOLO_EVERY_N != 0:
                time.sleep(0.005)
                continue

            yolo_alert_type, _msg, _annotated = self._yolo.detect(frame_copy)
            self._yolo_alert_type = yolo_alert_type

            if yolo_alert_type:
                elapsed = time.time() - self._session_start
                _push_log(f"[YOLO] {yolo_alert_type}", {"elapsed": round(elapsed,1)}, self.session_id)
                # Add to alert manager with keyframe capture
                self.alerts.add(yolo_alert_type, frame_copy)

                db = db_factory()
                try:
                    existing_yolo = (db.query(DBYoloAlert)
                                     .filter(DBYoloAlert.session_id == self.session_id,
                                             DBYoloAlert.alert_type == yolo_alert_type)
                                     .order_by(DBYoloAlert.timestamp.desc()).first())
                    now_dt      = datetime.utcnow()
                    should_save = True
                    if existing_yolo:
                        if (now_dt - existing_yolo.timestamp).total_seconds() < YOLODetector.COOLDOWN_SECONDS:
                            should_save = False
                    if should_save:
                        db.add(DBYoloAlert(
                            session_id   = self.session_id,
                            alert_type   = yolo_alert_type,
                            elapsed_secs = elapsed,
                            details_json = json.dumps({}),
                        ))
                        db.commit()
                finally:
                    db.close()

    def _camera_loop(self, db_factory):
        _pending_mediapipe_alerts = []

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

                progress = max(
                    self.head_est.calibration_progress,
                    self.eye_tracker.calibration_progress
                )

                frame = self._ui.draw(
                    frame, face_count, "—", 0, 0, "—", 0, 0, 0, "—", 0, 0, 0,
                    None, self._fps, calibrating=True, cal_progress=progress
                )
                UIRenderer.draw_hints(frame)

                small = cv2.resize(frame, (640, 480))
                _, jpg = cv2.imencode('.jpg', small, [cv2.IMWRITE_JPEG_QUALITY, 60])
                b64 = base64.b64encode(jpg).decode('utf-8')

                with self._lock:
                    self._latest_state["calibrated"]   = False
                    self._latest_state["cal_progress"]  = progress
                    self._latest_state["face_count"]    = face_count
                    self._latest_state["frame_b64"]     = b64
                    self._latest_state["fps"]           = round(self._fps, 1)
                self._tick_fps()
                continue

            # ── monitoring phase ──────────────────────────────────────────
            if face_count == 0:
                if self.alerts.add("NO_FACE", frame):
                    current_alert = "NO_FACE"
                    _push_log("[FACE] NO_FACE", {"count": 0}, self.session_id)
            elif face_count > 1:
                if self.alerts.add("MULTIPLE_FACES", frame):
                    current_alert = "MULTIPLE_FACES"
                    _push_log("[FACE] MULTIPLE_FACES", {"count": face_count}, self.session_id)

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
                        if self.alerts.add(eye_alert, alert_frame,
                                           {'h_movement': eye_h, 'v_movement': eye_v}):
                            current_alert = eye_alert
                            _push_log(f"[EYE] {eye_alert}", {'h': round(eye_h,3), 'v': round(eye_v,3)}, self.session_id)
                    elif gaze_alert:
                        if self.alerts.add(gaze_alert, alert_frame,
                                           {'yaw': gaze_yaw, 'pitch': gaze_pitch}):
                            current_alert = gaze_alert
                            _push_log(f"[GAZE] {gaze_alert}", {'yaw': round(gaze_yaw,1), 'pitch': round(gaze_pitch,1)}, self.session_id)
                    elif head_alert:
                        if self.alerts.add(head_alert, alert_frame,
                                           {'pitch': head_pitch, 'yaw': head_yaw, 'roll': head_roll}):
                            current_alert = head_alert
                            _push_log(f"[HEAD] {head_alert}", {'p': round(head_pitch,1), 'y': round(head_yaw,1), 'r': round(head_roll,1)}, self.session_id)

            score = self.alerts.suspicion_score()
            self.alerts.update_score_history(score)

            if current_alert:
                elapsed = time.time() - self._session_start
                _pending_mediapipe_alerts.append((current_alert, elapsed))

            self._frame_count += 1
            if self._frame_count % 30 == 0:
                db = db_factory()
                try:
                    for alt, elapsed in _pending_mediapipe_alerts:
                        db.add(DBAlert(
                            session_id    = self.session_id,
                            alert_type    = alt,
                            elapsed_secs  = elapsed,
                            metadata_json = json.dumps({}),
                        ))

                    # Feature 1: Persist keyframes for recent alerts
                    timeline = self.alerts.get_timeline_data()
                    for entry in timeline[-len(_pending_mediapipe_alerts)-2:]:
                        if entry['has_keyframe']:
                            kf_data = self.alerts._keyframes.get(entry['keyframe_idx'])
                            if kf_data:
                                # Check if already saved
                                exists = db.query(DBKeyframe).filter(
                                    DBKeyframe.session_id == self.session_id,
                                    DBKeyframe.elapsed_secs == round(entry['elapsed'], 1)
                                ).first()
                                if not exists:
                                    db.add(DBKeyframe(
                                        session_id   = self.session_id,
                                        alert_type   = entry['type'],
                                        elapsed_secs = round(entry['elapsed'], 1),
                                        image_data   = kf_data,
                                    ))

                    _pending_mediapipe_alerts.clear()
                    db.add(DBScoreHistory(session_id=self.session_id, score=score))
                    db.commit()
                except Exception as e:
                    print(f"DB error: {e}")
                    db.rollback()
                finally:
                    db.close()

            now_t  = time.time()
            cutoff = now_t - Config.SCORING_WINDOW_SECONDS
            breakdown = {}
            for a in self.alerts._alerts:
                if a['time'] >= cutoff:
                    breakdown[a['type']] = breakdown.get(a['type'], 0) + 1

            yolo_alert_type = self._yolo_alert_type
            if yolo_alert_type:
                yolo_labels = {
                    'MULTIPLE_PEOPLE'      : 'YOLO: Multiple people',
                    'CHEATING_ITEM_MOBILE' : 'YOLO: Mobile detected',
                }
                msg = yolo_labels.get(yolo_alert_type, yolo_alert_type.replace('_', ' '))
                cv2.putText(frame, msg, (20, 90),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.65, (0, 130, 255), 2)

            frame = self._ui.draw(
                frame, face_count,
                gaze_dir, gaze_yaw, gaze_pitch,
                head_dir, head_pitch, head_yaw, head_roll,
                eye_dir, eye_h, eye_v,
                score, current_alert, self._fps
            )
            UIRenderer.draw_hints(frame)

            with self._yolo_frame_lock:
                self._yolo_latest_frame = frame.copy()

            _, jpg = cv2.imencode('.jpg', frame, [cv2.IMWRITE_JPEG_QUALITY, 60])
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
                    "yolo_alert"     : yolo_alert_type,
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

        if self._yolo.available:
            self._yolo_thread = threading.Thread(
                target=self._yolo_loop,
                args=(DBSessionLocal,),
                daemon=True
            )
            self._yolo_thread.start()

    def get_state(self) -> dict:
        with self._lock:
            return dict(self._latest_state)

    def finalize(self, db: DBSessionType):
        self._running = False
        if self._thread:
            self._thread.join(timeout=3)
        if self._yolo_thread:
            self._yolo_thread.join(timeout=3)
        self.cap.release()

        score    = self.alerts.suspicion_score()
        rec, _   = self.alerts.recommendation()
        duration = time.time() - self._session_start

        # Persist all remaining keyframes
        timeline = self.alerts.get_timeline_data()
        for entry in timeline:
            if entry['has_keyframe']:
                kf_data = self.alerts._keyframes.get(entry['keyframe_idx'])
                if kf_data:
                    exists = db.query(DBKeyframe).filter(
                        DBKeyframe.session_id == self.session_id,
                        DBKeyframe.elapsed_secs == round(entry['elapsed'], 1)
                    ).first()
                    if not exists:
                        db.add(DBKeyframe(
                            session_id   = self.session_id,
                            alert_type   = entry['type'],
                            elapsed_secs = round(entry['elapsed'], 1),
                            image_data   = kf_data,
                        ))

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
        self._yolo.save_json(f"outputs/yolo_session_{self.session_id}.json")


# ══════════════════════════════════════════════════════════════════════════════
#  FASTAPI APP
# ══════════════════════════════════════════════════════════════════════════════

# ══════════════════════════════════════════════════════════════════════════════
#  LIVE LOG BUFFER  — بدل ما اللوج يتطبع في التيرمنال، بيتبعت للداشبورد
# ══════════════════════════════════════════════════════════════════════════════

import collections as _col_logs

# {session_id: deque of log dicts}  — آخر 200 رسالة لكل سيشن
_log_buffers: dict = {}
_log_subscribers: dict = {}  # {session_id: list of asyncio.Queue}


def _push_log(alert_type: str, details: dict, session_id=None):
    """يخزن اللوج في الـ buffer بدل print. بدون أي طباعة في التيرمنال."""
    entry = {
        "ts"         : datetime.now().strftime("%H:%M:%S"),
        "alert_type" : alert_type,
        "details"    : details,
        "session_id" : session_id,
    }
    # خزّن في buffer
    sid = str(session_id) if session_id else "global"
    if sid not in _log_buffers:
        _log_buffers[sid] = _col_logs.deque(maxlen=200)
    _log_buffers[sid].append(entry)

    # ابعت لكل المشتركين في الـ session دي
    for q in _log_subscribers.get(sid, []):
        try:
            q.put_nowait(entry)
        except Exception:
            pass


active_processors = {}  # type: dict

app = FastAPI(title="JobLens AI — Integrity Monitor")
app.add_middleware(CORSMiddleware, allow_origins=["*"],
                   allow_methods=["*"], allow_headers=["*"])
app.mount("/static", StaticFiles(directory="static"), name="static")


@app.get("/", response_class=HTMLResponse)
async def index():
    with open("templates/index.html", encoding="utf-8") as f:
        return HTMLResponse(f.read())


# ── Feature 2: Pydantic model for session start with candidate profile ────────
class StartSessionRequest(BaseModel):
    candidate_name: Optional[str] = None
    candidate_id: Optional[str] = None


@app.post("/api/sessions/start")
def start_session(req: StartSessionRequest = None):
    """Start a session, optionally with candidate name and ID (Feature 2)."""
    if req is None:
        req = StartSessionRequest()
    db = DBSessionLocal()
    try:
        session = DBSession(
            candidate_name=req.candidate_name or None,
            candidate_id=req.candidate_id or None,
        )
        db.add(session)
        db.commit()
        db.refresh(session)

        try:
            processor = WebSessionProcessor(session.id)
        except RuntimeError as cam_err:
            db.delete(session)
            db.commit()
            raise HTTPException(
                status_code=503,
                detail=(
                    f"Camera error: {cam_err}. "
                    f"Make sure no other app is using the camera "
                    f"and that CAMERA_INDEX={Config.CAMERA_INDEX} is correct."
                )
            )

        processor.start()
        active_processors[session.id] = processor
        return {
            "session_id"     : session.id,
            "started_at"     : session.started_at.isoformat(),
            "candidate_name" : session.candidate_name,
            "candidate_id"   : session.candidate_id,
        }
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
            "candidate_name"  : session.candidate_name,
            "candidate_id"    : session.candidate_id,
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
                "candidate_name"  : s.candidate_name,
                "candidate_id"    : s.candidate_id,
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
        yolo_breakdown = {}
        for ya in session.yolo_alerts:
            yolo_breakdown[ya.alert_type] = yolo_breakdown.get(ya.alert_type, 0) + 1

        # Feature 1: Build timeline events with keyframe availability
        timeline_events = []
        seen_elapsed = set()
        for a in sorted(session.alerts, key=lambda x: x.elapsed_secs):
            kf = next((k for k in session.keyframes
                       if abs(k.elapsed_secs - a.elapsed_secs) < 0.5
                       and k.alert_type == a.alert_type), None)
            key = (round(a.elapsed_secs, 1), a.alert_type)
            if key not in seen_elapsed:
                seen_elapsed.add(key)
                timeline_events.append({
                    "alert_type"  : a.alert_type,
                    "elapsed_secs": a.elapsed_secs,
                    "timestamp"   : a.timestamp.isoformat(),
                    "keyframe_id" : kf.id if kf else None,
                    "has_keyframe": kf is not None,
                })
        # Also add YOLO alerts to timeline
        for ya in sorted(session.yolo_alerts, key=lambda x: x.elapsed_secs):
            timeline_events.append({
                "alert_type"  : ya.alert_type,
                "elapsed_secs": ya.elapsed_secs,
                "timestamp"   : ya.timestamp.isoformat(),
                "keyframe_id" : None,
                "has_keyframe": False,
                "source"      : "yolo",
            })
        timeline_events.sort(key=lambda x: x['elapsed_secs'])

        return {
            "session_id"          : session_id,
            "started_at"          : session.started_at.isoformat() if session.started_at else None,
            "ended_at"            : session.ended_at.isoformat()   if session.ended_at   else None,
            "final_score"         : session.final_score,
            "recommendation"      : session.recommendation,
            "duration_seconds"    : session.duration_seconds,
            "alert_breakdown"     : breakdown,
            "total_alerts"        : len(session.alerts),
            "score_history"       : score_hist,
            "yolo_alert_breakdown": yolo_breakdown,
            "total_yolo_alerts"   : len(session.yolo_alerts),
            "candidate_name"      : session.candidate_name,
            "candidate_id"        : session.candidate_id,
            "timeline_events"     : timeline_events,   # Feature 1
        }
    finally:
        db.close()


# Feature 1: Endpoint to serve keyframe image
@app.get("/api/keyframes/{keyframe_id}")
def get_keyframe(keyframe_id: int):
    from fastapi.responses import Response
    db = DBSessionLocal()
    try:
        kf = db.query(DBKeyframe).filter(DBKeyframe.id == keyframe_id).first()
        if not kf or not kf.image_data:
            raise HTTPException(status_code=404, detail="Keyframe not found")
        return Response(content=kf.image_data, media_type="image/jpeg",
                        headers={"Cache-Control": "max-age=3600"})
    finally:
        db.close()


@app.get("/dashboard", response_class=HTMLResponse)
async def dashboard():
    with open("templates/dashboard.html", encoding="utf-8") as f:
        return HTMLResponse(f.read())


@app.get("/api/dashboard/stats")
def dashboard_stats():
    db = DBSessionLocal()
    try:
        sessions = db.query(DBSession).filter(DBSession.ended_at.isnot(None)).all()
        total   = len(sessions)
        accept  = sum(1 for s in sessions if s.recommendation == "ACCEPT")
        review  = sum(1 for s in sessions if s.recommendation == "REVIEW")
        reject  = sum(1 for s in sessions if s.recommendation == "REJECT")
        scores    = [s.final_score for s in sessions if s.final_score is not None]
        durations = [s.duration_seconds for s in sessions if s.duration_seconds]
        avg_score    = round(sum(scores) / len(scores), 1)    if scores    else 0.0
        avg_duration = round(sum(durations) / len(durations)) if durations else 0
        alert_totals: dict = {}
        for s in sessions:
            for a in s.alerts:
                alert_totals[a.alert_type] = alert_totals.get(a.alert_type, 0) + 1
        most_common = max(alert_totals, key=alert_totals.get) if alert_totals else None
        return {
            "total"            : total,
            "accept"           : accept,
            "review"           : review,
            "reject"           : reject,
            "avg_score"        : avg_score,
            "avg_duration"     : avg_duration,
            "most_common_alert": most_common,
            "alert_totals"     : alert_totals,
        }
    finally:
        db.close()


@app.get("/api/sessions/{session_id}/pdf")
def get_session_pdf(session_id: int):
    from fastapi.responses import FileResponse, PlainTextResponse
    path = os.path.join("outputs", f"session_{session_id}_report.txt")
    if os.path.exists(path):
        return FileResponse(path, media_type="text/plain",
                            filename=f"session_{session_id}_report.txt")
    db = DBSessionLocal()
    try:
        session = db.query(DBSession).filter(DBSession.id == session_id).first()
        if not session:
            raise HTTPException(status_code=404, detail="Session not found")
        breakdown = {}
        for a in session.alerts:
            breakdown[a.alert_type] = breakdown.get(a.alert_type, 0) + 1
        rec = session.recommendation or "PENDING"
        dur = f"{session.duration_seconds:.1f}s" if session.duration_seconds else "—"
        candidate_line = ""
        if session.candidate_name or session.candidate_id:
            candidate_line = f"  Candidate   : {session.candidate_name or '—'} (ID: {session.candidate_id or '—'})\n"
        lines = [
            "=" * 55,
            "  JOBLENS AI — SESSION REPORT",
            "=" * 55,
            f"  Session ID  : {session_id}",
            candidate_line.strip() if candidate_line else "",
            f"  Started     : {session.started_at}",
            f"  Duration    : {dur}",
            f"  Final Score : {session.final_score:.1f}% (Exp. Decay Weighted)",
            f"  Result      : {rec}",
            "",
            "  Alert Breakdown:",
        ]
        lines = [l for l in lines if l != ""]
        for t, c in breakdown.items():
            lines.append(f"    {t:.<30} {c}")
        lines.append("=" * 55)
        return PlainTextResponse("\n".join(lines),
                                 headers={"Content-Disposition":
                                          f'attachment; filename="session_{session_id}_report.txt"'})
    finally:
        db.close()


@app.websocket("/ws/{session_id}")
async def websocket_endpoint(websocket: WebSocket, session_id: int):
    await websocket.accept()
    try:
        processor = active_processors.get(session_id)
        if not processor:
            await websocket.send_json({"error": "Session not found"})
            await websocket.close()
            return

        while True:
            try:
                state = processor.get_state()
                await websocket.send_json(state)
                await asyncio.sleep(0.02)
            except WebSocketDisconnect:
                break
            except Exception as e:
                print(f"WS error: {e}")
                break

    except WebSocketDisconnect:
        pass
    except Exception as e:
        print(f"WS outer error: {e}")


@app.websocket("/ws/logs/{session_id}")
async def websocket_logs(websocket: WebSocket, session_id: int):
    """
    WebSocket بيبعت اللوج live للداشبورد بدل التيرمنال.
    بيبعت الـ history الموجود أول ما حد يتصل، وبعدين updates live.
    """
    await websocket.accept()
    sid = str(session_id)
    q: asyncio.Queue = asyncio.Queue()

    if sid not in _log_subscribers:
        _log_subscribers[sid] = []
    _log_subscribers[sid].append(q)

    try:
        # ابعت الـ history الموجود أولاً
        existing = list(_log_buffers.get(sid, []))
        for entry in existing:
            await websocket.send_json(entry)

        # بعدين استنى updates جديدة
        while True:
            try:
                entry = await asyncio.wait_for(q.get(), timeout=30.0)
                await websocket.send_json(entry)
            except asyncio.TimeoutError:
                # keep-alive ping
                try:
                    await websocket.send_json({"ping": True})
                except Exception:
                    break
            except WebSocketDisconnect:
                break
            except Exception:
                break
    except WebSocketDisconnect:
        pass
    finally:
        if sid in _log_subscribers:
            try:
                _log_subscribers[sid].remove(q)
            except ValueError:
                pass


@app.get("/api/logs/{session_id}")
def get_logs_history(session_id: int):
    """REST endpoint للداشبورد يجيب الـ log history."""
    sid = str(session_id)
    return list(_log_buffers.get(sid, []))


# ══════════════════════════════════════════════════════════════════════════════
#  STANDALONE DESKTOP MODE (unchanged)
# ══════════════════════════════════════════════════════════════════════════════

class CheatingDetectionSystem:
    def __init__(self):
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
                if not self.head_est.is_calibrated:
                    self.head_est.calibrate(lms, img_w, img_h)
                if not self.eye_tracker.is_calibrated:
                    eyes = self.face_det.get_eye_data(lms, img_w, img_h)
                    self.eye_tracker.calibrate(
                        eyes['left_eye'], eyes['left_iris'],
                        eyes['right_eye'], eyes['right_iris']
                    )
                if self.head_est.is_calibrated and self.eye_tracker.is_calibrated:
                    print("✓ CALIBRATION COMPLETE")
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
                eyes['left_eye'], eyes['left_iris'], eyes['right_eye'], eyes['right_iris'])
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
                    return
                frame = self.ui.draw(frame, 0, "—", 0, 0, "—", 0, 0, 0, "—", 0, 0, 0, None, 0, show_consent=True)
                cv2.imshow("JobLens AI — Integrity Monitor", frame)
                key = cv2.waitKey(1) & 0xFF
                if key == 32:
                    self.ui.accept_consent()
                    break
                elif key == 27:
                    self.cap.release()
                    cv2.destroyAllWindows()
                    return

        while True:
            ret, frame = self.cap.read()
            if not ret:
                break
            frame = self._process(frame)
            self._tick_fps()
            cv2.imshow("JobLens AI — Integrity Monitor", frame)
            key = cv2.waitKey(1) & 0xFF
            if key == ord('q'):
                break
            if key == ord('r'):
                path = self.alerts.export_report()
                print(f"✓ Report saved → {path}")
            if key == ord('t'):
                timeline_img = self.alerts.generate_timeline_chart()
                if timeline_img is not None:
                    cv2.imshow("Timeline & Score Chart", timeline_img)
            if key == ord('d'):
                Config.DEBUG_MODE = not Config.DEBUG_MODE
        self._shutdown()

    def _shutdown(self):
        path = self.alerts.export_report("final_report.txt")
        summ = self.alerts.summary()
        rec, reason = self.alerts.recommendation()
        print(f"Score: {summ['score']:.0f}% | {rec}: {reason}")
        self.cap.release()
        cv2.destroyAllWindows()
        self.face_det.close()


if __name__ == "__main__":
    desktop = CheatingDetectionSystem()
    desktop.run()