#!/bin/bash
set -e

echo "🧹 Cleaning up BookingEngine Kubernetes deployment..."

echo "⏳ Scaling down deployment..."
kubectl scale deployment/booking-engine -n booking-engine --replicas=0

echo "🗑️  Deleting resources..."
kubectl delete -f k8s-ingress-hpa.yaml --namespace booking-engine || true
kubectl delete -f k8s-deployment.yaml --namespace booking-engine || true

echo "✅ Cleanup complete!"
echo ""
echo "To delete the entire namespace (including data):"
echo "  kubectl delete namespace booking-engine"
