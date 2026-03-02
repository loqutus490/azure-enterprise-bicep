#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${1:-http://localhost:5176}"

echo "Running smoke test against: ${BASE_URL}"

ping_body="$(mktemp)"
ask_body="$(mktemp)"
cleanup() {
  rm -f "${ping_body}" "${ask_body}"
}
trap cleanup EXIT

ping_status="$(curl -sS -o "${ping_body}" -w "%{http_code}" "${BASE_URL}/ping" || true)"
if [[ "${ping_status}" != "200" ]]; then
  echo "FAIL: GET /ping returned ${ping_status}"
  cat "${ping_body}"
  exit 1
fi
echo "PASS: GET /ping returned 200"

ask_status="$(curl -sS -o "${ask_body}" -w "%{http_code}" \
  -H "Content-Type: application/json" \
  -d '{"question":""}' \
  "${BASE_URL}/ask" || true)"

case "${ask_status}" in
  400)
    echo "PASS: POST /ask returned 400 (auth bypass enabled in Development)."
    ;;
  401|403)
    echo "PASS: POST /ask returned ${ask_status} (auth enforcement active)."
    ;;
  *)
    echo "FAIL: POST /ask returned unexpected status ${ask_status}"
    cat "${ask_body}"
    exit 1
    ;;
esac
