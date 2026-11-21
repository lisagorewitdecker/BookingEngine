# BookingEngine - Kubernetes Deployment Package

## 📦 Deployment Package Contents

Your BookingEngine application is fully configured for Kubernetes deployment to a Development environment.

### Generated Artifacts

#### 1. **Container Image**
- **Dockerfile** - Multi-stage Docker build
  - Compiles .NET 11 application
  - Publishes optimized release binaries
  - Runtime: aspnet:11.0 base image
  - Includes health checks and diagnostics

#### 2. **Kubernetes Manifests**

**k8s-deployment.yaml** - Core Infrastructure
- Namespace: `booking-engine`
- PostgreSQL 16 StatefulSet (1 replica)
- BookingEngine API Deployment (2 replicas)
- Service for internal routing (ClusterIP)
- ConfigMap for application configuration
- Secret for database credentials
- ServiceAccount & RBAC

**k8s-ingress-hpa.yaml** - Advanced Features
- Ingress Controller integration (NGINX)
- Hostname: `booking-engine.local`
- TLS support (staging certificates)
- Horizontal Pod Autoscaler (2-5 replicas)
  - CPU target: 70%
  - Memory target: 80%
- Pod Disruption Budget (minimum 1 available)

#### 3. **Deployment Scripts**

**deploy.sh** - Automated Deployment
```bash
./deploy.sh [version]
```
- Builds Docker image
- Pushes to registry
- Creates namespace
- Applies all manifests
- Waits for rollout
- Shows status and access instructions

**undeploy.sh** - Graceful Cleanup
```bash
./undeploy.sh
```
- Scales down deployment
- Deletes resources safely
- Preserves namespace for redeployment

#### 4. **Documentation**

| Document | Purpose | Audience |
|----------|---------|----------|
| **DEPLOYMENT_SUMMARY.md** | Quick start guide | First-time deployers |
| **KUBERNETES_DEPLOYMENT.md** | Complete reference | DevOps engineers |
| **DEPLOYMENT_CHECKLIST.md** | Verification steps | QA/Testing teams |
| **CONFIGURATION.md** | Settings reference | Configuration managers |
| **README.md** (this file) | Package overview | Everyone |

---

## 🚀 Quick Start (60 seconds)

### Prerequisites
```bash
# Kubernetes cluster access
kubectl cluster-info

# Docker installed
docker --version
```

### Deploy
```bash
# Navigate to project root
cd BookingEngine

# Run deployment
./deploy.sh
```

### Verify
```bash
# Check status
kubectl get pods -n booking-engine

# Access application
kubectl port-forward -n booking-engine svc/booking-engine-service 8080:80
# Visit: http://localhost:8080
```

---

## 📋 Deployment Architecture

```
┌─────────────────────────────────────────┐
│      Kubernetes Cluster                 │
│      (booking-engine namespace)         │
├─────────────────────────────────────────┤
│                                         │
│  Ingress (booking-engine.local)         │
│         ↓                               │
│  Service (ClusterIP:80)                 │
│         ↓                               │
│  Deployment (2-5 replicas) ←─── HPA    │
│  ├─ Pod 1: BookingEngine API            │
│  ├─ Pod 2: BookingEngine API            │
│  └─ Pod N: BookingEngine API            │
│         ↓                               │
│  Service (postgres-service:5432)        │
│         ↓                               │
│  StatefulSet (postgres-0)               │
│  └─ PostgreSQL 16 Database              │
│                                         │
└─────────────────────────────────────────┘
```

---

## 🎯 Key Features

✅ **High Availability**
- 2 replicas minimum
- Scales up to 5 based on load
- Pod Disruption Budget ensures 1 always available
- Rolling updates with zero downtime

✅ **Self-Healing**
- Liveness probe restarts unhealthy pods
- Readiness probe removes failing pods from load balancer
- Automatic recovery from pod failures

✅ **Resource Management**
- CPU requests: 250m (25% core)
- CPU limits: 500m (50% core)
- Memory requests: 512Mi
- Memory limits: 1Gi
- HPA scales based on 70% CPU / 80% memory

✅ **Security**
- Non-root container execution
- Read-only root filesystem
- Dropped Linux capabilities
- RBAC restrictions
- Secrets for sensitive data
- No privilege escalation

✅ **Observability**
- Health checks (/health endpoint)
- Structured logging
- Kubernetes events integration
- Resource metrics
- HPA scaling decisions

---

## 📝 Configuration Highlights

### Database
- **Engine**: PostgreSQL 16
- **Connection**: Internal service (postgres-service:5432)
- **Credentials**: 
  - User: `booking_user`
  - Password: `booking_password` (changeable)
  - Database: `booking_engine`

### Application
- **Port**: 8080 (internal), 80 (external via service)
- **Environment**: Development
- **Framework**: .NET 11 (ASP.NET Core)
- **API Endpoint**: /api/availability/slots

### Network
- **Service Type**: ClusterIP (internal routing)
- **Ingress**: NGINX controller
- **Hostname**: `booking-engine.local`
- **Port**: 80 (HTTP)

### Scaling
- **Min Replicas**: 2
- **Max Replicas**: 5
- **CPU Target**: 70% utilization
- **Memory Target**: 80% utilization

---

## 📚 Documentation Guide

### For Quick Deployment
1. Read: **DEPLOYMENT_SUMMARY.md** (5 min)
2. Run: `./deploy.sh`
3. Verify with checklists

### For Complete Understanding
1. Read: **KUBERNETES_DEPLOYMENT.md** (20 min)
2. Review: **k8s-deployment.yaml** (10 min)
3. Review: **k8s-ingress-hpa.yaml** (5 min)

### For Configuration Changes
1. Refer: **CONFIGURATION.md**
2. Edit: Relevant Kubernetes manifest
3. Apply: `kubectl apply -f <file>`

### For Troubleshooting
1. Check: **DEPLOYMENT_CHECKLIST.md** - Common Issues
2. Use: Provided kubectl commands
3. Review: Application and database logs

---

## 🔧 Common Tasks

### View Application Logs
```bash
kubectl logs -n booking-engine deployment/booking-engine -f
```

### Access Database
```bash
kubectl port-forward -n booking-engine postgres-0 5432:5432
psql -h localhost -U booking_user -d booking_engine
```

### Update Application
```bash
docker build -t booking-engine:v1.1.0 .
docker tag booking-engine:v1.1.0 localhost:5000/booking-engine:v1.1.0
docker push localhost:5000/booking-engine:v1.1.0

kubectl set image deployment/booking-engine \
  booking-engine=localhost:5000/booking-engine:v1.1.0 \
  -n booking-engine
```

### Scale Manually
```bash
kubectl scale deployment/booking-engine -n booking-engine --replicas=4
```

### Monitor HPA
```bash
kubectl get hpa -n booking-engine -w
```

### Delete Deployment
```bash
kubectl delete namespace booking-engine
```

---

## ⚠️ Important Notes

### Health Endpoint
The deployment expects a `/health` endpoint. If your application doesn't have one, implement it or update the probes in `k8s-deployment.yaml`.

### Image Registry
Default configuration uses `localhost:5000` (Kubernetes Docker Desktop).
- For other registries, update image references in `k8s-deployment.yaml`
- For private registries, configure ImagePullSecret

### Database Persistence
Current setup uses `emptyDir` (ephemeral storage).
- Data is lost when pod restarts
- For production, replace with PersistentVolumeClaim
- See CONFIGURATION.md for PVC setup

### Ingress Controller
Requires NGINX Ingress Controller to be installed.
- If not available, use `kubectl port-forward` for access
- For minikube: `minikube addons enable ingress`

---

## 🔐 Security Checklist

✅ Container runs as non-root (UID 1000)
✅ Read-only root filesystem
✅ Linux capabilities dropped (CAP_ALL)
✅ No privilege escalation allowed
✅ RBAC configured with minimal permissions
✅ Secrets for sensitive data

⏳ Recommended for Production:
- [ ] Network policies
- [ ] Pod Security Policies
- [ ] TLS encryption (mTLS)
- [ ] Secret encryption at rest
- [ ] Image scanning
- [ ] Regular backup strategy

---

## 📊 Resource Requirements

### Minimum Cluster Configuration
- 2 worker nodes (minimum 2GB memory each)
- 2 CPU cores total (2×1 core or 1×2 core)
- 4GB total memory
- 10GB storage (for PostgreSQL)

### Recommended Cluster Configuration
- 3+ worker nodes (3GB+ memory each)
- 4+ CPU cores total
- 6GB+ total memory
- 20GB+ storage

### Per-Pod Resource Usage
- **Request**: 250m CPU, 512Mi memory
- **Limit**: 500m CPU, 1Gi memory
- **Database**: 250m CPU, 256Mi memory

---

## 🚀 Deployment Timeline

| Phase | Action | Time |
|-------|--------|------|
| **Build** | Docker image build | 2-5 min |
| **Push** | Push to registry | 1-2 min |
| **Deploy** | Apply manifests | <1 min |
| **Startup** | Pod initialization | 10-30 sec |
| **Ready** | Health checks pass | 10-30 sec |
| **Total** | Complete deployment | 4-8 min |

---

## 📞 Support & Resources

### Kubernetes
- Official Docs: https://kubernetes.io/docs/
- .NET on K8s: https://learn.microsoft.com/en-us/dotnet/architecture/cloud-native/

### Troubleshooting
1. Check pod logs: `kubectl logs -n booking-engine <pod>`
2. Describe resources: `kubectl describe pod <pod> -n booking-engine`
3. Check events: `kubectl get events -n booking-engine`

### Docker
- Official Docs: https://docs.docker.com/

---

## ✨ Next Steps After Deployment

1. ✅ Verify all pods are running
2. ✅ Test API endpoints
3. ⏳ Set up monitoring (Prometheus/Grafana)
4. ⏳ Configure persistent storage
5. ⏳ Set up automated backups
6. ⏳ Implement network policies
7. ⏳ Configure SSL/TLS certificates

---

## 📝 File Manifest

```
BookingEngine/
├── Dockerfile                      # Docker image definition
├── k8s-deployment.yaml            # Core Kubernetes resources
├── k8s-ingress-hpa.yaml           # Ingress & autoscaling
├── deploy.sh                      # Deployment automation
├── undeploy.sh                    # Cleanup automation
├── DEPLOYMENT_SUMMARY.md          # Quick reference
├── KUBERNETES_DEPLOYMENT.md       # Complete guide
├── DEPLOYMENT_CHECKLIST.md        # Verification steps
├── CONFIGURATION.md               # Settings reference
└── README.md                      # This file
```

---

## 🎓 Learning Resources

### For Kubernetes Beginners
1. Start with DEPLOYMENT_SUMMARY.md
2. Run automated deployment: `./deploy.sh`
3. Explore: `kubectl get all -n booking-engine`

### For DevOps Engineers
1. Review KUBERNETES_DEPLOYMENT.md
2. Analyze k8s manifests
3. Customize for your environment
4. Integrate with CI/CD pipeline

### For Platform Owners
1. Review security in CONFIGURATION.md
2. Plan scaling strategy
3. Set up monitoring infrastructure
4. Configure backup strategy

---

**Package Generated**: 2026-11-20T15:50:02Z
**Target Environment**: Kubernetes Development
**Application**: BookingEngine (.NET 11)
**Database**: PostgreSQL 16

**Status**: ✅ Ready for Deployment
