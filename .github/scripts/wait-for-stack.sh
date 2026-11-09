#!/usr/bin/env bash
# Waits until the docker-compose stack serves the API through nginx (401 = up, auth required).
# (`docker compose up --wait` can't be used: the one-off `migrator` service exits by design.)
set -u
for i in $(seq 1 60); do
  code=$(curl -s -o /dev/null -w '%{http_code}' http://localhost/api/products || true)
  if [ "$code" = "401" ]; then echo "stack is up after $((i * 5))s"; exit 0; fi
  sleep 5
done
echo "stack did not come up (last status: $code)"; docker compose ps -a; docker compose logs --tail=80 migrator api
exit 1
