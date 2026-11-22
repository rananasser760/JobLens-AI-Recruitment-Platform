# Deploying the JobLens demo

This guide puts the whole platform on one Linux server behind HTTPS. Only ports 80 and 443 are exposed; SQL Server and the AI services stay on the private Docker network.

## 1. Server requirements

- x86_64 (amd64) Linux. SQL Server and the ODBC driver used by the integrity service do not run on ARM.
- Docker Engine 24+ with Docker Compose v2.
- Plenty of memory: Parakeet, Coqui TTS, YOLOv8, MediaPipe, sentence-transformers, GPT-2 and SQL Server all run on CPU. 16 GB is a comfortable starting point.
- A domain (or free subdomain) whose DNS A record points at the server. Caddy obtains the HTTPS certificate automatically.

## 2. Files that are not in Git

Copy these to the server yourself:

- `ai-integrity-service/routers/yolov8n.pt` and `ai-integrity-service/routers/phones.pt` (`*.pt` is gitignored; without them phone/person detection is silently disabled).
- `ai-integrity-service/.env`, `ai-interview-service/.env`, `ai-recruitment-service/.env` (they hold your OpenRouter/Groq keys).

## 3. Configure secrets

```bash
cp .env.example .env
openssl rand -hex 32   # use for JOBLENS_INTERNAL_API_KEY
openssl rand -hex 48   # use for JWT_SIGNING_KEY
```

Fill in `JOBLENS_DOMAIN`, `MSSQL_SA_PASSWORD`, `ADMIN_PASSWORD` and `DEMO_PASSWORD`. Never commit `.env`.

## 4. Start

```bash
docker compose -f docker-compose.prod.yml up -d --build
docker compose -f docker-compose.prod.yml logs -f dotnet-gateway
```

The first start downloads the AI models and can take a while.

## 5. Demo accounts

With `DEMO_SEED_ENABLED=true` and a `DEMO_PASSWORD`, the gateway creates:

- `recruiter@demo.joblens`: recruiter for "JobLens Demo Co." with two sample jobs
- `candidate@demo.joblens`: candidate profile ready to upload a CV and apply

Both use `DEMO_PASSWORD`, so you can share them with recruiters. The admin account (`ADMIN_EMAIL` / `ADMIN_PASSWORD`) can open `/hangfire` and trigger scraping.

## 6. Staying free

All LLM calls use OpenRouter free models. `JOBLENS_FREE_MODELS_ONLY=true` (the default) appends `:free` to any OpenRouter model name that lacks it, so a typo can never bill you. Free models are rate-limited, so slow or failed AI steps are retried in the background (ATS scoring is retried every 30 minutes).

## Local development notes

- `docker-compose.yml` now reads `MSSQL_SA_PASSWORD`, `JOBLENS_INTERNAL_API_KEY` and `JWT_SIGNING_KEY` from the root `.env`.
- An existing `mssql_data` volume keeps its old `sa` password. Either run `docker compose down -v` once (this deletes local data) or put the old password in `.env` for local use.
- SQL Server (1433) and the AI gateway (8000) are now bound to `127.0.0.1` only.
- `appsettings.Development.json` seeds `admin@joblens.local` / `Admin@123` and demo accounts with `Demo@12345` for local runs only.

## Changing code without re-downloading

Everything big (Python packages, PyTorch, the Parakeet/Coqui/GPT-2/embedding models, Playwright Chromium, NuGet and npm packages) is downloaded once and then kept in Docker's build cache and named volumes. Code edits never need a rebuild.

Daily workflow (local):

```bash
docker compose up -d          # start everything, no --build
docker compose logs -f ai-interview-service
```

- Python services: saving a `.py` file reloads the service automatically (source is mounted into the container).
- .NET gateway: `dotnet watch` rebuilds on save.
- Angular: `ng serve` reloads on save.

Only rebuild when dependencies change, and only that service:

| You changed | Run |
| --- | --- |
| `requirements.txt` of one service | `docker compose build ai-interview-service` (only new packages download) |
| `package.json` | `docker compose build angular-frontend` |
| a `.csproj` package reference | `docker compose build dotnet-gateway` |
| a `Dockerfile` | `docker compose build <that service>` |

Never run these unless you want to download everything again:

- `docker compose build --no-cache` or `docker compose up --build --force-recreate` as a habit
- `docker compose down -v` (deletes the model and database volumes)
- `docker system prune -a`, `docker builder prune`, or Docker Desktop's "Clean / Purge data"

`docker system df` shows how much is cached.

On the server, updating code is `git pull` followed by `docker compose -f docker-compose.prod.yml up -d --build`. Dependency layers come from the cache, so only your source files are copied.
