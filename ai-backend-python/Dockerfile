FROM python:3.11-slim

WORKDIR /app

# Install MS ODBC Driver 17, ffmpeg, and espeak-ng
RUN apt-get update && apt-get install -y \
    libgl1 \
    libglib2.0-0 \
    curl \
    gnupg \
    unixodbc-dev \
    ffmpeg \
    espeak-ng \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && curl -fsSL https://packages.microsoft.com/config/debian/12/prod.list > /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y msodbcsql17 \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Force downgrade transformers to resolve Coqui-TTS incompatibility
RUN pip install --no-cache-dir transformers==4.40.2

COPY . .

EXPOSE 8000 8001

CMD ["python", "main.py"]