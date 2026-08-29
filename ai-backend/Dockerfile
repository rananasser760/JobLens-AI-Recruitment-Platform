FROM python:3.11
WORKDIR /app

# Install system dependencies, OpenCV requirements, and MS ODBC Driver
RUN apt-get update && apt-get install -y \
    curl \
    apt-transport-https \
    unixodbc-dev \
    gnupg2 \
    libgl1 \
    libglx-mesa0 \
    libglib2.0-0 \
    && curl -sSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor > /usr/share/keyrings/microsoft-prod.gpg \
    && curl -sSL https://packages.microsoft.com/config/debian/12/prod.list > /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y msodbcsql18 \
    && rm -rf /var/lib/apt/lists/*

# Install Python dependencies and Playwright browsers
COPY requirements.txt .
RUN pip install --no-cache-dir --upgrade pip && \
    pip install --no-cache-dir -r requirements.txt
RUN playwright install chromium
RUN playwright install-deps

COPY . .
EXPOSE 8000
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]