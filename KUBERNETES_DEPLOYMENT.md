# Kubernetes Deployment Guide for BookingEngine

## Overview

This guide covers deploying the BookingEngine application to a Kubernetes cluster in a Development environment.

## Prerequisites

- Kubernetes cluster (1.24+) with kubectl configured
- Docker registry (local or remote)
- kubectl CLI installed and configured
- At least 2 worker nodes with 1Gi memory available

## Files Generated

- **Dockerfile**: Multi-stage Docker image for BookingEngine API
- **k8s-deployment.yaml**: Core Kubernetes resources (Namespace, ConfigMap, Secrets, StatefulSet for PostgreSQL, Deployment)
- **k8s-ingress-hpa.yaml**: Ingress, Pod Disruption Budget, and Horizontal Pod Autoscaler
- **deploy.sh**: Automated deployment script
- **undeploy.sh**: Cleanup script

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   Kubernetes Cluster                     │
├─────────────────────────────────────────────────────────┤
│                                                           │
│  ┌────────────────────────────────────────────────────┐  │
│  │  Ingress (booking-engine.local)                   │  │
│  └──────────────────┬─────────────────────────────────┘  │
│                     │                                     │
│  ┌──────────────────▼─────────────────────────────────┐  │
│  │  Service: booking-engine-service (ClusterIP)      │  │
│  └──────────────────┬─────────────────────────────────┘  │
│                     │                                     │
│  ┌──────────────────▼─────────────────────────────────┐  │
│  │  Deployment: booking-engine (2 replicas, HPA)      │  │
│  │  ┌──────────────────────────────────────────────┐  │  │
│  │  │  Pod 1: booking-engine container            │  │  │
│  │  │  - Liveness probe: /health (30s interval)   │  │  │
│  │  │  - Readiness probe: /health (5s interval)   │  │  │
│  │  │  - CPU: 250m requested, 500m limit          │  │  │
│  │  │  - Memory: 512Mi requested, 1Gi limit       │  │  │
│  │  └──────────────────────────────────────────────┘  │  │
│  │  ┌──────────────────────────────────────────────┐  │  │
│  │  │  Pod 2: booking-engine container            │  │  │
│  │  │  (Additional replicas via HPA)              │  │  │
│  │  └──────────────────────────────────────────────┘  │  │
│  └───────────────────────┬────────────────────────────┘  │
│                          │                                │
│  ┌──────────────────────▼──────────────────────────────┐ │
│  │  Service: postgres-service (ClusterIP)             │ │
│  └──────────────────────┬──────────────────────────────┘ │
│                         │                                 │
│  ┌──────────────────────▼──────────────────────────────┐ │
│  │  StatefulSet: postgres (1 replica)                 │ │
│  │  ┌──────────────────────────────────────────────┐  │ │
│  │  │  Pod: postgres-0 (PostgreSQL 16)            │  │ │
│  │  │  - Volume: /var/lib/postgresql/data         │  │ │
│  │  │  - Port: 5432                               │  │ │
│  │  └──────────────────────────────────────────────┘  │ │
│  └──────────────────────────────────────────────────────┘ │
│                                                           │
└─────────────────────────────────────────────────────────┘
```

## Deployment Steps

### Quick Deploy (Automated)

```bash
# Make script executable
chmod +x deploy.sh

# Deploy with default settings (uses localhost:5000 registry)
./deploy.sh

# Deploy with custom registry
DOCKER_REGISTRY=myregistry.azurecr.io ./deploy.sh v1.0.0
```

### Manual Deployment Steps

#### 1. Build Docker Image

```bash
docker build -t booking-engine:latest .
```

#### 2. Push to Registry

```bash
# For local registry (Kubernetes Docker Desktop)
docker tag booking-engine:latest localhost:5000/booking-engine:latest
docker push localhost:5000/booking-engine:latest

# For remote registry
docker tag booking-engine:latest myregistry.azurecr.io/booking-engine:latest
docker push myregistry.azurecr.io/booking-engine:latest
```

#### 3. Update Image Reference

If using a custom registry, update the image in `k8s-deployment.yaml`:

```yaml
containers:
- name: booking-engine
  image: myregistry.azurecr.io/booking-engine:latest  # Update this line
```

#### 4. Deploy to Kubernetes

```bash
# Apply deployment
kubectl apply -f k8s-deployment.yaml

# Apply ingress and autoscaling
kubectl apply -f k8s-ingress-hpa.yaml

# Wait for rollout
kubectl rollout status deployment/booking-engine -n booking-engine --timeout=5m
```

## Configuration

### Environment Variables

**ConfigMap** (`booking-engine-config`):
- `ASPNETCORE_ENVIRONMENT`: Development
- `ASPNETCORE_URLS`: http://+:8080

**Secret** (`booking-engine-db-secret`):
- `connection-string`: PostgreSQL connection string
- `postgres-password`: Database password
- `postgres-user`: Database username
- `postgres-db`: Database name

To update secrets:

```bash
kubectl create secret generic booking-engine-db-secret \
  --from-literal=connection-string="Server=postgres-service;..." \
  --from-literal=postgres-password="new-password" \
  --namespace=booking-engine \
  --dry-run=client -o yaml | kubectl apply -f -

# Restart pods to pick up new secrets
kubectl rollout restart deployment/booking-engine -n booking-engine
```

### Resource Limits

Current limits (adjustable in `k8s-deployment.yaml`):
- **CPU Request**: 250m (25% of 1 CPU core)
- **CPU Limit**: 500m (50% of 1 CPU core)
- **Memory Request**: 512Mi
- **Memory Limit**: 1Gi

For production, increase these based on load testing.

### Database Configuration

PostgreSQL runs as a StatefulSet with:
- **Image**: postgres:16-alpine
- **Storage**: emptyDir (ephemeral, lost on pod restart)
- **Replicas**: 1 (high availability not configured)

For persistent storage, replace `emptyDir` with PersistentVolumeClaim:

```yaml
volumes:
- name: postgres-data
  persistentVolumeClaim:
    claimName: postgres-pvc
```

## Health Checks

The deployment includes both liveness and readiness probes:

```
Liveness Probe:
- Endpoint: /health
- Initial delay: 30s
- Period: 10s
- Timeout: 5s
- Failure threshold: 3

Readiness Probe:
- Endpoint: /health
- Initial delay: 10s
- Period: 5s
- Timeout: 3s
- Failure threshold: 2
```

If your application doesn't have a `/health` endpoint, update the probes or implement one.

## Accessing the Application

### Via kubectl port-forward

```bash
kubectl port-forward -n booking-engine svc/booking-engine-service 8080:80
```
Then access: `http://localhost:8080`

### Via Ingress

If Ingress Controller is installed:
```bash
# Add to /etc/hosts
127.0.0.1 booking-engine.local

# Access
http://booking-engine.local
```

### Direct Service Access

```bash
kubectl get service -n booking-engine
kubectl describe svc booking-engine-service -n booking-engine
```

## Monitoring & Troubleshooting

### View Deployment Status

```bash
# Overall status
kubectl get all -n booking-engine

# Detailed deployment info
kubectl describe deployment booking-engine -n booking-engine

# Check replica status
kubectl get rs -n booking-engine

# Check pod status
kubectl get pods -n booking-engine -o wide
```

### View Application Logs

```bash
# Latest logs
kubectl logs -n booking-engine deployment/booking-engine

# Follow logs in real-time
kubectl logs -n booking-engine deployment/booking-engine -f

# Logs from specific pod
kubectl logs -n booking-engine booking-engine-<pod-hash>

# Previous pod logs (if crashed)
kubectl logs -n booking-engine <pod-name> --previous
```

### Check Database Connectivity

```bash
# Connect to PostgreSQL pod
kubectl exec -it -n booking-engine postgres-0 -- psql -U booking_user -d booking_engine

# Run queries
\dt                  # List tables
SELECT version();    # Check PostgreSQL version
```

### Describe Pod Events

```bash
kubectl describe pod -n booking-engine <pod-name>
```

This shows recent events and can indicate why a pod failed to start.

### Check Resource Usage

```bash
# CPU and memory usage
kubectl top nodes
kubectl top pods -n booking-engine

# More detailed metrics (if Metrics Server installed)
kubectl get --raw /apis/metrics.k8s.io/v1beta1/namespaces/booking-engine/pods
```

## Scaling

### Manual Scaling

```bash
# Scale to 3 replicas
kubectl scale deployment/booking-engine -n booking-engine --replicas=3

# Check status
kubectl rollout status deployment/booking-engine -n booking-engine
```

### Automatic Scaling (Horizontal Pod Autoscaler)

HPA is configured to:
- **Minimum replicas**: 2
- **Maximum replicas**: 5
- **CPU target**: 70% average utilization
- **Memory target**: 80% average utilization

Monitor HPA status:

```bash
kubectl get hpa -n booking-engine
kubectl describe hpa booking-engine-hpa -n booking-engine
```

## Updating the Application

### Rolling Update

```bash
# Build and push new image
docker build -t booking-engine:v1.1.0 .
docker tag booking-engine:v1.1.0 localhost:5000/booking-engine:v1.1.0
docker push localhost:5000/booking-engine:v1.1.0

# Update deployment
kubectl set image deployment/booking-engine \
  booking-engine=localhost:5000/booking-engine:v1.1.0 \
  -n booking-engine

# Monitor rollout
kubectl rollout status deployment/booking-engine -n booking-engine --timeout=5m

# Rollback if needed
kubectl rollout undo deployment/booking-engine -n booking-engine
```

## Cleanup

### Remove Deployment Only

```bash
./undeploy.sh
```

### Delete Entire Namespace (Including All Data)

```bash
kubectl delete namespace booking-engine
```

## Security Considerations

The deployment includes:
- ✅ Non-root container execution (UID 1000)
- ✅ Read-only root filesystem with /tmp emptyDir
- ✅ Dropped Linux capabilities (CAP_ALL dropped)
- ✅ No privilege escalation allowed
- ✅ RBAC with minimal permissions (ServiceAccount)
- ✅ Secrets for sensitive data
- ✅ Network policies (not configured by default)

For production, additionally consider:
- Network policies to restrict traffic
- Pod Security Policies / Pod Security Standards
- TLS termination at Ingress
- Certificate management (cert-manager)
- Secret encryption at rest
- Regular security scanning of images

## Troubleshooting Common Issues

### Pod stuck in CrashLoopBackOff

```bash
# Check logs
kubectl logs -n booking-engine <pod-name> --previous

# Likely causes:
# - Application failed to start
# - Database connection failed
# - Missing environment variables
```

### Liveness/Readiness Probe Failing

```bash
# Test health endpoint directly
kubectl exec -it -n booking-engine <pod-name> -- curl http://localhost:8080/health

# May need to implement /health endpoint in application
```

### Database Connection Refused

```bash
# Verify postgres pod is running
kubectl get pods -n booking-engine -l app=postgres

# Test connectivity from app pod
kubectl exec -it -n booking-engine <app-pod-name> -- \
  psql -h postgres-service -U booking_user -d booking_engine -c "SELECT 1;"
```

### Insufficient Resources

```bash
# Check node capacity
kubectl describe nodes

# Check if pods are pending due to resource constraints
kubectl describe pod -n booking-engine <pod-name>

# Either add more nodes or reduce resource requests/limits
```

## Next Steps

1. Implement `/health` endpoint in the application if not present
2. Configure persistent storage for PostgreSQL
3. Set up monitoring with Prometheus/Grafana
4. Configure logging aggregation (ELK, Loki, etc.)
5. Set up backup strategy for PostgreSQL
6. Configure SSL/TLS certificates for Ingress
7. Implement network policies for security
8. Set up CI/CD pipeline for automated deployments

## Support

For issues or questions:
1. Check application logs: `kubectl logs -n booking-engine deployment/booking-engine -f`
2. Check pod events: `kubectl describe pod -n booking-engine <pod-name>`
3. Review Kubernetes documentation: https://kubernetes.io/docs/
4. Check deployment YAML for syntax errors: `kubectl apply -f k8s-deployment.yaml --dry-run=client`

---

Generated deployment configuration for BookingEngine Kubernetes deployment.
