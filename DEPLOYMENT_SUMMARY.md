# BookingEngine Kubernetes Deployment Summary

## ✅ Deployment Package Generated

Your BookingEngine application is ready for Kubernetes deployment to a Development environment.

### Generated Files

1. **Dockerfile** - Multi-stage Docker build
   - Build stage: Compiles the .NET application
   - Publish stage: Creates optimized release binaries
   - Runtime stage: Lightweight aspnet:11.0 runtime image
   - Includes health checks and curl for diagnostics

2. **k8s-deployment.yaml** - Core Kubernetes Resources
   - Namespace: `booking-engine`
   - ConfigMap: Application configuration
   - Secrets: Database credentials (changeable)
   - PostgreSQL StatefulSet: Database with ephemeral storage (`emptyDir`)
   - Deployment: 2 replicas of BookingEngine API with:
     - Resource requests: 250m CPU, 512Mi memory
     - Resource limits: 500m CPU, 1Gi memory
     - Liveness probe: /health endpoint
     - Readiness probe: /health endpoint
     - Security context: Non-root user (1000)
   - Service: ClusterIP for internal access
   - ServiceAccount & RBAC: Minimal permissions

3. **k8s-ingress-hpa.yaml** - Advanced Kubernetes Features
   - Ingress: Route external traffic to booking-engine.local
   - Pod Disruption Budget: Minimum 1 pod always available
   - Horizontal Pod Autoscaler (HPA):
     - Min replicas: 2
     - Max replicas: 5
     - CPU target: 70%
     - Memory target: 80%

4. **deploy.sh** - Automated Deployment Script
   - Builds Docker image
   - Pushes to registry
   - Applies Kubernetes manifests
   - Waits for rollout
   - Shows deployment status

5. **undeploy.sh** - Cleanup Script
   - Scales down deployment
   - Deletes resources gracefully

6. **KUBERNETES_DEPLOYMENT.md** - Complete Documentation
   - Architecture overview
   - Step-by-step deployment guide
   - Configuration options
   - Monitoring and troubleshooting
   - Security considerations

### Quick Start

#### Prerequisites
```bash
# Verify Kubernetes cluster access
kubectl cluster-info
kubectl get nodes

# Docker installed and running
docker --version
```

#### Automated Deployment (Recommended)
```bash
cd /Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine

# Standard deployment to localhost:5000 registry
./deploy.sh

# Or with custom registry and version
DOCKER_REGISTRY=myregistry.azurecr.io ./deploy.sh v1.0.0
```

#### Manual Deployment
```bash
# 1. Build Docker image
docker build -t booking-engine:latest .

# 2. Push to registry
docker tag booking-engine:latest localhost:5000/booking-engine:latest
docker push localhost:5000/booking-engine:latest

# 3. Deploy
kubectl apply -f k8s-deployment.yaml
kubectl apply -f k8s-ingress-hpa.yaml

# 4. Verify
kubectl rollout status deployment/booking-engine -n booking-engine
```

### Verification After Deployment

```bash
# Check all resources
kubectl get all -n booking-engine

# Check if pods are running
kubectl get pods -n booking-engine -o wide

# Check service endpoints
kubectl get svc -n booking-engine

# View application logs
kubectl logs -n booking-engine deployment/booking-engine -f

# Access the application
kubectl port-forward -n booking-engine svc/booking-engine-service 8080:80
# Then visit: http://localhost:8080
```

### Database Access

```bash
# PostgreSQL is automatically set up with:
# Host: postgres-service (accessible within cluster)
# Port: 5432
# Database: booking_engine
# User: booking_user
# Password: booking_password (change in k8s-deployment.yaml)

# Connect directly (from outside cluster):
kubectl port-forward -n booking-engine postgres-0 5432:5432
psql -h localhost -U booking_user -d booking_engine
```

### Configuration

**To change database credentials:**
1. Edit the `booking-engine-db-secret` in `k8s-deployment.yaml`
2. Redeploy: `kubectl apply -f k8s-deployment.yaml`
3. Restart pods: `kubectl rollout restart deployment/booking-engine -n booking-engine`

**To change resource limits:**
1. Edit resources section in `k8s-deployment.yaml` deployment spec
2. Apply: `kubectl apply -f k8s-deployment.yaml`
3. Pods will be recreated with new limits

**To change replicas:**
1. Manual: `kubectl scale deployment/booking-engine -n booking-engine --replicas=4`
2. HPA will adjust between 2-5 based on CPU/memory usage

### Health Checks

Your deployment includes:
- **Liveness probe** (/health): Checks if pod is alive - restarts if fails
- **Readiness probe** (/health): Checks if pod is ready for traffic

⚠️ **Note**: Ensure your application implements a `/health` endpoint, or update the probes in `k8s-deployment.yaml`.

### Security Features

✅ Non-root container execution
✅ Dropped Linux capabilities
✅ Read-only root filesystem with /tmp emptyDir
✅ No privilege escalation
✅ RBAC with minimal permissions
✅ Secrets for sensitive data
✅ Resource limits to prevent resource exhaustion

### Monitoring

```bash
# Watch pod status in real-time
kubectl get pods -n booking-engine -w

# Monitor HPA decisions
kubectl get hpa -n booking-engine -w

# Check resource usage
kubectl top pods -n booking-engine

# View events
kubectl get events -n booking-engine --sort-by='.lastTimestamp'
```

### Troubleshooting

```bash
# If pods crash immediately
kubectl logs -n booking-engine <pod-name> --previous

# If liveness/readiness probes fail
kubectl describe pod -n booking-engine <pod-name>

# If database connection fails
kubectl exec -it -n booking-engine postgres-0 -- psql -U booking_user -d booking_engine -c "SELECT 1;"

# Check deployment events
kubectl describe deployment/booking-engine -n booking-engine
```

### Cleanup

```bash
# Remove deployment (keeps namespace and data)
./undeploy.sh

# Delete entire namespace (removes all data)
kubectl delete namespace booking-engine
```

### Next Steps

1. ✅ Update `/health` endpoint in your application (if not implemented)
2. ⏳ Configure persistent storage for PostgreSQL (currently uses emptyDir)
3. ⏳ Set up monitoring with Prometheus/Grafana
4. ⏳ Configure SSL/TLS certificates
5. ⏳ Implement network policies
6. ⏳ Set up automated backups for database

### Important Notes

- **Image Registry**: Deployment uses `localhost:5000` by default (Kubernetes Docker Desktop)
  - For other registries, update image references in `k8s-deployment.yaml`
  - Docker credentials may need to be configured via ImagePullSecret

- **Database Persistence**: Currently uses `emptyDir` (ephemeral)
  - Data is lost when pod restarts
  - For production, replace with PersistentVolumeClaim

- **Health Endpoint**: Deployment expects `/health` endpoint
  - If not implemented, update probes to match your endpoint

- **Ingress**: Uses NGINX ingress controller and hostname `booking-engine.local`
  - Add to `/etc/hosts` or update DNS
  - For production, use proper domain and SSL

### Support & Documentation

- Full documentation: `KUBERNETES_DEPLOYMENT.md`
- Kubernetes docs: https://kubernetes.io/docs/
- .NET on Kubernetes: https://learn.microsoft.com/en-us/dotnet/architecture/cloud-native/

---

**Deployment prepared on**: 2026-11-20T15:50:02Z
**Target Environment**: Kubernetes Development
**Application**: BookingEngine (.NET 11)
**Database**: PostgreSQL 16
**Replicas**: 2 (autoscales to 5 max)
