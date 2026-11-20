#!/bin/bash
set -e

echo "🧹 Cleaning up BookingEngine Kubernetes deployment..."

echo "⏳ Scaling down deployment..."
kubectl scale deployment/booking-engine -n booking-engine --replicas=0 || true

echo "🗑️  Deleting resources..."
kubectl delete -f k8s-ingress-hpa.yaml --namespace booking-engine || true
kubectl delete deployment/booking-engine service/booking-engine-service service/postgres-service statefulset/postgres configmap/booking-engine-config secret/booking-engine-db-secret serviceaccount/booking-engine role/booking-engine rolebinding/booking-engine -n booking-engine --ignore-not-found

echo "✅ Cleanup complete!"
echo ""
echo "To delete the entire namespace (including data):"
echo "  kubectl delete namespace booking-engine"
