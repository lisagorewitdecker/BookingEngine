# BookingEngine Kubernetes Deployment Checklist

## Pre-Deployment Checklist

- [ ] Kubernetes cluster is up and running
  ```bash
  kubectl cluster-info
  ```

- [ ] kubectl is configured and authenticated
  ```bash
  kubectl auth can-i create deployments --all-namespaces
  ```

- [ ] Docker is installed and running
  ```bash
  docker --version
  docker ps
  ```

- [ ] At least 2 nodes available with sufficient resources
  ```bash
  kubectl get nodes
  kubectl describe nodes | grep -E "Name:|cpu|memory"
  ```

- [ ] Docker registry is accessible (or Docker Desktop for local testing)
  ```bash
  docker info | grep Registry
  ```

## Deployment Steps

### Step 1: Build Docker Image
- [ ] Navigate to BookingEngine root directory
- [ ] Run: `docker build -t booking-engine:latest .`
- [ ] Verify build succeeded: `docker images | grep booking-engine`
- [ ] Test image locally (optional):
  ```bash
  docker run -p 8080:8080 -e ASPNETCORE_ENVIRONMENT=Development booking-engine:latest
  ```

### Step 2: Push to Registry
- [ ] Tag image: `docker tag booking-engine:latest localhost:5000/booking-engine:latest`
- [ ] Push: `docker push localhost:5000/booking-engine:latest`
- [ ] Verify: `curl localhost:5000/v2/booking-engine/tags/list`

### Step 3: Prepare Kubernetes Manifests
- [ ] Review `k8s-deployment.yaml`
  - [ ] Database credentials are secure
  - [ ] Image reference matches your registry
  - [ ] Resource limits are appropriate for your cluster
  - [ ] Environment variables are correct

- [ ] Review `k8s-ingress-hpa.yaml`
  - [ ] Ingress hostname is correct
  - [ ] HPA min/max replicas are appropriate
  - [ ] Scale targets (CPU/memory) are reasonable

### Step 4: Deploy to Kubernetes
- [ ] Apply deployment: `kubectl apply -f k8s-deployment.yaml`
- [ ] Apply ingress/HPA: `kubectl apply -f k8s-ingress-hpa.yaml`
- [ ] Verify namespace created: `kubectl get namespace booking-engine`

### Step 5: Verify Deployment
- [ ] Check deployment status:
  ```bash
  kubectl rollout status deployment/booking-engine -n booking-engine --timeout=5m
  ```

- [ ] Verify pods are running:
  ```bash
  kubectl get pods -n booking-engine
  ```

- [ ] Check all resources:
  ```bash
  kubectl get all -n booking-engine
  ```

### Step 6: Test Application
- [ ] Port-forward to service:
  ```bash
  kubectl port-forward -n booking-engine svc/booking-engine-service 8080:80
  ```

- [ ] Test health endpoint:
  ```bash
  curl http://localhost:8080/health
  ```

- [ ] Test API endpoints:
  ```bash
  curl http://localhost:8080/api/availability/slots?resourceId=<guid>&date=2026-11-20
  ```

### Step 7: Verify Database
- [ ] Connect to PostgreSQL:
  ```bash
  kubectl port-forward -n booking-engine postgres-0 5432:5432
  psql -h localhost -U booking_user -d booking_engine
  ```

- [ ] Check if migrations ran:
  ```sql
  \dt  -- List tables
  SELECT * FROM __EFMigrationsHistory;  -- Check migrations
  ```

## Post-Deployment Verification

### Logs
- [ ] Check application logs:
  ```bash
  kubectl logs -n booking-engine deployment/booking-engine -f
  ```

- [ ] Check database logs:
  ```bash
  kubectl logs -n booking-engine postgres-0
  ```

### Health Checks
- [ ] Liveness probe passing:
  ```bash
  kubectl get pods -n booking-engine -o jsonpath='{.items[*].spec.containers[*].livenessProbe}' | jq .
  ```

- [ ] Readiness probe passing:
  ```bash
  kubectl get pods -n booking-engine -o jsonpath='{.items[*].status.containerStatuses[*].readinessProbe}' | jq .
  ```

### Performance
- [ ] Check resource usage:
  ```bash
  kubectl top pods -n booking-engine
  ```

- [ ] Monitor HPA status:
  ```bash
  kubectl get hpa -n booking-engine -w
  ```

## Common Issues & Solutions

### Pod in CrashLoopBackOff
```bash
# Check previous logs
kubectl logs -n booking-engine <pod-name> --previous

# Common causes:
# - Application failed to start
# - Database connection failed
# - Missing environment variables
```

### Liveness/Readiness Probe Failing
```bash
# Check if health endpoint exists
kubectl exec -it -n booking-engine <pod-name> -- curl http://localhost:8080/health

# If endpoint not found, implement /health or update probes
```

### Database Connection Error
```bash
# Test connectivity
kubectl exec -it -n booking-engine <app-pod-name> -- \
  psql -h postgres-service -U booking_user -d booking_engine -c "SELECT 1;"

# If fails, check postgres pod is running and database initialized
```

### Image Pull Failed
```bash
# Verify image is pushed to registry
docker images | grep booking-engine

# Check Docker registry is running
docker ps | grep registry

# Verify image reference in k8s-deployment.yaml matches registry
```

### Insufficient Resources
```bash
# Check cluster capacity
kubectl describe nodes

# If not enough resources:
# 1. Add more nodes to cluster
# 2. Reduce resource requests in k8s-deployment.yaml
# 3. Scale down other deployments
```

## Maintenance Tasks

### Updating Application
```bash
# 1. Build new image
docker build -t booking-engine:v1.1.0 .

# 2. Push to registry
docker tag booking-engine:v1.1.0 localhost:5000/booking-engine:v1.1.0
docker push localhost:5000/booking-engine:v1.1.0

# 3. Update deployment
kubectl set image deployment/booking-engine \
  booking-engine=localhost:5000/booking-engine:v1.1.0 \
  -n booking-engine

# 4. Monitor rollout
kubectl rollout status deployment/booking-engine -n booking-engine --timeout=5m
```

### Rolling Back
```bash
# See rollout history
kubectl rollout history deployment/booking-engine -n booking-engine

# Rollback to previous version
kubectl rollout undo deployment/booking-engine -n booking-engine

# Rollback to specific revision
kubectl rollout undo deployment/booking-engine -n booking-engine --to-revision=2
```

### Scaling
```bash
# Manual scale
kubectl scale deployment/booking-engine -n booking-engine --replicas=4

# Check HPA is not overriding manual scale
kubectl get hpa -n booking-engine
```

### Database Backup
```bash
# Backup PostgreSQL
kubectl exec -n booking-engine postgres-0 -- \
  pg_dump -U booking_user booking_engine > backup.sql

# Restore from backup
kubectl exec -i -n booking-engine postgres-0 -- \
  psql -U booking_user booking_engine < backup.sql
```

## Cleanup

### Remove Deployment Only
```bash
./undeploy.sh
```

### Delete Entire Namespace
```bash
kubectl delete namespace booking-engine
```

### Remove Docker Image
```bash
docker rmi booking-engine:latest
docker rmi localhost:5000/booking-engine:latest
```

## Success Criteria

✅ Deployment successful when:
- [ ] All pods are in `Running` state
- [ ] Ready count matches replica count (e.g., 2/2)
- [ ] Health checks are passing
- [ ] Application logs show no errors
- [ ] API endpoints are responding
- [ ] Database connectivity is working
- [ ] HPA is monitoring metrics

## Support Resources

- **Kubernetes Documentation**: https://kubernetes.io/docs/
- **.NET on Kubernetes**: https://learn.microsoft.com/en-us/dotnet/architecture/cloud-native/
- **Docker Documentation**: https://docs.docker.com/
- **PostgreSQL on Kubernetes**: https://www.postgresql.org/docs/

## Deployment Date & Time

- **Generated**: 2026-11-20T15:50:02Z
- **Environment**: Kubernetes Development
- **Application**: BookingEngine (.NET 11)
- **Database**: PostgreSQL 16
