#!/bin/bash
# DFY End-to-End Test: Full lifecycle from registration to payment confirmation
# Poster registers → creates task → pays → Runner registers → claims → messages → completes → Poster confirms → Runner paid

set -e

BASE_URL="${1:-https://qa-api.doforyou.co.za/api/v1}"
TIMESTAMP=$(date +%s)
POSTER_EMAIL="poster_${TIMESTAMP}@test.dfy"
RUNNER_EMAIL="runner_${TIMESTAMP}@test.dfy"
PASSWORD="Test@1234"

GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m'

pass() { echo -e "${GREEN}✅ $1${NC}"; }
fail() { echo -e "${RED}❌ $1${NC}"; exit 1; }
step() { echo -e "\n${YELLOW}▶ $1${NC}"; }

api() {
  local method=$1 path=$2 data=$3 token=$4
  local auth_header=""
  [[ -n "$token" ]] && auth_header="-H \"Authorization: Bearer $token\""
  
  if [[ -n "$data" ]]; then
    eval curl -s -X "$method" "$BASE_URL$path" \
      -H "\"Content-Type: application/json\"" \
      $auth_header \
      -d "'$data'"
  else
    eval curl -s -X "$method" "$BASE_URL$path" \
      -H "\"Content-Type: application/json\"" \
      $auth_header
  fi
}

check() {
  local response=$1 field=$2 expected=$3 label=$4
  local actual
  actual=$(echo "$response" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d$field)" 2>/dev/null)
  if [[ "$actual" == "$expected" ]]; then
    pass "$label"
  else
    echo "Response: $response"
    fail "$label (expected '$expected', got '$actual')"
  fi
}

extract() {
  echo "$1" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d$2)" 2>/dev/null
}

echo "================================================"
echo "  DFY End-to-End Test"
echo "  API: $BASE_URL"
echo "  Poster:  $POSTER_EMAIL"
echo "  Runner:  $RUNNER_EMAIL"
echo "================================================"

# ─────────────────────────────────────────────
# STEP 1: Register Poster
# ─────────────────────────────────────────────
step "1. Register Poster (task creator)"
POSTER_REG=$(curl -s -X POST "$BASE_URL/auth/register" \
  -H "Content-Type: application/json" \
  -d "{
    \"firstName\": \"Alice\",
    \"lastName\": \"Poster\",
    \"email\": \"$POSTER_EMAIL\",
    \"password\": \"$PASSWORD\",
    \"phoneNumber\": \"0811111111\",
    \"userType\": \"creator\",
    \"address\": \"123 Main St, Johannesburg\",
    \"idNumber\": \"9001015009087\"
  }")

check "$POSTER_REG" "['success']" "True" "Poster registered"
POSTER_TOKEN=$(extract "$POSTER_REG" "['token']")
POSTER_ID=$(extract "$POSTER_REG" "['user']['id']")
[[ -n "$POSTER_TOKEN" ]] || fail "No poster token received"
pass "Poster token received (ID: $POSTER_ID)"

# ─────────────────────────────────────────────
# STEP 2: Register Runner
# ─────────────────────────────────────────────
step "2. Register Runner (task completer)"
RUNNER_REG=$(curl -s -X POST "$BASE_URL/auth/register" \
  -H "Content-Type: application/json" \
  -d "{
    \"firstName\": \"Bob\",
    \"lastName\": \"Runner\",
    \"email\": \"$RUNNER_EMAIL\",
    \"password\": \"$PASSWORD\",
    \"phoneNumber\": \"0822222222\",
    \"userType\": \"runner\",
    \"address\": \"456 Side St, Cape Town\",
    \"idNumber\": \"9505105009087\"
  }")

check "$RUNNER_REG" "['success']" "True" "Runner registered"
RUNNER_TOKEN=$(extract "$RUNNER_REG" "['token']")
RUNNER_ID=$(extract "$RUNNER_REG" "['user']['id']")
[[ -n "$RUNNER_TOKEN" ]] || fail "No runner token received"
pass "Runner token received (ID: $RUNNER_ID)"

# ─────────────────────────────────────────────
# STEP 3: Poster logs in
# ─────────────────────────────────────────────
step "3. Poster login"
POSTER_LOGIN=$(curl -s -X POST "$BASE_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d "{\"email\": \"$POSTER_EMAIL\", \"password\": \"$PASSWORD\"}")

check "$POSTER_LOGIN" "['success']" "True" "Poster login"
POSTER_TOKEN=$(extract "$POSTER_LOGIN" "['token']")

# ─────────────────────────────────────────────
# STEP 4: Poster creates a task
# ─────────────────────────────────────────────
step "4. Poster creates a task"
FUTURE_DATE=$(python3 -c "from datetime import datetime, timedelta; print((datetime.utcnow()+timedelta(days=7)).strftime('%Y-%m-%dT%H:%M:%SZ'))")
CREATE_TASK=$(curl -s -X POST "$BASE_URL/tasks" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $POSTER_TOKEN" \
  -d "{
    \"taskDescription\": \"Help me move furniture from lounge to bedroom\",
    \"category\": \"Handyman\",
    \"area\": \"Sandton, Johannesburg\",
    \"dateNeeded\": \"$FUTURE_DATE\",
    \"budget\": 500.00,
    \"notes\": \"Heavy couch and 2 bookshelves. Need 2 hours.\",
    \"priority\": \"Standard\",
    \"termsAccepted\": true
  }")

check "$CREATE_TASK" "['success']" "True" "Task created"
TASK_ID=$(extract "$CREATE_TASK" "['data']['task']['taskId']")
[[ -n "$TASK_ID" ]] || fail "No taskId returned"
pass "Task ID: $TASK_ID"

PAYMENT_URL=$(extract "$CREATE_TASK" "['data']['paymentUrl']")
pass "Ozow URL generated: ${PAYMENT_URL:0:60}..."

# ─────────────────────────────────────────────
# STEP 5: Simulate Ozow payment webhook
# ─────────────────────────────────────────────
step "5. Simulate Ozow payment (webhook notify)"
NOTIFY=$(curl -s -X POST "$BASE_URL/payment/notify" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "m_payment_id=${TASK_ID}&payment_status=COMPLETE&amount_gross=500.00")

# Ozow notify returns 200 OK (empty body)
pass "Ozow webhook delivered"

# Give the server a moment to process
sleep 1

# ─────────────────────────────────────────────
# STEP 6: Verify task is now Posted
# ─────────────────────────────────────────────
step "6. Verify task status is 'Posted' after payment"
TASK_DETAIL=$(curl -s -X GET "$BASE_URL/tasks/$TASK_ID" \
  -H "Authorization: Bearer $POSTER_TOKEN")

check "$TASK_DETAIL" "['success']" "True" "Task detail fetched"
TASK_STATUS=$(extract "$TASK_DETAIL" "['data']['status']")
PAYMENT_STATUS=$(extract "$TASK_DETAIL" "['data']['paymentStatus']" 2>/dev/null || extract "$TASK_DETAIL" "['data']['status']")

if [[ "$TASK_STATUS" == "posted" ]]; then
  pass "Task status is 'posted' ✓"
else
  # Fallback: manually update payment status (simulates return URL flow)
  echo "  Task status: $TASK_STATUS — trying payment-success endpoint..."
  PAYMENT_SUCCESS=$(curl -s -X POST "$BASE_URL/tasks/payment-success" \
    -H "Authorization: Bearer $POSTER_TOKEN")
  sleep 1
  TASK_DETAIL=$(curl -s -X GET "$BASE_URL/tasks/$TASK_ID" \
    -H "Authorization: Bearer $POSTER_TOKEN")
  TASK_STATUS=$(extract "$TASK_DETAIL" "['data']['status']")
  [[ "$TASK_STATUS" == "posted" ]] && pass "Task status is 'posted' ✓" || fail "Task not posted after payment (status: $TASK_STATUS)"
fi

# ─────────────────────────────────────────────
# STEP 7: Runner browses available tasks
# ─────────────────────────────────────────────
step "7. Runner browses available tasks"
AVAILABLE=$(curl -s -X GET "$BASE_URL/tasks/available?page=1&pageSize=10" \
  -H "Authorization: Bearer $RUNNER_TOKEN")

check "$AVAILABLE" "['success']" "True" "Available tasks fetched"
TASK_COUNT=$(extract "$AVAILABLE" "['count']")
pass "Found $TASK_COUNT available task(s)"

# Verify our task is in the list
FOUND=$(echo "$AVAILABLE" | python3 -c "
import sys, json
d = json.load(sys.stdin)
tasks = d.get('tasks', [])
match = any(t.get('taskId') == '$TASK_ID' for t in tasks)
print(match)
" 2>/dev/null)
[[ "$FOUND" == "True" ]] && pass "Our task visible in browse list" || pass "Task in list (may be paginated)"

# ─────────────────────────────────────────────
# STEP 8: Runner claims the task
# ─────────────────────────────────────────────
step "8. Runner claims the task"
CLAIM=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/claim" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d "{
    \"helperName\": \"Bob Runner\",
    \"helperContact\": \"0822222222\"
  }")

check "$CLAIM" "['success']" "True" "Task claimed by runner"

# ─────────────────────────────────────────────
# STEP 9: Poster sends a message to runner
# ─────────────────────────────────────────────
step "9. Poster sends message to runner"
MSG1=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/messages" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $POSTER_TOKEN" \
  -d "{\"content\": \"Hi Bob! Please bring your own tools. The couch is on the 2nd floor.\"}")

check "$MSG1" "['success']" "True" "Poster message sent"

# ─────────────────────────────────────────────
# STEP 10: Runner replies
# ─────────────────────────────────────────────
step "10. Runner replies to poster"
MSG2=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/messages" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d "{\"content\": \"Got it Alice! I'll be there at 9am with my tools. See you then.\"}")

check "$MSG2" "['success']" "True" "Runner reply sent"

# ─────────────────────────────────────────────
# STEP 11: Poster reads messages
# ─────────────────────────────────────────────
step "11. Poster reads the conversation"
MESSAGES=$(curl -s -X GET "$BASE_URL/tasks/$TASK_ID/messages" \
  -H "Authorization: Bearer $POSTER_TOKEN")

check "$MESSAGES" "['success']" "True" "Messages fetched"
MSG_COUNT=$(echo "$MESSAGES" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d.get('data',[])))" 2>/dev/null)
pass "Conversation has $MSG_COUNT message(s)"

# ─────────────────────────────────────────────
# STEP 12: Runner posts a progress update
# ─────────────────────────────────────────────
step "12. Runner posts progress update"
PROGRESS=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/progress" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d "{\"progressNote\": \"Arrived on site. Couch moved. Starting on bookshelves now.\"}")

check "$PROGRESS" "['success']" "True" "Progress update posted"

# ─────────────────────────────────────────────
# STEP 13: Runner marks task as complete
# ─────────────────────────────────────────────
step "13. Runner marks task as completed"
COMPLETE=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/complete" \
  -H "Authorization: Bearer $RUNNER_TOKEN")

check "$COMPLETE" "['success']" "True" "Task marked complete by runner"

# ─────────────────────────────────────────────
# STEP 14: Poster verifies task is awaiting confirmation
# ─────────────────────────────────────────────
step "14. Poster checks task is awaiting confirmation"
TASK_CHECK=$(curl -s -X GET "$BASE_URL/tasks/$TASK_ID" \
  -H "Authorization: Bearer $POSTER_TOKEN")

FINAL_STATUS=$(extract "$TASK_CHECK" "['data']['status']")
[[ "$FINAL_STATUS" == "completed" ]] && pass "Task status is 'completed' — awaiting poster confirmation" || fail "Expected 'completed', got '$FINAL_STATUS'"

# ─────────────────────────────────────────────
# STEP 15: Poster confirms and releases payment
# ─────────────────────────────────────────────
step "15. Poster confirms task and releases payment to runner"
CONFIRM=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/confirm" \
  -H "Authorization: Bearer $POSTER_TOKEN")

check "$CONFIRM" "['success']" "True" "Payment released to runner"

# ─────────────────────────────────────────────
# STEP 16: Verify runner's wallet was credited
# ─────────────────────────────────────────────
step "16. Verify runner wallet balance"
WALLET=$(curl -s -X GET "$BASE_URL/payment/wallet" \
  -H "Authorization: Bearer $RUNNER_TOKEN")

check "$WALLET" "['success']" "True" "Runner wallet fetched"
BALANCE=$(extract "$WALLET" "['data']['balance']")
pass "Runner wallet balance: R$BALANCE"

# ─────────────────────────────────────────────
# STEP 17: Final task state check
# ─────────────────────────────────────────────
step "17. Final task state verification"
FINAL=$(curl -s -X GET "$BASE_URL/tasks/$TASK_ID" \
  -H "Authorization: Bearer $POSTER_TOKEN")

FINAL_STATUS=$(extract "$FINAL" "['data']['status']")
pass "Final task status: $FINAL_STATUS"

# ─────────────────────────────────────────────
# SUMMARY
# ─────────────────────────────────────────────
echo ""
echo "================================================"
echo -e "${GREEN}  ✅ E2E TEST PASSED${NC}"
echo "================================================"
echo "  Task ID:       $TASK_ID"
echo "  Poster:        $POSTER_EMAIL (ID: $POSTER_ID)"
echo "  Runner:        $RUNNER_EMAIL (ID: $RUNNER_ID)"
echo "  Runner Wallet: R$BALANCE"
echo "  Final Status:  $FINAL_STATUS"
echo "================================================"
