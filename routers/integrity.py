# ══════════════════════════════════════════════════════════════════════════════
#  routers/integrity.py
#  All camera / cheating-detection endpoints.
#  Ported from joblens_main.py — imports shared state from session_store.py
#  and DB models from models.py.
# ══════════════════════════════════════════════════════════════════════════════

import asyncio
import base64
import json
import math
import os
import sys
import threading
import time
import queue # Added for WebSocket frame routing
from datetime import datetime
from typing import Optional

# Dynamically add the root folder to the Python path
current_dir = os.path.dirname(os.path.abspath(__file__))
parent_dir = os.path.dirname(current_dir)
if parent_dir not in sys.path:
    sys.path.append(parent_dir)

import cv2
import mediapipe as mp
import numpy as np

# Pre-load MediaPipe solutions in the main thread
mp_face_mesh = mp.solutions.face_mesh
mp_drawing = mp.solutions.drawing_utils
mp_drawing_styles = mp.solutions.drawing_styles

from fastapi import APIRouter, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, PlainTextResponse, Response
from pydantic import BaseModel

from models import (
    DBSession, DBAlert, DBYoloAlert, DBScoreHistory, DBKeyframe,
    DBSessionLocal,
)
from session_store import ACTIVE_PROCESSORS, push_log

router = APIRouter(prefix="/api", tags=["integrity"])

# Shared state guards for concurrent API/thread access.
_ACTIVE_PROCESSORS_LOCK = threading.Lock()
_WS_CONNECTIONS_LOCK = threading.Lock()
_WS_CONNECTIONS: dict[int, int] = {}


def _active_get(session_id: int):
    with _ACTIVE_PROCESSORS_LOCK:
        return ACTIVE_PROCESSORS.get(session_id)


def _active_set(session_id: int, processor) -> None:
    with _ACTIVE_PROCESSORS_LOCK:
        ACTIVE_PROCESSORS[session_id] = processor


def _active_pop(session_id: int):
    with _ACTIVE_PROCESSORS_LOCK:
        return ACTIVE_PROCESSORS.pop(session_id, None)


def _ws_inc(session_id: int) -> None:
    with _WS_CONNECTIONS_LOCK:
        _WS_CONNECTIONS[session_id] = _WS_CONNECTIONS.get(session_id, 0) + 1


def _ws_dec(session_id: int) -> int:
    with _WS_CONNECTIONS_LOCK:
        remaining = max(0, _WS_CONNECTIONS.get(session_id, 0) - 1)
        if remaining == 0:
            _WS_CONNECTIONS.pop(session_id, None)
        else:
            _WS_CONNECTIONS[session_id] = remaining
        return remaining


def _ws_count(session_id: int) -> int:
    with _WS_CONNECTIONS_LOCK:
        return _WS_CONNECTIONS.get(session_id, 0)


def _is_ws_authorized(websocket: WebSocket) -> bool:
    expected_key = os.getenv("JOBLENS_INTERNAL_API_KEY", "").strip()
    if not expected_key:
        return True

    provided_key = (websocket.headers.get("x-api-key") or websocket.query_params.get("api_key") or "").strip()
    return provided_key == expected_key


# ══════════════════════════════════════════════════════════════════════════════
#  CONFIG
# ══════════════════════════════════════════════════════════════════════════════

class Config:
    FRAME_WIDTH  = 640
    FRAME_HEIGHT = 480
    FPS          = 30

    MIN_DETECTION_CONFIDENCE = 0.5
    MIN_TRACKING_CONFIDENCE  = 0.5
    MAX_NUM_FACES    = 2
    REFINE_LANDMARKS = True

    GAZE_YAW_THRESHOLD       = 20
    GAZE_PITCH_UP_THRESHOLD  = 18
    GAZE_PITCH_DOWN_THRESHOLD= 20

    HEAD_YAW_THRESHOLD        = 35
    HEAD_PITCH_UP_THRESHOLD   = 18
    HEAD_PITCH_DOWN_THRESHOLD = 18
    HEAD_ROLL_THRESHOLD       = 22

    EYE_MOVEMENT_LEFT_THRESHOLD  = 0.22
    EYE_MOVEMENT_RIGHT_THRESHOLD = 0.22
    EYE_MOVEMENT_UP_THRESHOLD    = 0.20
    EYE_MOVEMENT_DOWN_THRESHOLD  = 0.20

    ALERT_COOLDOWN = 5

    SCORING_WINDOW_SECONDS = 180
    DECAY_HALF_LIFE        = 200.0
    SCORE_COOLDOWN_SECS    = 30
    SCORE_FLOORS = {80: 78, 60: 58, 40: 38}

    ALERT_WEIGHTS = {
        'NO_FACE': 6, 'MULTIPLE_FACES': 9,
        'LOOKING_LEFT': 2, 'LOOKING_RIGHT': 2,
        'LOOKING_UP': 3, 'LOOKING_DOWN': 2,
        'HEAD_TURNED_LEFT': 2, 'HEAD_TURNED_RIGHT': 2,
        'HEAD_TILTED_UP': 3, 'HEAD_TILTED_DOWN': 2, 'HEAD_TILTED_SIDE': 1,
        'EYE_LEFT': 3, 'EYE_RIGHT': 3, 'EYE_UP': 3, 'EYE_DOWN': 2,
        'MULTIPLE_PEOPLE': 20, 'CHEATING_ITEM_MOBILE': 25,
    }

    MAX_RAW_SCORE_FOR_NORMALIZATION = 100
    CALIBRATION_FRAMES = 70
    BLUR_FACE          = True
    BLUR_KERNEL_SIZE   = 51
    SAVE_ALERTS        = True
    ALERT_FRAMES_DIR   = "outputs/alert_frames"

    KEYFRAME_JPEG_QUALITY = 40
    KEYFRAME_MAX_WIDTH    = 320
    KEYFRAME_BLUR_KERNEL  = 35

    NO_FACE_AUTO_STOP_SECONDS = 30

    LEFT_EYE      = [33, 160, 158, 133, 153, 144]
    LEFT_EYE_IRIS = [468, 469, 470, 471, 472]
    RIGHT_EYE     = [362, 385, 387, 263, 373, 380]
    RIGHT_EYE_IRIS= [473, 474, 475, 476, 477]

    NOSE_TIP         = 1
    CHIN             = 152
    LEFT_EYE_CORNER  = 33
    RIGHT_EYE_CORNER = 263
    LEFT_MOUTH       = 61
    RIGHT_MOUTH      = 291


# ══════════════════════════════════════════════════════════════════════════════
#  DETECTION CLASSES  
# ══════════════════════════════════════════════════════════════════════════════

class FaceDetector:
    def __init__(self):
        self._mesh = mp_face_mesh.FaceMesh(
            max_num_faces=Config.MAX_NUM_FACES,
            refine_landmarks=Config.REFINE_LANDMARKS,
            min_detection_confidence=Config.MIN_DETECTION_CONFIDENCE,
            min_tracking_confidence=Config.MIN_TRACKING_CONFIDENCE,
        )
    def detect(self, frame):
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        rgb.flags.writeable = False
        return self._mesh.process(rgb)
    @staticmethod
    def face_count(results) -> int:
        return len(results.multi_face_landmarks) if results.multi_face_landmarks else 0
    def get_eye_data(self, lms, w, h):
        def _pts(idx):
            return np.array([(int(lms.landmark[i].x*w), int(lms.landmark[i].y*h)) for i in idx])
        return {'left_eye': _pts(Config.LEFT_EYE), 'right_eye': _pts(Config.RIGHT_EYE),
                'left_iris': _pts(Config.LEFT_EYE_IRIS), 'right_iris': _pts(Config.RIGHT_EYE_IRIS)}
    def get_face_bbox(self, lms, w, h):
        xs = [int(l.x*w) for l in lms.landmark]
        ys = [int(l.y*h) for l in lms.landmark]
        return max(0,min(xs)-20), max(0,min(ys)-20), min(w,max(xs)+20), min(h,max(ys)+20)
    def close(self): self._mesh.close()


class EyeMovementTracker:
    def __init__(self):
        self._bl, self._br = None, None
        self._sl, self._sr = [], []
        self._calibrated = False
        self._history = []

    def _iris_pos(self, eye, iris):
        ew = eye[:,0].max()-eye[:,0].min(); eh = eye[:,1].max()-eye[:,1].min()
        if ew==0 or eh==0: return 0.,0.
        cx,cy = np.median(iris[:,0]), np.median(iris[:,1])
        ecx = (eye[:,0].min()+eye[:,0].max())/2; ecy = (eye[:,1].min()+eye[:,1].max())/2
        return float(np.clip((cx-ecx)/(ew/2),-1,1)), float(np.clip((cy-ecy)/(eh/2),-1,1))

    def calibrate(self, le,li,re,ri):
        lh,lv=self._iris_pos(le,li); rh,rv=self._iris_pos(re,ri)
        self._sl.append((lh,lv)); self._sr.append((rh,rv))
        if len(self._sl)>=Config.CALIBRATION_FRAMES:
            self._bl=(float(np.median([s[0] for s in self._sl])),float(np.median([s[1] for s in self._sl])))
            self._br=(float(np.median([s[0] for s in self._sr])),float(np.median([s[1] for s in self._sr])))
            self._calibrated=True; return True
        return False

    @property
    def is_calibrated(self): return self._calibrated
    @property
    def calibration_progress(self): return min(len(self._sl)/Config.CALIBRATION_FRAMES,1.)

    def track(self, le,li,re,ri):
        if not self._calibrated: return 0.,0.,"CENTER",None
        lh,lv=self._iris_pos(le,li); rh,rv=self._iris_pos(re,ri)
        h=(lh-self._bl[0]+rh-self._br[0])/2; v=(lv-self._bl[1]+rv-self._br[1])/2
        self._history.append((h,v))
        if len(self._history)>3: self._history.pop(0)
        w=np.array([0.2,0.3,0.5])[:len(self._history)]; w/=w.sum()
        sh=float(np.average([p[0] for p in self._history],weights=w))
        sv=float(np.average([p[1] for p in self._history],weights=w))
        if abs(sh)>abs(sv):
            if sh>Config.EYE_MOVEMENT_RIGHT_THRESHOLD: return sh,sv,"RIGHT","EYE_RIGHT"
            if sh<-Config.EYE_MOVEMENT_LEFT_THRESHOLD: return sh,sv,"LEFT","EYE_LEFT"
        if sv<-Config.EYE_MOVEMENT_UP_THRESHOLD: return sh,sv,"UP","EYE_UP"
        if sv>Config.EYE_MOVEMENT_DOWN_THRESHOLD: return sh,sv,"DOWN","EYE_DOWN"
        return sh,sv,"CENTER",None


class GazeEstimator:
    def __init__(self): self._history=[]
    def _est(self, eye, iris):
        ew=eye[:,0].max()-eye[:,0].min(); eh=eye[:,1].max()-eye[:,1].min()
        if ew==0 or eh==0: return 0.,0.
        cx,cy=np.median(iris[:,0]),np.median(iris[:,1])
        return float(np.clip((cx-(eye[:,0].min()+eye[:,0].max())/2)/(ew/2),-1,1)), \
               float(np.clip((cy-(eye[:,1].min()+eye[:,1].max())/2)/(eh/2),-1,1))
    def combined_gaze(self,le,li,re,ri):
        lh,lv=self._est(le,li); rh,rv=self._est(re,ri)
        yaw=((lh+rh)/2)*35.; pitch=((lv+rv)/2)*30.
        self._history.append((yaw,pitch))
        if len(self._history)>5: self._history.pop(0)
        return float(np.mean([p[0] for p in self._history])), float(np.mean([p[1] for p in self._history]))
    @staticmethod
    def classify(yaw,pitch):
        if abs(yaw)>abs(pitch):
            if yaw>Config.GAZE_YAW_THRESHOLD: return "RIGHT","LOOKING_RIGHT"
            if yaw<-Config.GAZE_YAW_THRESHOLD: return "LEFT","LOOKING_LEFT"
        if pitch<-Config.GAZE_PITCH_UP_THRESHOLD: return "UP","LOOKING_UP"
        if pitch>Config.GAZE_PITCH_DOWN_THRESHOLD: return "DOWN","LOOKING_DOWN"
        return "CENTER",None


class HeadPoseEstimator:
    _M3D = np.array([(0,0,0),(0,-330,-65),(-225,170,-135),(225,170,-135),(-150,-150,-125),(150,-150,-125)],dtype=np.float64)
    _IDX = [Config.NOSE_TIP,Config.CHIN,Config.LEFT_EYE_CORNER,Config.RIGHT_EYE_CORNER,Config.LEFT_MOUTH,Config.RIGHT_MOUTH]
    def __init__(self): self._history=[]; self._baseline=None; self._cal=[]; self._calibrated=False
    @staticmethod
    def _norm(a):
        while a>180: a-=360
        while a<-180: a+=360
        return a
    @staticmethod
    def _angles(rvec):
        rm,_=cv2.Rodrigues(rvec); n=rm@np.array([0.,0.,-1.]); u=rm@np.array([0.,-1.,0.])
        return math.degrees(math.atan2(n[1],math.sqrt(n[0]**2+n[2]**2))), \
               math.degrees(math.atan2(-n[0],-n[2])), \
               math.degrees(math.atan2(-u[0],-u[1]))
    def _solve(self,lms,w,h):
        pts=np.array([[lms.landmark[i].x*w,lms.landmark[i].y*h] for i in self._IDX],dtype=np.float64)
        cam=np.array([[w,0,w/2],[0,w,h/2],[0,0,1]],dtype=np.float64)
        ok,rv,tv=cv2.solvePnP(self._M3D,pts,cam,np.zeros((4,1)),flags=cv2.SOLVEPNP_ITERATIVE)
        return (rv,tv) if ok else (None,None)
    def calibrate(self,lms,w,h):
        rv,_=self._solve(lms,w,h)
        if rv is None: return False
        p,y,r=self._angles(rv); self._cal.append((p,y,r))
        if len(self._cal)>=Config.CALIBRATION_FRAMES:
            self._baseline=(float(np.median([s[0] for s in self._cal])),
                            float(np.median([s[1] for s in self._cal])),
                            float(np.median([s[2] for s in self._cal])))
            self._calibrated=True; return True
        return False
    @property
    def is_calibrated(self): return self._calibrated
    @property
    def calibration_progress(self): return min(len(self._cal)/Config.CALIBRATION_FRAMES,1.)
    def estimate(self,lms,w,h):
        rv,tv=self._solve(lms,w,h)
        if rv is None: return None,None,None,None,None
        p,y,r=self._angles(rv)
        if self._calibrated and self._baseline:
            p=self._norm(p-self._baseline[0]); y=self._norm(y-self._baseline[1]); r=self._norm(r-self._baseline[2])
        self._history.append((p,y,r))
        if len(self._history)>4: self._history.pop(0)
        return float(np.mean([x[0] for x in self._history])), \
               float(np.mean([x[1] for x in self._history])), \
               float(np.mean([x[2] for x in self._history])), rv, tv
    @staticmethod
    def classify(p,y,r):
        if abs(y)>abs(p) and abs(y)>abs(r):
            if y>Config.HEAD_YAW_THRESHOLD: return "TURNED RIGHT","HEAD_TURNED_RIGHT"
            if y<-Config.HEAD_YAW_THRESHOLD: return "TURNED LEFT","HEAD_TURNED_LEFT"
        if abs(p)>abs(r):
            if p<-Config.HEAD_PITCH_UP_THRESHOLD: return "TILTED UP","HEAD_TILTED_UP"
            if p>Config.HEAD_PITCH_DOWN_THRESHOLD: return "TILTED DOWN","HEAD_TILTED_DOWN"
        if abs(r)>Config.HEAD_ROLL_THRESHOLD: return "TILTED SIDE","HEAD_TILTED_SIDE"
        return "FORWARD",None


class AlertManager:
    def __init__(self):
        self._alerts=[]; self._last_time={}; self._session_start=time.time()
        self._score_history=[]; self._keyframes={}
        if Config.SAVE_ALERTS: os.makedirs(Config.ALERT_FRAMES_DIR,exist_ok=True)

    def add(self, atype, frame=None, metadata=None):
        now=time.time()
        if atype in self._last_time and (now-self._last_time[atype])<Config.ALERT_COOLDOWN: return False
        self._last_time[atype]=now
        idx=len(self._alerts)
        rec={'type':atype,'time':now,'elapsed':now-self._session_start,'metadata':metadata or {},'keyframe_idx':idx}
        if frame is not None:
            try:
                kf=self._capture_keyframe(frame)
                if kf: self._keyframes[idx]=kf
            except: pass
            if Config.SAVE_ALERTS:
                path=os.path.join(Config.ALERT_FRAMES_DIR,f"{atype}_{int(now*1000)}.jpg")
                cv2.imwrite(path,frame); rec['frame_path']=path
        self._alerts.append(rec); return True

    def _capture_keyframe(self,frame):
        h,w=frame.shape[:2]; scale=Config.KEYFRAME_MAX_WIDTH/w
        small=cv2.resize(frame,(Config.KEYFRAME_MAX_WIDTH,int(h*scale)))
        k=Config.KEYFRAME_BLUR_KERNEL
        blurred=cv2.GaussianBlur(small,(k,k),0)
        _,buf=cv2.imencode('.jpg',blurred,[cv2.IMWRITE_JPEG_QUALITY,Config.KEYFRAME_JPEG_QUALITY])
        return buf.tobytes()

    def update_score_history(self,score):
        now=time.time()
        self._score_history.append((now,score))
        cutoff=now-Config.SCORING_WINDOW_SECONDS
        self._score_history=[(t,s) for t,s in self._score_history if t>=cutoff]

    def suspicion_score(self):
        now=time.time(); cutoff=now-Config.SCORING_WINDOW_SECONDS
        lam=math.log(2)/Config.DECAY_HALF_LIFE
        raw=0.; last_t=0.
        for a in self._alerts:
            if a['time']<cutoff: continue
            raw+=Config.ALERT_WEIGHTS.get(a['type'],3)*math.exp(-lam*(now-a['time']))
            if a['time']>last_t: last_t=a['time']
        if last_t>0 and (now-last_t)<Config.SCORE_COOLDOWN_SECS:
            raw*=1.+(1.-(now-last_t)/Config.SCORE_COOLDOWN_SECS)*0.4
        cur=min((raw/Config.MAX_RAW_SCORE_FOR_NORMALIZATION)*100.,100.)
        if not hasattr(self,'_peak'): self._peak=0.
        if cur>self._peak: self._peak=cur
        floor=0.
        for thr in sorted(Config.SCORE_FLOORS,reverse=True):
            if self._peak>=thr: floor=Config.SCORE_FLOORS[thr]; break
        return min(max(cur,floor),100.)

    def recommendation(self):
        s=self.suspicion_score()
        if s<30: return "ACCEPT","Low suspicion"
        if s<70: return "REVIEW","Medium suspicion — manual review recommended"
        return "REJECT","High suspicion — potential cheating detected"

    def summary(self):
        now=time.time(); cutoff=now-Config.SCORING_WINDOW_SECONDS
        bd={}
        for a in self._alerts:
            if a['time']>=cutoff: bd[a['type']]=bd.get(a['type'],0)+1
        return {'total_in_window':sum(bd.values()),'breakdown':bd,
                'score':self.suspicion_score(),'session_duration':now-self._session_start}

    def get_timeline_data(self):
        return [{'elapsed':a['elapsed'],'type':a['type'],'time':a['time'],
                 'keyframe_idx':a['keyframe_idx'],'has_keyframe':a['keyframe_idx'] in self._keyframes}
                for a in self._alerts]

    def export_report(self,filename="final_report.txt"):
        summ=self.summary(); rec,reason=self.recommendation()
        lines=["="*55,"  CHEATING DETECTION — SESSION REPORT","="*55,
               f"  Generated : {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
               f"  Duration  : {summ['session_duration']:.1f} s","",
               f"  Score     : {summ['score']:.1f} / 100",
               f"  Result    : {rec}","  Alerts:"]
        for t,c in summ['breakdown'].items(): lines.append(f"    {t:.<30} {c}")
        lines.append("="*55)
        os.makedirs("outputs",exist_ok=True)
        path=os.path.join("outputs",filename)
        with open(path,'w',encoding='utf-8') as f: f.write('\n'.join(lines))
        return path


# ── YOLO (optional) ───────────────────────────────────────────────────────────
try:
    from ultralytics import YOLO as _YOLO
    _YOLO_AVAILABLE = True
except ImportError:
    _YOLO_AVAILABLE = False


class YOLODetector:
    COOLDOWN=2; PERSON_CONF=0.70; PHONE_CONF=0.70; PHONE_MIN_AREA=0.002
    VOTE_WINDOW={'MULTIPLE_PEOPLE':5,'CHEATING_ITEM_MOBILE':6}
    VOTE_THRESH={'MULTIPLE_PEOPLE':3,'CHEATING_ITEM_MOBILE':2}

    def __init__(self, session_id=None):
        import collections
        self._ok=_YOLO_AVAILABLE; self._coco=None; self._custom=None
        if self._ok:
            try:
                coco_model_path = os.path.join(current_dir, "yolov8n.pt")
                phone_model_path = os.path.join(current_dir, "phones.pt")
                self._coco = _YOLO(coco_model_path)
                self._custom = _YOLO(phone_model_path)
            except Exception as e:
                print(f"YOLO load error: {e}"); self._ok=False
        self.session_id=session_id or datetime.now().strftime("%Y%m%d_%H%M%S")
        self._last_type=None; self._last_logged=0.; self._start=time.time()
        self._votes={k:collections.deque(maxlen=self.VOTE_WINDOW[k]) for k in self.VOTE_WINDOW}
        self.frame_counters={"total":0,"secure":0,"cheating":0,"MULTIPLE_PEOPLE":0,"CHEATING_ITEM_MOBILE":0}

    @property
    def available(self): return self._ok

    @staticmethod
    def _iou(a,b):
        ix1,iy1=max(a[0],b[0]),max(a[1],b[1]); ix2,iy2=min(a[2],b[2]),min(a[3],b[3])
        inter=max(0,ix2-ix1)*max(0,iy2-iy1)
        union=(a[2]-a[0])*(a[3]-a[1])+(b[2]-b[0])*(b[3]-b[1])-inter
        return inter/union if union>0 else 0.

    @classmethod
    def _dedup(cls,boxes,thr):
        kept=[]
        for c in sorted(boxes,key=lambda x:x[4],reverse=True):
            if not any(cls._iou(c[:4],k[:4])>=thr for k in kept): kept.append(c)
        return kept

    def detect(self, frame):
        if not self._ok: return None,"Secure",frame
        h,w=frame.shape[:2]; area=h*w
        pb=[]; phb=[]
        for r in self._coco.predict(frame,conf=self.PERSON_CONF,iou=0.45,verbose=False, device='cpu'):
            for b in r.boxes:
                if int(b.cls[0])!=0: continue
                x1,y1,x2,y2=map(int,b.xyxy[0]); pb.append((x1,y1,x2,y2,round(float(b.conf[0]),2)))
        pb=self._dedup(pb,0.45); pc=len(pb)
        for r in self._custom.predict(frame,conf=self.PHONE_CONF,iou=0.40,verbose=False, device='cpu'):
            for b in r.boxes:
                x1,y1,x2,y2=map(int,b.xyxy[0]); conf=round(float(b.conf[0]),2)
                if (x2-x1)*(y2-y1)<area*self.PHONE_MIN_AREA: continue
                phb.append((x1,y1,x2,y2,conf))
        phb=self._dedup(phb,0.40); phone=len(phb)>0
        raw="MULTIPLE_PEOPLE" if pc>1 else ("CHEATING_ITEM_MOBILE" if phone else None)
        for k,buf in self._votes.items(): buf.append(raw==k)
        confirmed=None
        for k in ("MULTIPLE_PEOPLE","CHEATING_ITEM_MOBILE"):
            if sum(self._votes[k])>=self.VOTE_THRESH[k]: confirmed=k; break
        self.frame_counters["total"]+=1
        if confirmed:
            self.frame_counters["cheating"]+=1; self.frame_counters[confirmed]+=1
            now=time.time()
            if confirmed!=self._last_type or now-self._last_logged>=self.COOLDOWN:
                push_log(f"[YOLO] {confirmed}",{},self.session_id)
                self._last_type=confirmed; self._last_logged=now
        else:
            self.frame_counters["secure"]+=1; self._last_type=None
        return confirmed, "ALERT" if confirmed else "Secure", frame


# ══════════════════════════════════════════════════════════════════════════════
#  WebSessionProcessor (Updated to receive frames via WebSocket queue)
# ══════════════════════════════════════════════════════════════════════════════

class WebSessionProcessor:
    def __init__(self, session_id: int):
        self.session_id      = session_id
        self.face_det        = FaceDetector()
        self.gaze_est        = GazeEstimator()
        self.head_est        = HeadPoseEstimator()
        self.eye_tracker     = EyeMovementTracker()
        self.alerts          = AlertManager()
        self._session_start  = time.time()
        self._frame_count    = 0
        self.calibrated      = False
        self._running        = False
        self._thread         = None
        self._lock           = threading.Lock()
        self._dropped_frames = 0
        self._no_face_since: Optional[float] = None
        self.abandoned       = False

        self._latest_state = {
            "face_count":0,"calibrated":False,"cal_progress":0.,
            "gaze_dir":"—","gaze_yaw":0.,"gaze_pitch":0.,
            "head_dir":"—","head_pitch":0.,"head_yaw":0.,"head_roll":0.,
            "eye_dir":"—","eye_h":0.,"eye_v":0.,
            "score":0.,"current_alert":None,"alert_breakdown":{},
            "frame_b64":None,"yolo_alert":None,"fps":0.,
            "abandoned":False,"no_face_countdown":None,
            "dropped_frames":0,
        }

        # --- FIX: Removed cv2.VideoCapture() completely ---
        # Instead of pulling frames from hardware, we queue them from the browser
        self._frame_queue = queue.Queue(maxsize=5) 
        
        self._yolo           = YOLODetector(session_id=str(session_id))
        self._yolo_frame     = None
        self._yolo_lock      = threading.Lock()
        self._yolo_alert     = None
        self._yolo_thread    = None
        self._fps            = 0.
        self._fps_prev       = time.time()
        self._fps_cnt        = 0

    def push_frame(self, frame):
        """Called by the WebSocket endpoint when a new frame arrives from the browser."""
        if self._frame_queue.full():
            try:
                self._frame_queue.get_nowait()
                with self._lock:
                    self._dropped_frames += 1
                    dropped = self._dropped_frames
                if dropped % 50 == 0:
                    push_log("[FRAME_DROP] input queue overflow", {"dropped_frames": dropped}, self.session_id)
            except queue.Empty:
                pass

        try:
            self._frame_queue.put_nowait(frame)
        except queue.Full:
            # In rare races where queue fills again before put, skip this frame.
            with self._lock:
                self._dropped_frames += 1

    # ── helpers ───────────────────────────────────────────────────────────
    def _blur_face(self, frame, lms, w, h):
        x0,y0,x1,y1=self.face_det.get_face_bbox(lms,w,h)
        roi=frame[y0:y1,x0:x1]
        if roi.size>0:
            k=Config.BLUR_KERNEL_SIZE
            frame[y0:y1,x0:x1]=cv2.GaussianBlur(roi,(k,k),0)
        return frame

    def _tick_fps(self):
        self._fps_cnt+=1
        if self._fps_cnt>=15:
            now=time.time(); elapsed=now-self._fps_prev
            if elapsed>0: self._fps=self._fps_cnt/elapsed
            self._fps_prev=now; self._fps_cnt=0

    def _no_face_check(self, face_count) -> Optional[int]:
        now=time.time(); thr=Config.NO_FACE_AUTO_STOP_SECONDS
        if face_count>0: self._no_face_since=None; return None
        if self._no_face_since is None: self._no_face_since=now
        remaining=int(thr-(now-self._no_face_since))
        if remaining<=0:
            self.abandoned=True; self._running=False
            push_log("[AUTO-STOP] abandoned",{"duration":round(now-self._no_face_since,1)},self.session_id)
            return 0
        return max(remaining,1)

    # ── YOLO thread ───────────────────────────────────────────────────────
    def _yolo_loop(self, db_factory):
        cnt=0
        while self._running:
            with self._yolo_lock:
                frame=self._yolo_frame.copy() if self._yolo_frame is not None else None
            if frame is None: time.sleep(0.05); continue
            cnt+=1
            if cnt%4!=0: time.sleep(0.005); continue
            alert,_,_=self._yolo.detect(frame)
            self._yolo_alert=alert
            if alert:
                elapsed=time.time()-self._session_start
                self.alerts.add(alert,frame)
                db=db_factory()
                try:
                    last=db.query(DBYoloAlert).filter(
                        DBYoloAlert.session_id==self.session_id,
                        DBYoloAlert.alert_type==alert
                    ).order_by(DBYoloAlert.timestamp.desc()).first()
                    if not last or (datetime.utcnow()-last.timestamp).total_seconds()>=YOLODetector.COOLDOWN:
                        db.add(DBYoloAlert(session_id=self.session_id,alert_type=alert,
                                           elapsed_secs=elapsed,details_json="{}"))
                        db.commit()
                finally: db.close()

    # ── camera loop ───────────────────────────────────────────────────────
    def _camera_loop(self, db_factory):
        pending=[]
        while self._running:
            # --- FIX: Pull from Queue instead of cv2.VideoCapture ---
            try:
                frame = self._frame_queue.get(timeout=0.1)
            except queue.Empty:
                continue

            h,w=frame.shape[:2]
            results=self.face_det.detect(frame)
            fc=self.face_det.face_count(results)

            gd,gy,gp="—",0.,0.; hd,hp,hw,hr="—",0.,0.,0.; ed,eh,ev="—",0.,0.; alert=None

            # calibration phase
            if not self.head_est.is_calibrated or not self.eye_tracker.is_calibrated:
                if results.multi_face_landmarks and fc==1:
                    lms=results.multi_face_landmarks[0]
                    if not self.head_est.is_calibrated: self.head_est.calibrate(lms,w,h)
                    if not self.eye_tracker.is_calibrated:
                        eyes=self.face_det.get_eye_data(lms,w,h)
                        self.eye_tracker.calibrate(eyes['left_eye'],eyes['left_iris'],eyes['right_eye'],eyes['right_iris'])
                    if self.head_est.is_calibrated and self.eye_tracker.is_calibrated:
                        self.calibrated=True
                prog=max(self.head_est.calibration_progress,self.eye_tracker.calibration_progress)
                _,jpg=cv2.imencode('.jpg',frame,[cv2.IMWRITE_JPEG_QUALITY,60])
                b64=base64.b64encode(jpg).decode()
                with self._lock:
                    self._latest_state.update({"calibrated":False,"cal_progress":prog,
                                               "face_count":fc,"frame_b64":b64,"fps":round(self._fps,1),
                                               "dropped_frames":self._dropped_frames})
                self._tick_fps(); continue

            # monitoring phase
            countdown=self._no_face_check(fc)
            if self.abandoned:
                threading.Thread(target=self._auto_abandon,args=(db_factory,),daemon=True).start()
                with self._lock:
                    self._latest_state["abandoned"]=True; self._latest_state["no_face_countdown"]=0
                    self._latest_state["dropped_frames"]=self._dropped_frames
                break

            if fc==0:
                if self.alerts.add("NO_FACE",frame): alert="NO_FACE"; push_log("[FACE] NO_FACE",{"count":0},self.session_id)
            elif fc>1:
                if self.alerts.add("MULTIPLE_FACES",frame): alert="MULTIPLE_FACES"; push_log("[FACE] MULTIPLE_FACES",{"count":fc},self.session_id)

            if results.multi_face_landmarks and fc>=1:
                lms=results.multi_face_landmarks[0]
                eyes=self.face_det.get_eye_data(lms,w,h)
                eh,ev,ed,e_alert=self.eye_tracker.track(eyes['left_eye'],eyes['left_iris'],eyes['right_eye'],eyes['right_iris'])
                gy,gp=self.gaze_est.combined_gaze(eyes['left_eye'],eyes['left_iris'],eyes['right_eye'],eyes['right_iris'])
                gd,g_alert=self.gaze_est.classify(gy,gp)
                hp2,hw2,hr2,rvec,tvec=self.head_est.estimate(lms,w,h)
                h_alert=None
                if hp2 is not None:
                    hp,hw,hr=hp2,hw2,hr2; hd,h_alert=self.head_est.classify(hp,hw,hr)
                af=frame.copy()
                if Config.BLUR_FACE: af=self._blur_face(af,lms,w,h)
                if alert is None:
                    for a_type,a_meta in [(e_alert,{'h':eh,'v':ev}),(g_alert,{'yaw':gy,'pitch':gp}),(h_alert,{'p':hp,'y':hw,'r':hr})]:
                        if a_type and self.alerts.add(a_type,af,a_meta):
                            alert=a_type; push_log(f"[DETECT] {a_type}",a_meta,self.session_id); break

            score=self.alerts.suspicion_score(); self.alerts.update_score_history(score)
            if alert: pending.append((alert,time.time()-self._session_start))

            self._frame_count+=1
            if self._frame_count%30==0:
                db=db_factory()
                try:
                    for at,el in pending:
                        db.add(DBAlert(session_id=self.session_id,alert_type=at,elapsed_secs=el,metadata_json="{}"))
                    for entry in self.alerts.get_timeline_data()[-(len(pending)+2):]:
                        if entry['has_keyframe']:
                            kd=self.alerts._keyframes.get(entry['keyframe_idx'])
                            if kd:
                                ex=db.query(DBKeyframe).filter(DBKeyframe.session_id==self.session_id,
                                    DBKeyframe.elapsed_secs==round(entry['elapsed'],1)).first()
                                if not ex:
                                    db.add(DBKeyframe(session_id=self.session_id,alert_type=entry['type'],
                                                      elapsed_secs=round(entry['elapsed'],1),image_data=kd))
                    pending.clear()
                    db.add(DBScoreHistory(session_id=self.session_id,score=score)); db.commit()
                except Exception as e: print(f"DB error: {e}"); db.rollback()
                finally: db.close()

            with self._yolo_lock: self._yolo_frame=frame.copy()
            _,jpg=cv2.imencode('.jpg',frame,[cv2.IMWRITE_JPEG_QUALITY,60])
            b64=base64.b64encode(jpg).decode()
            bd={}
            for a in self.alerts._alerts:
                if a['time']>=time.time()-Config.SCORING_WINDOW_SECONDS:
                    bd[a['type']]=bd.get(a['type'],0)+1
            with self._lock:
                self._latest_state={
                    "calibrated":True,"cal_progress":1.,"face_count":fc,
                    "gaze_dir":gd,"gaze_yaw":round(float(gy),1),"gaze_pitch":round(float(gp),1),
                    "head_dir":hd,"head_pitch":round(float(hp),1),"head_yaw":round(float(hw),1),"head_roll":round(float(hr),1),
                    "eye_dir":ed,"eye_h":round(float(eh),3),"eye_v":round(float(ev),3),
                    "score":round(score,1),"current_alert":alert,"alert_breakdown":bd,
                    "frame_b64":b64,"fps":round(self._fps,1),"yolo_alert":self._yolo_alert,
                    "abandoned":False,"no_face_countdown":countdown,
                    "dropped_frames":self._dropped_frames,
                }
            self._tick_fps()

    def _auto_abandon(self, db_factory):
        db=db_factory()
        try:
            score=self.alerts.suspicion_score(); duration=time.time()-self._session_start
            db.query(DBSession).filter(DBSession.id==self.session_id).update(
                {"ended_at":datetime.utcnow(),"final_score":score,"recommendation":"ABANDONED","duration_seconds":duration})
            db.commit()
        except Exception as e: print(f"abandon DB error: {e}"); db.rollback()
        finally: db.close()
        try:
            self.face_det.close()
            self.alerts.export_report(f"session_{self.session_id}_report.txt")
        except: pass
        _active_pop(self.session_id)

    def start(self):
        self._running=True
        self._thread=threading.Thread(target=self._camera_loop,args=(DBSessionLocal,),daemon=True)
        self._thread.start()
        if self._yolo.available:
            self._yolo_thread=threading.Thread(target=self._yolo_loop,args=(DBSessionLocal,),daemon=True)
            self._yolo_thread.start()

    def get_state(self) -> dict:
        with self._lock: return dict(self._latest_state)

    def finalize(self, db):
        self._running=False
        for t in [self._thread,self._yolo_thread]:
            if t: t.join(timeout=3)
            
        score=self.alerts.suspicion_score()
        rec,_=self.alerts.recommendation()
        if self.abandoned: rec="ABANDONED"
        duration=time.time()-self._session_start
        for entry in self.alerts.get_timeline_data():
            if entry['has_keyframe']:
                kd=self.alerts._keyframes.get(entry['keyframe_idx'])
                if kd:
                    ex=db.query(DBKeyframe).filter(DBKeyframe.session_id==self.session_id,
                        DBKeyframe.elapsed_secs==round(entry['elapsed'],1)).first()
                    if not ex:
                        db.add(DBKeyframe(session_id=self.session_id,alert_type=entry['type'],
                                          elapsed_secs=round(entry['elapsed'],1),image_data=kd))
        for t,s in self.alerts._score_history:
            db.add(DBScoreHistory(session_id=self.session_id,score=s))
        db.query(DBSession).filter(DBSession.id==self.session_id).update(
            {"ended_at":datetime.utcnow(),"final_score":score,"recommendation":rec,"duration_seconds":duration})
        db.commit()
        self.alerts.export_report(f"session_{self.session_id}_report.txt")
        self.face_det.close()


# ══════════════════════════════════════════════════════════════════════════════
#  ENDPOINTS
# ══════════════════════════════════════════════════════════════════════════════

class StartSessionRequest(BaseModel):
    candidate_name: Optional[str] = None
    candidate_id:   Optional[str] = None
    interview_session_id: Optional[str] = None   # link to interview


@router.post("/sessions/start")
def start_session(req: StartSessionRequest = None):
    if req is None: req = StartSessionRequest()
    db = DBSessionLocal()
    try:
        session = DBSession(
            candidate_name=req.candidate_name,
            candidate_id=req.candidate_id,
            interview_session_id=req.interview_session_id,
        )
        db.add(session); db.commit(); db.refresh(session)
        try:
            proc = WebSessionProcessor(session.id)
        except RuntimeError as e:
            db.delete(session); db.commit()
            raise HTTPException(status_code=503, detail=str(e))
        proc.start()
        _active_set(session.id, proc)
        return {"session_id": session.id, "started_at": session.started_at.isoformat(),
                "candidate_name": session.candidate_name, "candidate_id": session.candidate_id}
    finally: db.close()


@router.post("/sessions/{session_id}/end")
def end_session(session_id: int):
    db = DBSessionLocal()
    try:
        proc = _active_pop(session_id)
        if not proc:
            s = db.query(DBSession).filter(DBSession.id==session_id).first()
            if s and s.recommendation=="ABANDONED":
                return {"session_id":session_id,"final_score":s.final_score,
                        "recommendation":"ABANDONED","duration_seconds":s.duration_seconds}
            raise HTTPException(status_code=404, detail="Session not found or already ended")
        proc.finalize(db)
        s = db.query(DBSession).filter(DBSession.id==session_id).first()
        rec,reason = proc.alerts.recommendation()
        if proc.abandoned: rec="ABANDONED"; reason="Auto-stopped — no face detected"
        return {"session_id":session_id,"final_score":s.final_score,"recommendation":rec,
                "reason":reason,"duration_seconds":s.duration_seconds}
    finally: db.close()


@router.get("/sessions")
def list_sessions():
    db = DBSessionLocal()
    try:
        rows = db.query(DBSession).order_by(DBSession.started_at.desc()).limit(50).all()
        return [{"id":s.id,"started_at":s.started_at.isoformat() if s.started_at else None,
                 "ended_at":s.ended_at.isoformat() if s.ended_at else None,
                 "final_score":s.final_score,"recommendation":s.recommendation,
                 "duration_seconds":s.duration_seconds,"alert_count":len(s.alerts),
                 "candidate_name":s.candidate_name,"candidate_id":s.candidate_id,
                 "interview_session_id":s.interview_session_id} for s in rows]
    finally: db.close()


@router.get("/sessions/{session_id}/report")
def get_report(session_id: int):
    db = DBSessionLocal()
    try:
        s = db.query(DBSession).filter(DBSession.id==session_id).first()
        if not s: raise HTTPException(status_code=404, detail="Session not found")
        bd={}
        for a in s.alerts: bd[a.alert_type]=bd.get(a.alert_type,0)+1
        yolo_bd={}
        for ya in s.yolo_alerts: yolo_bd[ya.alert_type]=yolo_bd.get(ya.alert_type,0)+1
        timeline=[]
        seen=set()
        for a in sorted(s.alerts,key=lambda x:x.elapsed_secs):
            kf=next((k for k in s.keyframes if abs(k.elapsed_secs-a.elapsed_secs)<0.5 and k.alert_type==a.alert_type),None)
            key=(round(a.elapsed_secs,1),a.alert_type)
            if key not in seen:
                seen.add(key)
                timeline.append({"alert_type":a.alert_type,"elapsed_secs":a.elapsed_secs,
                                  "timestamp":a.timestamp.isoformat(),"keyframe_id":kf.id if kf else None,
                                  "has_keyframe":kf is not None,"source":"mediapipe"})
        for ya in sorted(s.yolo_alerts,key=lambda x:x.elapsed_secs):
            timeline.append({"alert_type":ya.alert_type,"elapsed_secs":ya.elapsed_secs,
                              "timestamp":ya.timestamp.isoformat(),"keyframe_id":None,
                              "has_keyframe":False,"source":"yolo"})
        timeline.sort(key=lambda x:x['elapsed_secs'])

        # interview summary if linked
        interview_data = None
        if s.interview_summary_json:
            try: interview_data = json.loads(s.interview_summary_json)
            except: pass

        return {
            "session_id": session_id,
            "started_at": s.started_at.isoformat() if s.started_at else None,
            "ended_at":   s.ended_at.isoformat()   if s.ended_at   else None,
            "final_score": s.final_score,
            "recommendation": s.recommendation,
            "duration_seconds": s.duration_seconds,
            "candidate_name": s.candidate_name,
            "candidate_id":   s.candidate_id,
            "alert_breakdown": bd,
            "total_alerts": len(s.alerts),
            "yolo_alert_breakdown": yolo_bd,
            "total_yolo_alerts": len(s.yolo_alerts),
            "score_history": [{"timestamp":sh.timestamp.isoformat(),"score":sh.score} for sh in s.score_history],
            "timeline_events": timeline,
            "interview_session_id": s.interview_session_id,
            "interview_score": s.interview_score,
            "interview_summary": interview_data,
        }
    finally: db.close()


@router.get("/keyframes/{keyframe_id}")
def get_keyframe(keyframe_id: int):
    db = DBSessionLocal()
    try:
        kf = db.query(DBKeyframe).filter(DBKeyframe.id==keyframe_id).first()
        if not kf or not kf.image_data:
            raise HTTPException(status_code=404, detail="Keyframe not found")
        return Response(content=kf.image_data, media_type="image/jpeg",
                        headers={"Cache-Control":"max-age=3600"})
    finally: db.close()


@router.get("/sessions/{session_id}/download")
def download_report(session_id: int):
    path = os.path.join("outputs", f"session_{session_id}_report.txt")
    if os.path.exists(path):
        return FileResponse(path, media_type="text/plain",
                            filename=f"session_{session_id}_report.txt")
    raise HTTPException(status_code=404, detail="Report file not found")


@router.get("/dashboard/stats")
def dashboard_stats():
    db = DBSessionLocal()
    try:
        rows = db.query(DBSession).filter(DBSession.ended_at.isnot(None)).all()
        scores    = [s.final_score for s in rows if s.final_score is not None]
        durations = [s.duration_seconds for s in rows if s.duration_seconds]
        at={}
        for s in rows:
            for a in s.alerts: at[a.alert_type]=at.get(a.alert_type,0)+1
        return {
            "total":     len(rows),
            "accept":    sum(1 for s in rows if s.recommendation=="ACCEPT"),
            "review":    sum(1 for s in rows if s.recommendation=="REVIEW"),
            "reject":    sum(1 for s in rows if s.recommendation=="REJECT"),
            "abandoned": sum(1 for s in rows if s.recommendation=="ABANDONED"),
            "avg_score":    round(sum(scores)/len(scores),1) if scores else 0.,
            "avg_duration": round(sum(durations)/len(durations)) if durations else 0,
            "most_common_alert": max(at,key=at.get) if at else None,
            "alert_totals": at,
        }
    finally: db.close()


def _cleanup_orphan_processor(session_id: int, grace_seconds: float = 20.0):
    """Graceful cleanup when client disconnects and never calls /sessions/{id}/end."""
    time.sleep(grace_seconds)

    if _ws_count(session_id) > 0:
        return

    proc = _active_pop(session_id)
    if not proc:
        return

    db = DBSessionLocal()
    try:
        session = db.query(DBSession).filter(DBSession.id == session_id).first()
        if session and session.ended_at is None:
            proc.abandoned = True
            proc.finalize(db)
            push_log("[AUTO-CLEANUP] websocket disconnected", {"grace_seconds": grace_seconds}, session_id)
        else:
            proc._running = False
            for t in [proc._thread, proc._yolo_thread]:
                if t:
                    t.join(timeout=2)
            try:
                proc.face_det.close()
            except Exception:
                pass
    except Exception as e:
        print(f"orphan cleanup error: {e}")
        db.rollback()
    finally:
        db.close()


# ── WebSocket: live camera state (Updated for Ping-Pong Routing) ──────────────
@router.websocket("/ws/{session_id}")
async def ws_state(websocket: WebSocket, session_id: int):
    if not _is_ws_authorized(websocket):
        await websocket.accept()
        await websocket.close(code=1008)
        return

    await websocket.accept()
    _ws_inc(session_id)
    try:
        proc = _active_get(session_id)
        if not proc:
            await websocket.send_json({"error":"Session not found"})
            await websocket.close()
            return
            
        while True:
            # 1. Wait for the browser to send a frame
            data = await websocket.receive_json()
            if data.get("type") == "frame" and data.get("frame_b64"):
                try:
                    img_data = base64.b64decode(data["frame_b64"])
                    nparr = np.frombuffer(img_data, np.uint8)
                    frame = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
                    if frame is not None:
                        proc.push_frame(frame)
                except Exception as e:
                    print(f"Error decoding frame: {e}")

            # 2. Reply with the analyzed state and the processed face-mesh image
            state = proc.get_state()
            await websocket.send_json(state)
            
            if state.get("abandoned"):
                await asyncio.sleep(0.5)
                break
                
    except WebSocketDisconnect:
        pass
    except Exception as e:
        print(f"WS error: {e}")
    finally:
        remaining = _ws_dec(session_id)
        if remaining == 0:
            threading.Thread(target=_cleanup_orphan_processor, args=(session_id,), daemon=True).start()