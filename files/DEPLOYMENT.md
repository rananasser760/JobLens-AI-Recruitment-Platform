# 🚀 JobLens Deployment Guide

Complete guide for deploying JobLens in various environments.

## 📋 Table of Contents

1. [Local Development Setup](#local-development-setup)
2. [Production Deployment](#production-deployment)
3. [Docker Deployment](#docker-deployment)
4. [Cloud Deployment](#cloud-deployment)
5. [Monitoring & Maintenance](#monitoring--maintenance)

---

## 1. Local Development Setup

### Quick Start

```bash
# 1. Create virtual environment
python -m venv venv
source venv/bin/activate  # On Windows: venv\Scripts\activate

# 2. Install dependencies
pip install -r requirements.txt

# 3. Install Playwright browsers
playwright install chromium

# 4. Copy config file
cp config.env.example .env

# 5. Run the API server
python api_server.py
```

### Running Tests

```bash
# Start the server in one terminal
python api_server.py

# Run tests in another terminal
python test_api.py
```

---

## 2. Production Deployment

### System Requirements

- **OS**: Ubuntu 20.04+ or similar Linux distribution
- **RAM**: 8GB minimum, 16GB recommended
- **Storage**: 50GB+ for job database
- **CPU**: 4+ cores recommended
- **Python**: 3.10+

### Step-by-Step Production Setup

#### 1. Server Setup

```bash
# Update system
sudo apt update && sudo apt upgrade -y

# Install Python 3.10
sudo apt install python3.10 python3.10-venv python3-pip -y

# Install system dependencies
sudo apt install -y \
    libnss3 \
    libnspr4 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libxkbcommon0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libasound2
```

#### 2. Application Setup

```bash
# Create application directory
sudo mkdir -p /opt/joblens
sudo chown $USER:$USER /opt/joblens
cd /opt/joblens

# Upload application files
# (Use scp, rsync, or git clone)

# Create virtual environment
python3.10 -m venv venv
source venv/bin/activate

# Install dependencies
pip install -r requirements.txt
playwright install chromium

# Create necessary directories
mkdir -p logs joblens_db
```

#### 3. Configuration

```bash
# Copy and edit configuration
cp config.env.example .env
nano .env

# Key production settings:
# - Set PLAYWRIGHT_HEADLESS=true
# - Configure proper logging
# - Set appropriate rate limits
# - Disable debug features
```

#### 4. Create Systemd Service

Create `/etc/systemd/system/joblens.service`:

```ini
[Unit]
Description=JobLens API Service
After=network.target

[Service]
Type=simple
User=joblens
Group=joblens
WorkingDirectory=/opt/joblens
Environment="PATH=/opt/joblens/venv/bin"
ExecStart=/opt/joblens/venv/bin/python api_server.py
Restart=always
RestartSec=10

# Logging
StandardOutput=append:/opt/joblens/logs/service.log
StandardError=append:/opt/joblens/logs/service.error.log

# Security
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

```bash
# Enable and start service
sudo systemctl daemon-reload
sudo systemctl enable joblens
sudo systemctl start joblens

# Check status
sudo systemctl status joblens
```

#### 5. Nginx Reverse Proxy

Install Nginx:
```bash
sudo apt install nginx -y
```

Create `/etc/nginx/sites-available/joblens`:

```nginx
server {
    listen 80;
    server_name your-domain.com;

    location / {
        proxy_pass http://127.0.0.1:8000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        
        # Timeout for long scraping operations
        proxy_read_timeout 300s;
        proxy_connect_timeout 75s;
    }
}
```

```bash
# Enable site
sudo ln -s /etc/nginx/sites-available/joblens /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl restart nginx
```

#### 6. SSL with Let's Encrypt

```bash
sudo apt install certbot python3-certbot-nginx -y
sudo certbot --nginx -d your-domain.com
```

---

## 3. Docker Deployment

### Dockerfile

Create `Dockerfile`:

```dockerfile
FROM python:3.10-slim

# Install system dependencies
RUN apt-get update && apt-get install -y \
    wget \
    gnupg \
    libnss3 \
    libnspr4 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libxkbcommon0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libasound2 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Install Python dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Install Playwright browsers
RUN playwright install chromium
RUN playwright install-deps

# Copy application files
COPY . .

# Create directories
RUN mkdir -p logs joblens_db

# Expose port
EXPOSE 8000

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD python -c "import requests; requests.get('http://localhost:8000/')"

# Run application
CMD ["python", "api_server.py"]
```

### Docker Compose

Create `docker-compose.yml`:

```yaml
version: '3.8'

services:
  joblens:
    build: .
    container_name: joblens-api
    ports:
      - "8000:8000"
    volumes:
      - ./joblens_db:/app/joblens_db
      - ./logs:/app/logs
    environment:
      - PLAYWRIGHT_HEADLESS=true
      - LOG_LEVEL=INFO
    restart: unless-stopped
    mem_limit: 4g
    cpus: 2

  # Optional: Redis for caching
  redis:
    image: redis:7-alpine
    container_name: joblens-redis
    ports:
      - "6379:6379"
    volumes:
      - redis_data:/data
    restart: unless-stopped

volumes:
  redis_data:
```

### Build and Run

```bash
# Build image
docker-compose build

# Start services
docker-compose up -d

# View logs
docker-compose logs -f joblens

# Stop services
docker-compose down
```

---

## 4. Cloud Deployment

### AWS Deployment

#### Using EC2

1. **Launch EC2 Instance**
   - AMI: Ubuntu 22.04 LTS
   - Instance Type: t3.large (minimum)
   - Storage: 50GB GP3
   - Security Group: Allow ports 22, 80, 443

2. **Setup Application**
   ```bash
   ssh ubuntu@your-ec2-ip
   # Follow production deployment steps
   ```

3. **Configure Load Balancer** (Optional)
   - Create Application Load Balancer
   - Add target group pointing to EC2
   - Configure health checks

#### Using ECS (Docker)

```bash
# Push image to ECR
aws ecr get-login-password --region us-east-1 | docker login --username AWS --password-stdin YOUR_ECR_URL
docker build -t joblens .
docker tag joblens:latest YOUR_ECR_URL/joblens:latest
docker push YOUR_ECR_URL/joblens:latest

# Create ECS task definition and service
# (Use AWS Console or CloudFormation)
```

### Google Cloud Platform

#### Using Cloud Run

```bash
# Build and push to GCR
gcloud builds submit --tag gcr.io/YOUR_PROJECT/joblens

# Deploy to Cloud Run
gcloud run deploy joblens \
    --image gcr.io/YOUR_PROJECT/joblens \
    --platform managed \
    --region us-central1 \
    --memory 4Gi \
    --cpu 2 \
    --timeout 300s \
    --allow-unauthenticated
```

### DigitalOcean

#### Using App Platform

1. Create `app.yaml`:

```yaml
name: joblens
services:
- name: api
  github:
    repo: your-username/joblens
    branch: main
  dockerfile_path: Dockerfile
  http_port: 8000
  instance_count: 1
  instance_size_slug: professional-xs
  routes:
  - path: /
  health_check:
    http_path: /
    initial_delay_seconds: 60
    period_seconds: 30
```

2. Deploy:
```bash
doctl apps create --spec app.yaml
```

---

## 5. Monitoring & Maintenance

### Logging

#### View Logs

```bash
# Systemd service logs
sudo journalctl -u joblens -f

# Application logs
tail -f /opt/joblens/logs/joblens.log

# Docker logs
docker-compose logs -f joblens
```

#### Log Rotation

Create `/etc/logrotate.d/joblens`:

```
/opt/joblens/logs/*.log {
    daily
    rotate 14
    compress
    delaycompress
    notifempty
    create 0640 joblens joblens
    sharedscripts
    postrotate
        systemctl reload joblens
    endscript
}
```

### Monitoring

#### Basic Health Checks

```bash
# Check API status
curl http://localhost:8000/

# Check job statistics
curl http://localhost:8000/api/scraping/jobs/stats
```

#### Automated Monitoring Script

Create `monitor.sh`:

```bash
#!/bin/bash

API_URL="http://localhost:8000"
ALERT_EMAIL="admin@example.com"

# Check API health
if ! curl -f -s "$API_URL/" > /dev/null; then
    echo "API is down!" | mail -s "JobLens Alert" $ALERT_EMAIL
    systemctl restart joblens
fi

# Check disk space
DISK_USAGE=$(df -h /opt/joblens | awk 'NR==2 {print $5}' | sed 's/%//')
if [ $DISK_USAGE -gt 80 ]; then
    echo "Disk usage is at ${DISK_USAGE}%" | mail -s "JobLens Disk Alert" $ALERT_EMAIL
fi

# Check database size
DB_SIZE=$(du -sh /opt/joblens/joblens_db | cut -f1)
echo "Database size: $DB_SIZE"
```

Add to crontab:
```bash
crontab -e
# Add line:
*/5 * * * * /opt/joblens/monitor.sh
```

### Backup Strategy

#### Database Backup

```bash
# Create backup script
cat > /opt/joblens/backup.sh << 'EOF'
#!/bin/bash
DATE=$(date +%Y%m%d_%H%M%S)
BACKUP_DIR="/opt/joblens/backups"
mkdir -p $BACKUP_DIR

# Backup ChromaDB
tar -czf $BACKUP_DIR/joblens_db_$DATE.tar.gz /opt/joblens/joblens_db

# Keep only last 7 days
find $BACKUP_DIR -name "*.tar.gz" -mtime +7 -delete
EOF

chmod +x /opt/joblens/backup.sh
```

Add to crontab:
```bash
0 2 * * * /opt/joblens/backup.sh
```

### Scheduled Scraping

Create a scraping schedule:

```bash
# Create scraping script
cat > /opt/joblens/scheduled_scrape.sh << 'EOF'
#!/bin/bash
curl -X POST http://localhost:8000/api/scraping/trigger \
  -H "Content-Type: application/json" \
  -d '{
    "sources": ["wuzzuf", "linkedin"],
    "categories": ["Data & AI", "IT & Software Development"],
    "max_pages": 3
  }'
EOF

chmod +x /opt/joblens/scheduled_scrape.sh
```

Add to crontab:
```bash
# Run daily at 2 AM
0 2 * * * /opt/joblens/scheduled_scrape.sh
```

### Performance Optimization

#### Database Optimization

```python
# Run periodically to optimize database
from job_scraper_enhanced import CategoryBasedJobScraper
import chromadb

client = chromadb.PersistentClient(path="./joblens_db")
collection = client.get_or_create_collection("job_listings")

# Get all jobs
jobs = collection.get()

# Remove old jobs (> 30 days)
from datetime import datetime, timedelta
cutoff = (datetime.now() - timedelta(days=30)).isoformat()

old_job_ids = []
for i, meta in enumerate(jobs['metadatas']):
    if meta.get('scraped_at', '') < cutoff:
        old_job_ids.append(jobs['ids'][i])

if old_job_ids:
    collection.delete(ids=old_job_ids)
    print(f"Removed {len(old_job_ids)} old jobs")
```

---

## 🔒 Security Best Practices

1. **API Authentication**
   - Add API key authentication for production
   - Use JWT tokens for admin endpoints
   - Implement rate limiting

2. **Environment Variables**
   - Never commit `.env` files
   - Use secret management services
   - Rotate credentials regularly

3. **Network Security**
   - Use HTTPS only
   - Configure firewall rules
   - Enable CORS only for trusted domains

4. **Data Protection**
   - Encrypt sensitive data
   - Regular security updates
   - Implement data retention policies

---

## 📊 Scaling Strategies

### Horizontal Scaling

1. **Load Balancer Setup**
   - Deploy multiple API instances
   - Use shared ChromaDB storage
   - Implement session stickiness

2. **Database Scaling**
   - Move to PostgreSQL for metadata
   - Use Redis for caching
   - Implement read replicas

### Vertical Scaling

- Increase instance size
- Add more RAM for larger models
- Use faster storage (NVMe SSDs)

---

## 🆘 Troubleshooting

### Common Issues

**Issue: High memory usage**
```bash
# Solution: Restart service periodically
0 3 * * * systemctl restart joblens
```

**Issue: Playwright browser crashes**
```bash
# Solution: Install missing dependencies
sudo apt install -y $(playwright install-deps 2>&1 | grep 'apt-get install' | cut -d' ' -f4-)
```

**Issue: Database locked errors**
```bash
# Solution: Ensure only one scraping job runs at a time
# Implement job queue system
```

---

## 📞 Support

For deployment issues:
1. Check logs first
2. Review configuration
3. Test with smaller datasets
4. Monitor resource usage

---

**Happy Deploying! 🚀**
