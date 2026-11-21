#!/bin/bash
set -e

REGISTRY="${DOCKER_REGISTRY:-localhost:5000}"
IMAGE_NAME="${REGISTRY}/booking-engine"
IMAGE_TAG="${1:-latest}"
KUBECONFIG="${KUBECONFIG:-$HOME/.kube/config}"

echo "🔨 Building BookingEngine Docker image..."
docker build -t "${IMAGE_NAME}:${IMAGE_TAG}" .
docker build -t "${IMAGE_NAME}:latest" .

echo "📦 Pushing image to registry..."
docker push "${IMAGE_NAME}:${IMAGE_TAG}"
docker push "${IMAGE_NAME}:latest"

echo "🚀 Deploying to Kubernetes..."

# Create namespace if it doesn't exist
kubectl create namespace booking-engine --dry-run=client -o yaml | kubectl apply -f -

echo "📋 Applying deployment configuration..."
kubectl apply -f k8s-deployment.yaml

echo "🔗 Applying ingress and autoscaling configuration..."
kubectl apply -f k8s-ingress-hpa.yaml

echo "⏳ Waiting for deployment to be ready..."
kubectl rollout status deployment/booking-engine -n booking-engine --timeout=5m

echo "✅ Deployment complete!"
echo ""
echo "📊 Deployment status:"
kubectl get all -n booking-engine
echo ""
echo "🌐 Service endpoints:"
kubectl get service -n booking-engine
echo ""
echo "💾 Database status:"
kubectl get statefulset -n booking-engine
echo ""
echo "✨ BookingEngine is now deployed to Kubernetes!"
echo ""
echo "To access the application:"
echo "  kubectl port-forward -n booking-engine svc/booking-engine-service 8080:80"
echo "  Then visit: http://localhost:8080"
echo ""
echo "To view logs:"
echo "  kubectl logs -n booking-engine deployment/booking-engine -f"
echo ""
echo "To check pod status:"
echo "  kubectl get pods -n booking-engine -w"
