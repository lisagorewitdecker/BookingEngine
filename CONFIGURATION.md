# BookingEngine Environment & Configuration Guide

## Application Configuration

### Environment Variables

#### ASPNETCORE Configuration
```
ASPNETCORE_ENVIRONMENT=Development
ASPNETCORE_URLS=http://+:8080
ASPNETCORE_LOGGING__LOGLEVEL__DEFAULT=Information
ASPNETCORE_LOGGING__LOGLEVEL__MICROSOFT=Warning
```

#### Database Connection
```
ConnectionStrings__DefaultConnection=Server=postgres-service;Port=5432;Database=booking_engine;User Id=booking_user;Password=booking_password;
```

#### Kubernetes-Specific
```
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
```

## Secret Management

### Database Credentials

**Current Defaults** (in `k8s-deployment.yaml`):
```yaml
postgres-user: "booking_user"
postgres-password: "booking_password"
postgres-db: "booking_engine"
connection-string: "Server=postgres-service;Port=5432;Database=booking_engine;User Id=booking_user;Password=booking_password;"
```

### Updating Secrets

```bash
# 1. Create new secret
kubectl create secret generic booking-engine-db-secret \
  --from-literal=postgres-user="new_user" \
  --from-literal=postgres-password="new_password" \
  --from-literal=postgres-db="booking_engine" \
  --from-literal=connection-string="Server=postgres-service;Port=5432;Database=booking_engine;User Id=new_user;Password=new_password;" \
  --namespace=booking-engine \
  --dry-run=client \
  -o yaml | kubectl apply -f -

# 2. Restart pods to pick up new secrets
kubectl rollout restart deployment/booking-engine -n booking-engine
kubectl rollout restart statefulset/postgres -n booking-engine

# 3. Verify
kubectl get secret booking-engine-db-secret -n booking-engine -o yaml
```

### Best Practices for Secrets

- ✅ Use strong passwords (min 16 characters, mixed case, numbers, symbols)
- ✅ Rotate secrets regularly
- ✅ Use external secret management (HashiCorp Vault, Azure Key Vault, etc.)
- ✅ Enable encryption at rest for etcd
- ✅ Restrict RBAC access to secrets
- ✅ Audit secret access

**Example Strong Password**:
```
P@ssw0rd_2026_K8s_B00k1ng!Engine
```

## Resource Configuration

### CPU & Memory

**Current Settings**:
```yaml
requests:
  memory: "512Mi"
  cpu: "250m"
limits:
  memory: "1Gi"
  cpu: "500m"
```

**What It Means**:
- Requested CPU: 250 millicores (1/4 of a CPU core)
- Limited CPU: 500 millicores (1/2 of a CPU core)
- Requested Memory: 512 MiB
- Limited Memory: 1 GiB (1024 MiB)

### Recommended Configurations by Environment

#### Development (Current)
```yaml
requests:
  cpu: "250m"
  memory: "512Mi"
limits:
  cpu: "500m"
  memory: "1Gi"
```

#### Staging
```yaml
requests:
  cpu: "500m"
  memory: "1Gi"
limits:
  cpu: "1000m"
  memory: "2Gi"
```

#### Production
```yaml
requests:
  cpu: "1000m"      # 1 full CPU core
  memory: "2Gi"
limits:
  cpu: "2000m"      # 2 CPU cores
  memory: "4Gi"
```

### Updating Resources

```bash
# Edit deployment
kubectl edit deployment/booking-engine -n booking-engine

# Or patch directly
kubectl patch deployment booking-engine -n booking-engine --type='json' -p='[
  {"op": "replace", "path": "/spec/template/spec/containers/0/resources/requests/cpu", "value":"500m"},
  {"op": "replace", "path": "/spec/template/spec/containers/0/resources/requests/memory", "value":"1Gi"},
  {"op": "replace", "path": "/spec/template/spec/containers/0/resources/limits/cpu", "value":"1000m"},
  {"op": "replace", "path": "/spec/template/spec/containers/0/resources/limits/memory", "value":"2Gi"}
]'

# Monitor rollout
kubectl rollout status deployment/booking-engine -n booking-engine
```

## Autoscaling Configuration

### HPA Settings

**Current Configuration**:
```yaml
minReplicas: 2
maxReplicas: 5
metrics:
- type: Resource
  resource:
    name: cpu
    target:
      type: Utilization
      averageUtilization: 70
- type: Resource
  resource:
    name: memory
    target:
      type: Utilization
      averageUtilization: 80
```

**Scale-Up Behavior**: Aggressive (immediate if threshold exceeded)
**Scale-Down Behavior**: Conservative (5 minute stabilization)

### Adjusting HPA

```bash
# View current HPA
kubectl get hpa -n booking-engine
kubectl describe hpa booking-engine-hpa -n booking-engine

# Change min/max replicas
kubectl patch hpa booking-engine-hpa -n booking-engine \
  --type merge \
  -p '{"spec":{"minReplicas":3,"maxReplicas":10}}'

# Monitor HPA in real-time
kubectl get hpa -n booking-engine -w
```

## Health Checks Configuration

### Liveness Probe
```yaml
httpGet:
  path: /health
  port: 8080
initialDelaySeconds: 30    # Wait 30s before first check
periodSeconds: 10           # Check every 10s
timeoutSeconds: 5           # Timeout if no response in 5s
failureThreshold: 3         # Restart after 3 failures (30s)
```

**When it fires**: Pod is restarted
**Purpose**: Restart hung processes

### Readiness Probe
```yaml
httpGet:
  path: /health
  port: 8080
initialDelaySeconds: 10     # Wait 10s before first check
periodSeconds: 5            # Check every 5s
timeoutSeconds: 3           # Timeout if no response in 3s
failureThreshold: 2         # Remove from load balancer after 2 failures (10s)
```

**When it fires**: Pod is removed from service load balancer
**Purpose**: Wait for pod to be ready, remove temporarily if unhealthy

### Implementing /health Endpoint

If not implemented, add to your ASP.NET application:

```csharp
// Program.cs or Startup.cs
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithName("Health")
    .WithOpenApi()
    .ExcludeFromDescription();

// Or with more details
app.MapGet("/health/live", async (AppDbContext db) => 
{
    var dbHealthy = await db.Database.CanConnectAsync();
    return dbHealthy 
        ? Results.Ok(new { status = "healthy", database = "connected" })
        : Results.ServiceUnavailable(new { status = "unhealthy", database = "disconnected" });
})
.WithName("HealthLive")
.WithOpenApi();
```

## Network Configuration

### Service
- **Type**: ClusterIP (internal only)
- **Port**: 80 (external)
- **Target Port**: 8080 (pod)

### Ingress
- **Host**: booking-engine.local
- **TLS**: Configured but needs certificate (staging by default)
- **Path**: / (all paths)

### Local Testing

Add to `/etc/hosts`:
```
127.0.0.1 booking-engine.local
```

Then access: `http://booking-engine.local`

## Logging Configuration

### Application Logs
```bash
# View logs
kubectl logs -n booking-engine deployment/booking-engine

# Follow logs in real-time
kubectl logs -n booking-engine deployment/booking-engine -f

# View logs from specific pod
kubectl logs -n booking-engine booking-engine-<hash>

# View logs from previous pod (if crashed)
kubectl logs -n booking-engine <pod-name> --previous

# View logs with timestamps
kubectl logs -n booking-engine deployment/booking-engine --timestamps=true

# View only errors
kubectl logs -n booking-engine deployment/booking-engine | grep ERROR
```

### Log Level Configuration

**Current**: Information level (from ConfigMap)

To change:
```bash
kubectl set env deployment/booking-engine \
  ASPNETCORE_LOGGING__LOGLEVEL__DEFAULT=Debug \
  -n booking-engine
```

**Available Levels**:
- `Critical` - Only critical errors
- `Error` - Errors and above
- `Warning` - Warnings and above (default)
- `Information` - General information (current)
- `Debug` - Detailed debug information
- `Trace` - Most detailed

## Persistence Configuration

### Current Setup
- **Storage Type**: emptyDir
- **Lifespan**: Pod lifecycle (lost on restart)
- **Use Case**: Temporary files only

### For Persistent Storage

Create PVC (from local storage):
```yaml
apiVersion: v1
kind: PersistentVolume
metadata:
  name: postgres-pv
spec:
  capacity:
    storage: 10Gi
  accessModes:
    - ReadWriteOnce
  hostPath:
    path: /data/postgres
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: postgres-pvc
  namespace: booking-engine
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: 10Gi
```

Then update `k8s-deployment.yaml`:
```yaml
volumes:
- name: postgres-data
  persistentVolumeClaim:
    claimName: postgres-pvc
```

## Environment-Specific Configurations

### Development (Current)
- Replicas: 2
- Resource requests: 250m CPU, 512Mi memory
- Logging: Information level
- Database: Local PostgreSQL in cluster
- TLS: Staging certificates
- Secrets: Default values

### Staging
- Replicas: 3 (min), 8 (max)
- Resource requests: 500m CPU, 1Gi memory
- Logging: Information level
- Database: External PostgreSQL instance
- TLS: Staging certificates
- Secrets: Updated credentials

### Production
- Replicas: 3 (min), 10 (max)
- Resource requests: 1000m CPU, 2Gi memory
- Logging: Warning level
- Database: External managed PostgreSQL (RDS, Azure Database, etc.)
- TLS: Production certificates (Let's Encrypt or CA signed)
- Secrets: Vault integration, encrypted at rest
- Monitoring: Prometheus, Grafana
- Backups: Automated daily

## Troubleshooting Configuration Issues

### Pod Not Starting

1. Check resource availability:
```bash
kubectl describe node
```

2. Check resource requests don't exceed limits:
```bash
kubectl top nodes
kubectl top pods -n booking-engine
```

3. Check image exists:
```bash
kubectl describe pod <pod-name> -n booking-engine
```

### Health Check Failing

1. Test endpoint directly:
```bash
kubectl exec -it -n booking-engine <pod-name> -- curl http://localhost:8080/health
```

2. Check pod logs:
```bash
kubectl logs -n booking-engine <pod-name>
```

3. If endpoint not found, implement `/health` endpoint

### Database Connection Failing

1. Test PostgreSQL connectivity:
```bash
kubectl exec -it -n booking-engine <pod-name> -- \
  psql -h postgres-service -U booking_user -d booking_engine -c "SELECT 1;"
```

2. Verify connection string in secret:
```bash
kubectl get secret booking-engine-db-secret -n booking-engine -o yaml
```

3. Check PostgreSQL logs:
```bash
kubectl logs -n booking-engine postgres-0
```

## Configuration Files Location

```
<repository-root>/

├── k8s-deployment.yaml          # Main configuration
├── k8s-ingress-hpa.yaml         # Ingress and autoscaling
├── Dockerfile                   # Container image
├── KUBERNETES_DEPLOYMENT.md     # Deployment guide
├── DEPLOYMENT_CHECKLIST.md      # Verification checklist
└── DEPLOYMENT_SUMMARY.md        # Quick reference
```

## Configuration Best Practices

✅ Use ConfigMaps for non-sensitive configuration
✅ Use Secrets for sensitive data (passwords, tokens)
✅ Set resource requests and limits
✅ Implement health checks (/health endpoint)
✅ Use meaningful environment variable names
✅ Document all configuration options
✅ Version control Kubernetes manifests (except secrets)
✅ Use kustomize or Helm for environment-specific configurations
✅ Regularly review and update configurations
✅ Monitor resource usage and adjust limits accordingly

---

For complete deployment guide, see `KUBERNETES_DEPLOYMENT.md`
