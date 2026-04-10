from __future__ import annotations

import os
from typing import Dict

import requests
from pyngrok import ngrok


def open_public_tunnel(
    host: str = "127.0.0.1",
    port: int = 8000,
    auth_token: str | None = None,
) -> Dict:
    token = auth_token or os.getenv("NGROK_AUTH_TOKEN", "")
    if not token:
        raise RuntimeError("NGROK_AUTH_TOKEN is missing.")

    target = f"{host}:{port}"

    ngrok.set_auth_token(token)
    try:
        ngrok.kill()
    except Exception:
        pass

    tunnel = ngrok.connect(addr=target, proto="http")
    public_url = tunnel.public_url

    reachable = False
    try:
        response = requests.get(
            f"{public_url}/docs",
            headers={"ngrok-skip-browser-warning": "1"},
            timeout=10,
        )
        reachable = response.status_code == 200
    except Exception:
        reachable = False

    return {
        "public_url": public_url,
        "docs_url": f"{public_url}/docs",
        "reachable": reachable,
    }
