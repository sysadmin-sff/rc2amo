# Deployment Guide

## Overview

This guide provides step-by-step instructions for deploying the RingCentral to amoCRM Integration application in various environments.

---

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Configuration Setup](#configuration-setup)
3. [Local Development](#local-development)
4. [Linux/PM2 Deployment](#linuxpm2-deployment)
5. [Windows/IIS Deployment](#windowsiis-deployment)
6. [Azure App Service Deployment](#azure-app-service-deployment)
7. [Post-Deployment Verification](#post-deployment-verification)
8. [Backup and Rollback](#backup-and-rollback)

---

## Prerequisites

### System Requirements

- **Operating System**: Windows 10+, Linux (Ubuntu 20.04+), or macOS
- **Runtime**: .NET 9.0 SDK (for development) or Runtime (for production)
- **Memory**: 512 MB minimum, 1 GB recommended
- **Storage**: 100 MB for application, additional space for logs
- **Network**: Public HTTPS endpoint accessible by RingCentral

### External Services

1. **RingCentral Account**:
   - Production or Sandbox account
   - JWT credentials (Client ID, Client Secret, JWT token)
   - WebHook-capable subscription permissions

2. **amoCRM Account**:
   - Active amoCRM account with admin access
   - OAuth2 integration created
   - API access enabled

### SSL Certificate

- Valid SSL certificate for HTTPS (Let's Encrypt, DigiCert, etc.)
- Self-signed certificates not accepted by RingCentral in production

---

## Configuration Setup

### Step 1: Obtain RingCentral Credentials

1. Log in to [RingCentral Developer Portal](https://developers.ringcentral.com)
2. Create a new app or select existing
3. Choose **JWT** auth flow
4. Enable permissions:
   - **ReadMessages**
   - **ReadCallLog**
   - **WebhookSubscriptions**
5. Generate JWT token
6. Copy **Client ID**, **Client Secret**, and **JWT token**

### Step 2: Create amoCRM Integration

1. Log in to your amoCRM account as admin
2. Navigate to **Settings** → **Integrations** → **API**
3. Click **Create Integration**
4. Fill in details:
   - **Name**: RingCentral Integration
   - **Redirect URI**: `https://your-domain.com/oauth/callback`
   - **Scopes**: `crm` (all CRM permissions)
5. Save and copy **Client ID** and **Client Secret**

### Step 3: Configure appsettings.json

Create or edit `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  
  "Credentials": {
    "CID": "YOUR_RINGCENTRAL_CLIENT_ID",
    "CS": "YOUR_RINGCENTRAL_CLIENT_SECRET",
    "JWT": "YOUR_RINGCENTRAL_JWT_TOKEN",
    "URL": "https://platform.ringcentral.com",
    "RedirectUri": "https://your-domain.com/rc2amocrm"
  },
  
  "AmoCrm": {
    "ClientId": "YOUR_AMOCRM_CLIENT_ID",
    "ClientSecret": "YOUR_AMOCRM_CLIENT_SECRET",
    "RedirectUri": "https://your-domain.com/oauth/callback",
    "Subdomain": "your_subdomain",
    "RefreshToken": "",
    "AccessToken": ""
  }
}
```

**Important**:
- Replace `your-domain.com` with your actual domain
- Replace `your_subdomain` with your amoCRM subdomain
- Leave `RefreshToken` and `AccessToken` empty initially

### Step 4: Authorize amoCRM

1. Build and start the application (see deployment sections below)
2. Open browser and navigate to:
   ```
   https://your_subdomain.amocrm.ru/oauth?client_id=YOUR_AMOCRM_CLIENT_ID&redirect_uri=https://your-domain.com/oauth/callback&response_type=code
   ```
3. Click **Authorize**
4. You'll be redirected to your application's callback endpoint
5. Tokens will be automatically saved to `appsettings.json`

---

## Local Development

### Method 1: Visual Studio / Rider

1. **Open Solution**:
   ```bash
   cd RingCentral_amoCRM
   ```
   - Open `RingCentral_amoCRM.sln` in Visual Studio or Rider

2. **Configure Launch Settings** (optional):
   Edit `Properties/launchSettings.json`:
   ```json
   {
     "profiles": {
       "RingCentral_amoCRM": {
         "commandName": "Project",
         "launchBrowser": true,
         "launchUrl": "swagger",
         "environmentVariables": {
           "ASPNETCORE_ENVIRONMENT": "Development"
         },
         "applicationUrl": "https://localhost:5001;http://localhost:5000"
       }
     }
   }
   ```

3. **Run**:
   - Press **F5** or click **Run**
   - Application starts on `https://localhost:5001`

4. **Test WebHook** (using ngrok):
   ```bash
   ngrok http 5001
   ```
   - Copy the HTTPS URL (e.g., `https://abc123.ngrok.io`)
   - Update `Credentials:RedirectUri` in `appsettings.json`
   - Restart application

### Method 2: .NET CLI

1. **Restore Dependencies**:
   ```bash
   dotnet restore
   ```

2. **Build**:
   ```bash
   dotnet build
   ```

3. **Run**:
   ```bash
   dotnet run --project RingCentral_amoCRM
   ```

4. **Run with Hot Reload** (development):
   ```bash
   dotnet watch run --project RingCentral_amoCRM
   ```

### Verify Local Deployment

1. Open browser: `https://localhost:5001/swagger`
2. Check Swagger UI loads
3. View logs in console for startup messages:
   ```
   🚀 WebHook Hosted Service starting subscription management...
   🚀 CallLogPollingService starting...
   ```

---

## Linux/PM2 Deployment

### Overview

This application is deployed to `/var/www/rc2amocrm` and managed by PM2 process manager. PM2 runs the application using `dotnet run` command directly from the project directory, so the .NET SDK must be installed on the server.

### Step 1: Install Prerequisites

**Install .NET 9.0 SDK** (not just runtime, as PM2 uses `dotnet run`):
```bash
# Add Microsoft repository
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

# Install .NET SDK
sudo apt update
sudo apt install -y dotnet-sdk-9.0
```

**Verify installation**:
```bash
dotnet --version
# Should show: 9.0.x
```

**Install PM2**:
```bash
# Install Node.js (if not already installed)
curl -fsSL https://deb.nodesource.com/setup_lts.x | sudo -E bash -
sudo apt install -y nodejs

# Install PM2 globally
sudo npm install -g pm2
```

**Install nginx** (for reverse proxy):
```bash
sudo apt update
sudo apt install -y nginx
```

### Step 2: Deploy Application Files

**Option 1: Transfer published Release build from development machine**:

```bash
# On development machine - publish Release build
cd RingCentral_amoCRM
dotnet publish -c Release -o ./publish

# Transfer to server (preserves all dependencies)
rsync -avz --progress ./publish/ user@your-server:/var/www/rc2amocrm/

# Or using SCP
scp -r ./publish/* user@your-server:/var/www/rc2amocrm/
```

**Option 2: Clone repository on server**:

```bash
# Clone repository
cd /var/www
sudo git clone <your-repo-url> rc2amocrm
cd rc2amocrm

# Restore dependencies
sudo dotnet restore
```

**Important Notes**:
- The application runs from the project directory, not from a published output
- PM2 executes `dotnet run` which handles compilation and execution
- Configuration files (`appsettings.json`) must be in the project root

### Step 3: Set Permissions

```bash
# Set ownership (optional, can run as current user)
sudo chown -R $USER:$USER /var/www/rc2amocrm

# Set permissions
sudo chmod 755 /var/www/rc2amocrm
sudo chmod 644 /var/www/rc2amocrm/appsettings.json
```

### Step 4: Configure PM2

**Create PM2 ecosystem file**:

Create `/var/www/rc2amocrm/ecosystem.config.js`:

```javascript
module.exports = {
  apps: [{
    name: 'rc2amocrm',
    cwd: '/var/www/rc2amocrm',
    script: 'dotnet',
    args: 'run --no-launch-profile',
    interpreter: 'none',
    instances: 1,
    autorestart: true,
    watch: false,
    max_memory_restart: '500M',
    env: {
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: 'http://localhost:5000',
      DOTNET_CLI_TELEMETRY_OPTOUT: '1'
    },
    error_file: '/var/www/rc2amocrm/logs/err.log',
    out_file: '/var/www/rc2amocrm/logs/out.log',
    log_file: '/var/www/rc2amocrm/logs/combined.log',
    time: true,
    merge_logs: true
  }]
};
```

**Key Configuration Options**:
- `script: 'dotnet'` - Runs dotnet CLI
- `args: 'run --no-launch-profile'` - Executes dotnet run without launch profiles
- `interpreter: 'none'` - PM2 doesn't wrap the command in a shell
- `cwd: '/var/www/rc2amocrm'` - Working directory (project root)

### Step 5: Start Application with PM2

**Create logs directory**:
```bash
mkdir -p /var/www/rc2amocrm/logs
```

**Start application**:
```bash
cd /var/www/rc2amocrm
pm2 start ecosystem.config.js
```

**Alternative: Simple PM2 start command**:
```bash
cd /var/www/rc2amocrm
pm2 start "dotnet run --no-launch-profile" --name rc2amocrm
```

**Save PM2 process list** (for auto-start on reboot):
```bash
pm2 save
```

**Setup PM2 startup script**:
```bash
# Generate startup script (run as your user, not root)
pm2 startup

# This will output a command like:
# sudo env PATH=$PATH:/usr/bin /usr/lib/node_modules/pm2/bin/pm2 startup systemd -u yourusername --hp /home/yourusername

# Copy and run the generated command
```

### Step 6: PM2 Management Commands

**View running applications**:
```bash
pm2 list
```

**View logs**:
```bash
# All logs
pm2 logs rc2amocrm

# Only errors
pm2 logs rc2amocrm --err

# Live logs (tail)
pm2 logs rc2amocrm --lines 100
```

**Restart application**:
```bash
pm2 restart rc2amocrm
```

**Stop application**:
```bash
pm2 stop rc2amocrm
```

**Delete application from PM2**:
```bash
pm2 delete rc2amocrm
```

**Monitor application**:
```bash
pm2 monit
```

**View detailed info**:
```bash
pm2 show rc2amocrm
```

**Reload application (zero-downtime)**:
```bash
pm2 reload rc2amocrm
```

### Step 7: Configure nginx Reverse Proxy

Create `/etc/nginx/sites-available/rc2amocrm`:

```nginx
server {
    listen 80;
    server_name your-domain.com;
    return 301 https://$server_name$request_uri;
}

server {
    listen 443 ssl http2;
    server_name your-domain.com;

    ssl_certificate /etc/letsencrypt/live/your-domain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/your-domain.com/privkey.pem;

    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;

    # Logs
    access_log /var/log/nginx/rc2amocrm.access.log;
    error_log /var/log/nginx/rc2amocrm.error.log;

    location / {
        proxy_pass http://localhost:5000;
        proxy_http_version 1.1;
        
        # WebSocket support
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection keep-alive;
        
        # Headers
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        
        # Timeouts
        proxy_connect_timeout 60s;
        proxy_send_timeout 60s;
        proxy_read_timeout 60s;
        
        # Buffer settings
        proxy_buffering off;
        proxy_cache_bypass $http_upgrade;
    }
}
```

**Enable site**:
```bash
sudo ln -s /etc/nginx/sites-available/rc2amocrm /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl reload nginx
```

### Step 8: Obtain SSL Certificate (Let's Encrypt)

```bash
sudo apt install certbot python3-certbot-nginx
sudo certbot --nginx -d your-domain.com

# Auto-renewal (certbot usually sets this up automatically)
sudo certbot renew --dry-run
```

### Step 9: Firewall Configuration

```bash
# Allow HTTP and HTTPS
sudo ufw allow 'Nginx Full'

# Or individually
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp

# Enable firewall (if not already enabled)
sudo ufw enable
```

### Step 10: Verify Deployment

**Check PM2 status**:
```bash
pm2 status
```

Expected output:
```
┌────┬────────────┬──────────┬──────┬───────────┬──────────┬──────────┐
│ id │ name       │ mode     │ ↺    │ status    │ cpu      │ memory   │
├────┼────────────┼──────────┼──────┼───────────┼──────────┼──────────┤
│ 0  │ rc2amocrm  │ fork     │ 0    │ online    │ 0%       │ 50.0mb   │
└────┴────────────┴──────────┴──────┴───────────┴──────────┴──────────┘
```

**View logs**:
```bash
pm2 logs rc2amocrm --lines 50
```

**Test endpoints**:
```bash
# Local test
curl http://localhost:5000/swagger

# External test
curl https://your-domain.com/swagger
```

**Check nginx**:
```bash
sudo systemctl status nginx
sudo nginx -t
```

### PM2 Log Rotation (Optional)

Install PM2 log rotate module:
```bash
pm2 install pm2-logrotate

# Configure (optional)
pm2 set pm2-logrotate:max_size 10M
pm2 set pm2-logrotate:retain 30
pm2 set pm2-logrotate:compress true
```

### Updating Application (Standard Workflow)

**Step-by-step update process**:

```bash
# 1. Navigate to application directory
cd /var/www/rc2amocrm

# 2. Backup current appsettings.json (tokens will be preserved)
cp appsettings.json appsettings.json.backup

# 3. Stop PM2 process
pm2 stop rc2amocrm

# 4. On development machine: Publish Release build
# (Run this on your development machine)
cd RingCentral_amoCRM
dotnet publish -c Release -o ./publish

# 5. Transfer published files to server
# (From development machine)
rsync -avz --progress --exclude='appsettings.json' ./publish/ user@your-server:/var/www/rc2amocrm/

# OR using SCP
scp -r $(ls -A ./publish | grep -v appsettings.json) user@your-server:/var/www/rc2amocrm/

# 6. On server: Restore backed-up appsettings.json if it was overwritten
cd /var/www/rc2amocrm
# If appsettings.json was replaced, restore tokens:
# cp appsettings.json.backup appsettings.json

# 7. Restart PM2 process
pm2 restart rc2amocrm

# 8. Check logs for successful startup
pm2 logs rc2amocrm --lines 50

# 9. Verify application is running
pm2 status
curl http://localhost:5000/swagger
```

**Quick Update Script** (create as `/var/www/rc2amocrm/update.sh`):

```bash
#!/bin/bash

echo "🔄 Starting application update..."

# Backup config
cp /var/www/rc2amocrm/appsettings.json /var/www/rc2amocrm/appsettings.json.backup

# Stop application
pm2 stop rc2amocrm

echo "⏸️  Application stopped. Deploy new files now, then press Enter to continue..."
read

# Restore config if overwritten
if [ ! -f /var/www/rc2amocrm/appsettings.json ]; then
    cp /var/www/rc2amocrm/appsettings.json.backup /var/www/rc2amocrm/appsettings.json
    echo "✅ Configuration restored"
fi

# Restart application
pm2 restart rc2amocrm

# Wait for startup
sleep 5

# Check status
pm2 status
pm2 logs rc2amocrm --lines 20

echo "✅ Update complete!"
```

Make executable:
```bash
chmod +x /var/www/rc2amocrm/update.sh
```

### Alternative: Git-Based Updates (if using repository)

```bash
# 1. Navigate to application directory
cd /var/www/rc2amocrm

# 2. Backup configuration
cp appsettings.json appsettings.json.backup

# 3. Pull latest changes
git pull origin main

# 4. Restore configuration
cp appsettings.json.backup appsettings.json

# 5. Restart application (PM2 will run dotnet run and recompile)
pm2 restart rc2amocrm

# 6. Check logs
pm2 logs rc2amocrm
```

---

## Windows/IIS Deployment

### Step 1: Install Prerequisites

1. **Install .NET 9.0 Hosting Bundle**:
   - Download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/9.0)
   - Run installer
   - Restart IIS: `iisreset`

2. **Enable IIS Features**:
   - Open **Turn Windows features on or off**
   - Enable:
     - Internet Information Services
     - Web Management Tools → IIS Management Console
     - World Wide Web Services → Application Development Features → ASP.NET 4.8
     - World Wide Web Services → Security → Basic Authentication

### Step 2: Publish Application

```powershell
dotnet publish -c Release -o C:\inetpub\ringcentral-amocrm
```

### Step 3: Create IIS Site

1. Open **IIS Manager**
2. Right-click **Sites** → **Add Website**
3. Configure:
   - **Site name**: RingCentral-amoCRM
   - **Physical path**: `C:\inetpub\ringcentral-amocrm`
   - **Binding**:
     - Type: https
     - Port: 443
     - Host name: your-domain.com
     - SSL certificate: [Select your certificate]
4. Click **OK**

### Step 4: Configure Application Pool

1. Select **Application Pools** → **RingCentral-amoCRM**
2. **Basic Settings**:
   - **.NET CLR version**: No Managed Code
   - **Managed pipeline mode**: Integrated
3. **Advanced Settings**:
   - **Start Mode**: AlwaysRunning
   - **Idle Time-out**: 0 (disable)
4. Click **OK**

### Step 5: Set Permissions

```powershell
icacls "C:\inetpub\ringcentral-amocrm" /grant "IIS_IUSRS:(OI)(CI)F" /T
```

### Step 6: Configure web.config

Ensure `web.config` exists in publish directory:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet"
                  arguments=".\RingCentral_amoCRM.dll"
                  stdoutLogEnabled="true"
                  stdoutLogFile=".\logs\stdout"
                  hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
```

### Verify

1. Browse to `https://your-domain.com/swagger`
2. Check IIS logs: `C:\inetpub\ringcentral-amocrm\logs\stdout_*.log`

---

## Azure App Service Deployment

### Method 1: Visual Studio Publish

1. **Right-click project** → **Publish**
2. **Target**: Azure App Service (Linux)
3. **Create New App Service**:
   - **Name**: ringcentral-amocrm
   - **Resource Group**: Create new or select existing
   - **Hosting Plan**: Create new (B1 Basic minimum)
   - **Operating System**: Linux
4. **Click Publish**

### Method 2: Azure CLI

**Install Azure CLI**:
```bash
curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
```

**Login**:
```bash
az login
```

**Create Resource Group**:
```bash
az group create --name RingCentralAmoCRM --location eastus
```

**Create App Service Plan**:
```bash
az appservice plan create \
  --name RingCentralPlan \
  --resource-group RingCentralAmoCRM \
  --sku B1 \
  --is-linux
```

**Create Web App**:
```bash
az webapp create \
  --name ringcentral-amocrm \
  --resource-group RingCentralAmoCRM \
  --plan RingCentralPlan \
  --runtime "DOTNET|9.0"
```

**Deploy from Local Git**:
```bash
# Enable local git
az webapp deployment source config-local-git \
  --name ringcentral-amocrm \
  --resource-group RingCentralAmoCRM

# Get deployment URL
az webapp deployment list-publishing-credentials \
  --name ringcentral-amocrm \
  --resource-group RingCentralAmoCRM \
  --query scmUri \
  --output tsv

# Add remote and push
git remote add azure <deployment-url>
git push azure main
```

### Method 3: GitHub Actions

Create `.github/workflows/deploy.yml`:

```yaml
name: Deploy to Azure App Service

on:
  push:
    branches:
      - main

env:
  AZURE_WEBAPP_NAME: ringcentral-amocrm
  DOTNET_VERSION: '9.0.x'

jobs:
  build-and-deploy:
    runs-on: ubuntu-latest

    steps:
    - uses: actions/checkout@v3

    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: ${{ env.DOTNET_VERSION }}

    - name: Restore dependencies
      run: dotnet restore

    - name: Build
      run: dotnet build --configuration Release --no-restore

    - name: Publish
      run: dotnet publish -c Release -o ./publish

    - name: Deploy to Azure Web App
      uses: azure/webapps-deploy@v2
      with:
        app-name: ${{ env.AZURE_WEBAPP_NAME }}
        publish-profile: ${{ secrets.AZURE_WEBAPP_PUBLISH_PROFILE }}
        package: ./publish
```

### Configure Application Settings

**Via Azure Portal**:
1. Navigate to **App Service** → **Configuration**
2. Add Application Settings:
   ```
   Credentials__CID = YOUR_RINGCENTRAL_CLIENT_ID
   Credentials__CS = YOUR_RINGCENTRAL_CLIENT_SECRET
   Credentials__JWT = YOUR_RINGCENTRAL_JWT_TOKEN
   Credentials__URL = https://platform.ringcentral.com
   Credentials__RedirectUri = https://ringcentral-amocrm.azurewebsites.net/rc2amocrm
   
   AmoCrm__ClientId = YOUR_AMOCRM_CLIENT_ID
   AmoCrm__ClientSecret = YOUR_AMOCRM_CLIENT_SECRET
   AmoCrm__RedirectUri = https://ringcentral-amocrm.azurewebsites.net/oauth/callback
   AmoCrm__Subdomain = your_subdomain
   ```

**Via Azure CLI**:
```bash
az webapp config appsettings set \
  --name ringcentral-amocrm \
  --resource-group RingCentralAmoCRM \
  --settings \
  Credentials__CID="YOUR_VALUE" \
  Credentials__CS="YOUR_VALUE" \
  # ... etc
```

### Enable Always On

```bash
az webapp config set \
  --name ringcentral-amocrm \
  --resource-group RingCentralAmoCRM \
  --always-on true
```

### Configure Custom Domain (Optional)

1. **Add Custom Domain**:
   ```bash
   az webapp config hostname add \
     --webapp-name ringcentral-amocrm \
     --resource-group RingCentralAmoCRM \
     --hostname your-domain.com
   ```

2. **Enable HTTPS**:
   - Portal: **App Service** → **Custom domains** → **Add binding**
   - Select **App Service Managed Certificate** (free)

### Verify

```bash
# View logs
az webapp log tail --name ringcentral-amocrm --resource-group RingCentralAmoCRM

# Test endpoint
curl https://ringcentral-amocrm.azurewebsites.net/swagger
```

---

## Post-Deployment Verification

### 1. Health Check

**Test Endpoints**:
```bash
# Swagger UI
curl https://your-domain.com/swagger

# WebHook endpoint (validation)
curl -H "Validation-Token: test123" https://your-domain.com/rc2amocrm/api/RingCentralWebHook/webhook

# Should return 200 OK with Validation-Token header
```

### 2. Authorize amoCRM

1. Navigate to:
   ```
   https://your_subdomain.amocrm.ru/oauth?client_id=YOUR_CLIENT_ID&redirect_uri=https://your-domain.com/oauth/callback&response_type=code
   ```
2. Click **Authorize**
3. Verify success message

### 3. Check Background Services

**View Logs** (method depends on deployment):

- **PM2**: `pm2 logs rc2amocrm`
- **IIS**: Check `C:\inetpub\ringcentral-amocrm\logs\stdout_*.log`
- **Azure**: Use Log Stream in Portal

**Look for**:
```
🚀 WebHook Hosted Service starting subscription management...
✅ Подписка успешно создана для аккаунтов...
🚀 CallLogPollingService starting...
📞 Fetched X call log records
```

### 4. Test SMS Flow

1. Send SMS to your RingCentral number
2. Check logs for:
   ```
   Received SMS from +1234567890...
   Note added to amoCRM (Lead ID: 12345) successfully.
   ```
3. Verify note appears in amoCRM lead

### 5. Test Call Log Polling

1. Make a test call via RingCentral
2. Wait 2 minutes for polling
3. Check logs for:
   ```
   Processing call: From=+1234567890...
   ✅ Note added to lead 12345 for call from +1234567890
   ```
4. Verify call note with recording in amoCRM

### 6. Monitor Subscription Status

```bash
curl https://your-domain.com/rc2amocrm/api/RingCentralWebHook/Subscriptions
```

Should return active subscription with:
- `eventFilters`: SMS message-store
- `deliveryMode.address`: Your WebHook URL
- `expirationTime`: ~6 days from now

---

## Backup and Rollback

### Backup Configuration

**Before Deployment**:
```bash
# Backup config
cp /var/www/rc2amocrm/appsettings.json /var/www/rc2amocrm/appsettings.json.backup.$(date +%Y%m%d_%H%M%S)

# Or create full backup
tar -czf /backups/rc2amocrm_backup_$(date +%Y%m%d).tar.gz \
  /var/www/rc2amocrm/appsettings.json \
  /var/www/rc2amocrm/logs/
```

### Backup Application

**File System**:
```bash
# Create full backup (exclude logs and temp files)
tar -czf /backups/rc2amocrm_app_$(date +%Y%m%d).tar.gz \
  --exclude='logs' \
  --exclude='obj' \
  --exclude='bin' \
  /var/www/rc2amocrm

# Restore
tar -xzf /backups/rc2amocrm_app_20250115.tar.gz -C /var/www/
```

### Rollback Procedure

**PM2 Deployment**:
```bash
# 1. Stop application
pm2 stop rc2amocrm

# 2. Restore backup
rm -rf /var/www/rc2amocrm/*
tar -xzf /backups/rc2amocrm_app_20250115.tar.gz -C /var/www/

# 3. Restart application
pm2 restart rc2amocrm

# 4. Verify
pm2 logs rc2amocrm --lines 50
pm2 status
```

**Azure App Service**:
```bash
# Swap slots (if using deployment slots)
az webapp deployment slot swap \
  --name ringcentral-amocrm \
  --resource-group RingCentralAmoCRM \
  --slot staging

# Or redeploy previous version via Portal:
# App Service → Deployment Center → Logs → Redeploy
```

---

## Troubleshooting Deployment

### Application Won't Start

**Symptoms**: PM2 shows status as "errored" or constant restarts

**Solutions**:
1. Check .NET SDK installed: `dotnet --version`
2. Verify permissions: `ls -la /var/www/rc2amocrm`
3. Check PM2 logs: `pm2 logs rc2amocrm --err --lines 100`
4. Ensure port not already in use: `netstat -tulpn | grep 5000`
5. Verify appsettings.json is valid JSON: `cat /var/www/rc2amocrm/appsettings.json | jq`
6. Check if project files are present: `ls -la /var/www/rc2amocrm/*.csproj`
7. Try running manually: `cd /var/www/rc2amocrm && dotnet run`

### WebHook Validation Fails

**Symptoms**: Subscription shows "Invalid" in RingCentral

**Solutions**:
1. Verify HTTPS certificate valid: `curl -v https://your-domain.com`
2. Check WebHook URL accessible: `curl https://your-domain.com/rc2amocrm/api/RingCentralWebHook/webhook`
3. Review nginx/reverse proxy config: `sudo nginx -t`
4. Check firewall allows RingCentral IPs
5. Verify nginx is proxying to correct port: `sudo systemctl status nginx`
6. Check application is listening on port 5000: `netstat -tulpn | grep 5000`

### Tokens Not Refreshing

**Symptoms**: 401 errors in amoCRM API calls

**Solutions**:
1. Verify `appsettings.json` writable by application: `ls -l /var/www/rc2amocrm/appsettings.json`
2. Re-authorize amoCRM via OAuth flow
3. Check clock sync (token expiry calculation): `timedatectl`
4. Review PM2 logs for refresh errors: `pm2 logs rc2amocrm | grep -i token`
5. Manually check if file is being updated: `stat /var/www/rc2amocrm/appsettings.json`

### High Memory Usage

**Symptoms**: Application consuming >500MB RAM, PM2 restarts due to memory

**Solutions**:
1. Check for memory leaks in PM2 monit: `pm2 monit`
2. Reduce log verbosity in `appsettings.json`
3. Increase PM2 max memory: Edit `ecosystem.config.js` and change `max_memory_restart`
4. Review call log polling frequency
5. Check for log file accumulation: `du -sh /var/www/rc2amocrm/logs/`
6. Clear old build artifacts: `rm -rf /var/www/rc2amocrm/obj /var/www/rc2amocrm/bin`

### PM2 Specific Issues

**PM2 not starting on boot**:
```bash
# Re-run startup command as your user
pm2 startup

# Follow the instructions, then save
pm2 save
```

**PM2 logs not working**:
```bash
# Clear logs
pm2 flush

# Restart with new log settings
pm2 restart rc2amocrm
```

**PM2 shows "errored" status**:
```bash
# View error logs
pm2 logs rc2amocrm --err --lines 100

# Delete and recreate
pm2 delete rc2amocrm
cd /var/www/rc2amocrm
pm2 start ecosystem.config.js
pm2 save
```

**Dotnet run fails with compilation errors**:
```bash
# Clean build artifacts
cd /var/www/rc2amocrm
rm -rf obj bin

# Restore dependencies
dotnet restore

# Try running manually to see errors
dotnet run
```

---

## Security Checklist

- [ ] SSL certificate valid and trusted
- [ ] `appsettings.json` not in version control (.gitignore configured)
- [ ] File permissions set correctly (644 for config, 755 for directories)
- [ ] Firewall configured (only 80/443 open)
- [ ] Secrets stored securely (consider environment variables for production)
- [ ] Admin endpoints protected (or disabled)
- [ ] Logs don't contain sensitive data (passwords, tokens)
- [ ] Regular security updates applied (dotnet, nginx, OS)
- [ ] PM2 running as non-root user
- [ ] nginx security headers configured
- [ ] Application runs with minimal permissions

---

## Maintenance Tasks

### Daily

- Monitor PM2 logs for errors: `pm2 logs rc2amocrm --lines 50`
- Verify background services running: `pm2 status`
- Check disk space: `df -h /var/www/rc2amocrm`

### Weekly

- Check subscription status
- Review failed note creations in logs
- Analyze API error rates
- Rotate PM2 logs if not using auto-rotate: `pm2 flush`
- Check nginx error logs: `sudo tail -50 /var/log/nginx/rc2amocrm.error.log`
- Clean old build artifacts: `rm -rf /var/www/rc2amocrm/obj /var/www/rc2amocrm/bin`

### Monthly

- Update NuGet packages: `cd /var/www/rc2amocrm && dotnet list package --outdated`
- Review and rotate credentials (if policy requires)
- Backup configuration files
- Test disaster recovery procedure
- Update .NET SDK if new version available: `dotnet --version`
- Review PM2 configuration and optimize if needed
- Check SSL certificate expiry: `sudo certbot certificates`

---

## PM2 Advanced Configuration

### Environment-Specific Configuration

Create multiple ecosystem files:

**ecosystem.production.config.js**:
```javascript
module.exports = {
  apps: [{
    name: 'rc2amocrm',
    cwd: '/var/www/rc2amocrm',
    script: 'dotnet',
    args: 'run --no-launch-profile',
    interpreter: 'none',
    instances: 1,
    autorestart: true,
    watch: false,
    max_memory_restart: '500M',
    env_production: {
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: 'http://localhost:5000',
      DOTNET_CLI_TELEMETRY_OPTOUT: '1'
    }
  }]
};
```

Start with specific environment:
```bash
pm2 start ecosystem.production.config.js --env production
```

### PM2 Monitoring

**Basic monitoring**:
```bash
pm2 monit
```

**Web-based monitoring** (PM2 Plus - free tier available):
```bash
pm2 link <secret_key> <public_key>
```

### PM2 with Published DLL (Alternative)

If you prefer to run from a published DLL instead of `dotnet run`:

**ecosystem.config.js** (for published app):
```javascript
module.exports = {
  apps: [{
    name: 'rc2amocrm',
    cwd: '/var/www/rc2amocrm',
    script: 'dotnet',
    args: 'RingCentral_amoCRM.dll',
    interpreter: 'none',
    instances: 1,
    autorestart: true,
    watch: false,
    max_memory_restart: '500M',
    env: {
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: 'http://localhost:5000'
    }
  }]
};
```

**Note**: This requires publishing the app first with `dotnet publish -c Release -o /var/www/rc2amocrm`

---

## Deployment Checklist

### Initial Deployment

- [ ] .NET SDK 9.0 installed
- [ ] PM2 installed and configured
- [ ] nginx installed and configured
- [ ] SSL certificate obtained and installed
- [ ] Firewall rules configured
- [ ] Application files deployed to `/var/www/rc2amocrm`
- [ ] `appsettings.json` configured with credentials
- [ ] PM2 started and saved
- [ ] PM2 startup script configured
- [ ] amoCRM OAuth authorization completed
- [ ] Endpoints tested (local and external)
- [ ] Background services verified in logs
- [ ] Test SMS/Call flow verified

### Before Each Update

- [ ] Backup `appsettings.json`
- [ ] Backup entire application directory
- [ ] Notify users of maintenance window (if applicable)
- [ ] Test new version in development/staging
- [ ] Review changelog for breaking changes

### After Each Update

- [ ] Verify PM2 status: `pm2 status`
- [ ] Check logs for errors: `pm2 logs rc2amocrm --lines 50`
- [ ] Test Swagger UI accessible
- [ ] Verify background services running
- [ ] Test WebHook endpoint
- [ ] Monitor for first 30 minutes

---

*For detailed API documentation, see [API_REFERENCE.md](./API_REFERENCE.md)*

*For architecture details, see [ARCHITECTURE.md](./ARCHITECTURE.md)*

*Last Updated: 2025-01-15*
