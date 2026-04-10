import base64
import os
import tempfile
import wave

import cv2
import numpy as np
from fastapi.testclient import TestClient

import main
import routers.interview as interview_router
from models import DBSession, DBSessionLocal


def _tiny_frame_b64() -> str:
    img = np.zeros((64, 64, 3), dtype=np.uint8)
    ok, jpg = cv2.imencode(".jpg", img)
    if not ok:
        raise RuntimeError("Failed to encode test frame")
    return base64.b64encode(jpg.tobytes()).decode()


def _write_test_wav(path: str) -> None:
    with wave.open(path, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(16000)
        wav_file.writeframes(b"\x00\x00" * 8000)


def _assert(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def test_integrity_flow_and_report_aliases(client: TestClient, frame_b64: str) -> None:
    start = client.post("/api/sessions/start", json={})
    _assert(start.status_code == 200, f"Integrity start failed: {start.status_code}")
    sid = start.json()["session_id"]

    with client.websocket_connect(f"/api/ws/{sid}") as ws:
        ws.send_json({"type": "frame", "frame_b64": frame_b64})
        state = ws.receive_json()
        _assert("score" in state, "Integrity WS state missing score")

    end = client.post(f"/api/sessions/{sid}/end")
    _assert(end.status_code == 200, f"Integrity end failed: {end.status_code}")

    unified = client.get(f"/api/report/{sid}")
    _assert(unified.status_code == 200, f"Unified report failed: {unified.status_code}")
    payload = unified.json()

    required_aliases = [
        "final_score",
        "recommendation",
        "alert_breakdown",
        "score_history",
        "timeline_events",
        "interview_summary",
        "interview_score",
    ]
    for key in required_aliases:
        _assert(key in payload, f"Unified report missing alias field: {key}")


def test_interview_flow(client: TestClient, wav_path: str) -> None:
    # Monkeypatch expensive/runtime-dependent pieces for deterministic smoke test.
    original_to_wav = interview_router._to_wav
    original_stt = interview_router.speech_to_text
    original_gen = interview_router.generate_interview_response
    original_tts = interview_router.text_to_speech_file

    def fake_to_wav(src: str, dst: str) -> None:
        with open(dst, "wb") as f:
            f.write(b"RIFF____WAVEfmt ")

    def fake_speech_to_text(path: str) -> str:
        return "sample candidate answer"

    def fake_generate_interview_response(current_transcript, chat_history, cv_text) -> str:
        return "Thanks. Next question please."

    def fake_tts(text: str) -> str:
        return wav_path

    interview_router._to_wav = fake_to_wav
    interview_router.speech_to_text = fake_speech_to_text
    interview_router.generate_interview_response = fake_generate_interview_response
    interview_router.text_to_speech_file = fake_tts

    try:
        start = client.post(
            "/interview/start",
            data={
                "cv_text": "test cv",
                "max_questions": "3",
                "evaluation_criteria": "technical",
            },
        )
        _assert(start.status_code == 200, f"Interview start failed: {start.status_code}")
        interview_id = start.json()["interview_session_id"]

        with client.websocket_connect(f"/interview/ws/{interview_id}") as ws:
            ws.send_bytes(b"fake-audio")
            msg = ws.receive_json()
            _assert(msg.get("type") == "transcript", "Interview WS did not return transcript message")
            _assert(msg.get("is_complete") is False, "Interview unexpectedly completed on first turn")
    finally:
        interview_router._to_wav = original_to_wav
        interview_router.speech_to_text = original_stt
        interview_router.generate_interview_response = original_gen
        interview_router.text_to_speech_file = original_tts


def test_linked_handoff_abandonment(client: TestClient) -> None:
    # Start integrity
    start_integrity = client.post("/api/sessions/start", json={})
    _assert(start_integrity.status_code == 200, "Integrity start for handoff failed")
    integrity_id = start_integrity.json()["session_id"]

    # Start interview linked to integrity
    start_interview = client.post(
        "/interview/start",
        data={
            "cv_text": "linked cv",
            "max_questions": "3",
            "evaluation_criteria": "technical",
            "integrity_db_session_id": str(integrity_id),
        },
    )
    _assert(start_interview.status_code == 200, "Linked interview start failed")
    interview_id = start_interview.json()["interview_session_id"]

    # Force integrity recommendation to ABANDONED to simulate linked termination.
    db = DBSessionLocal()
    try:
        row = db.query(DBSession).filter(DBSession.id == integrity_id).first()
        _assert(row is not None, "Linked integrity row not found")
        row.recommendation = "ABANDONED"
        db.commit()
    finally:
        db.close()

    with client.websocket_connect(f"/interview/ws/{interview_id}") as ws:
        msg = ws.receive_json()
        _assert(msg.get("type") == "transcript", "Linked abandonment did not return transcript message")
        _assert(msg.get("is_complete") is True, "Linked abandonment did not mark interview complete")
        _assert(msg.get("user") == "(System)", "Linked abandonment did not return system user marker")

    # Cleanup integrity processor/session if still active.
    client.post(f"/api/sessions/{integrity_id}/end")


def main_smoke() -> None:
    client = TestClient(main.app)
    frame_b64 = _tiny_frame_b64()

    with tempfile.TemporaryDirectory() as td:
        wav_path = os.path.join(td, "test_tts.wav")
        _write_test_wav(wav_path)

        test_integrity_flow_and_report_aliases(client, frame_b64)
        print("PASS: integrity flow + unified report aliases")

        test_interview_flow(client, wav_path)
        print("PASS: interview flow")

        test_linked_handoff_abandonment(client)
        print("PASS: linked handoff abandonment behavior")

    print("PASS: merge regression smoke suite")


if __name__ == "__main__":
    main_smoke()