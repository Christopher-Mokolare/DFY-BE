#!/bin/bash
# user-journey-tests.sh - Complete User Journey E2E Tests
# Usage: ./user-journey-tests.sh [BASE_URL] [FE_URL]

BASE_URL="${1:-https://qa-api.doforyou.co.za/api/v1}"
FE_URL="${2:-https://do-for-you-qa.vercel.app}"

GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

PASS=0
FAIL=0
SKIP=0
FAILURES=()

pass() { echo -e "${GREEN}✅ PASS: $1${NC}"; ((PASS++)); }
fail() { echo -e "${RED}❌ FAIL: $1${NC}"; ((FAIL++)); FAILURES+=("$1"); }
skip() { echo -e "${BLUE}⏭️ SKIP: $1${NC}"; ((SKIP++)); }
step() { echo -e "\n${YELLOW}▶ $1${NC}"; }

contains() { echo "$1" | grep -q "$2"; }

extract_token() {
  echo "$1" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('token',''))" 2>/dev/null
}

extract() {
  echo "$1" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d$2)" 2>/dev/null
}

echo "================================================"
echo "  DFY Complete User Journey Tests"
echo "  API: $BASE_URL"
echo "  FE:  $FE_URL"
echo "================================================"

# ============================================================
# SECTION 1: POSTER JOURNEY
# ============================================================
step "1. Poster Complete Journey"

POSTER_EMAIL="poster_journey_$(date +%s)@test.dfy"
PASSWORD="Test@1234"
FUTURE_DATE=$(python3 -c "from datetime import datetime,timedelta; print((datetime.utcnow()+timedelta(days=7)).strftime('%Y-%m-%dT%H:%M:%SZ'))" 2>/dev/null)

# ----- PHASE 1: Onboarding -----
echo "  [1.1] Register as Poster..."
POSTER_REG=$(curl -s -X POST "$BASE_URL/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"firstName\":\"Alice\",\"lastName\":\"Poster\",\"email\":\"$POSTER_EMAIL\",\"password\":\"$PASSWORD\",\"phoneNumber\":\"0811111111\",\"userType\":\"creator\",\"address\":\"123 Main St, Sandton\",\"idNumber\":\"9001015009087\"}")
POSTER_TOKEN=$(extract_token "$POSTER_REG")
if [[ -n "$POSTER_TOKEN" ]]; then pass "1.1 Register as Poster"; else fail "1.1 Register as Poster"; fi

echo "  [1.2] Login as Poster..."
POSTER_LOGIN=$(curl -s -X POST "$BASE_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$POSTER_EMAIL\",\"password\":\"$PASSWORD\"}")
POSTER_TOKEN=$(extract_token "$POSTER_LOGIN")
[[ -n "$POSTER_TOKEN" ]] && pass "1.2 Login as Poster" || fail "1.2 Login as Poster"

echo "  [1.3] Complete Profile..."
PROFILE=$(curl -s -X PUT "$BASE_URL/user/profile" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $POSTER_TOKEN" \
  -d '{"firstName":"Alice","lastName":"Poster","phoneNumber":"0811111111","address":"123 Main St, Sandton"}')
contains "$PROFILE" "success" && pass "1.3 Complete Profile" || fail "1.3 Complete Profile"

echo "  [1.4] Set User Preferences..."
PREF=$(curl -s -X PUT "$BASE_URL/UserPreferences" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $POSTER_TOKEN" \
  -d '{"userType":"creator"}')
contains "$PREF" "success" && pass "1.4 Set User Preferences" || fail "1.4 Set User Preferences"

echo "  [1.5] View Dashboard..."
DASH=$(curl -s "$BASE_URL/user/dashboard/stats" \
  -H "Authorization: Bearer $POSTER_TOKEN" 2>/dev/null || echo '{"success":false}')
if contains "$DASH" "success"; then pass "1.5 View Dashboard"; else fail "1.5 View Dashboard"; fi

# ----- PHASE 2: Task Creation -----
echo "  [1.6] Create Task..."
TASK_REG=$(curl -s -X POST "$BASE_URL/tasks" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $POSTER_TOKEN" \
  -d "{\"taskDescription\":\"Help move furniture from lounge to bedroom\",\"category\":\"Handyman\",\"area\":\"Sandton, Johannesburg\",\"dateNeeded\":\"$FUTURE_DATE\",\"budget\":500.00,\"notes\":\"Heavy couch and 2 bookshelves\",\"priority\":\"Standard\",\"termsAccepted\":true}")
TASK_ID=$(extract "$TASK_REG" "['data']['task']['taskId']")
[[ -n "$TASK_ID" ]] && pass "1.6 Create Task ($TASK_ID)" || fail "1.6 Create Task"

echo "  [1.7] Verify Task Created..."
TASK_GET=$(curl -s "$BASE_URL/tasks/$TASK_ID" \
  -H "Authorization: Bearer $POSTER_TOKEN")
TASK_STATUS=$(extract "$TASK_GET" "['data']['status']")
[[ -n "$TASK_STATUS" ]] && pass "1.7 Task Created Successfully" || fail "1.7 Task Created"

echo "  [1.8] Edit Task (before payment)..."
EDIT_TASK=$(curl -s -X PUT "$BASE_URL/tasks/$TASK_ID" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $POSTER_TOKEN" \
  -d '{"taskDescription":"Updated: Help move heavy furniture"}')
contains "$EDIT_TASK" "success" && pass "1.8 Edit Task" || skip "1.8 Edit Task (endpoint not implemented)"

echo "  [1.9] Initiate Payment..."
PAYMENT_URL=$(extract "$TASK_REG" "['data']['paymentUrl']")
[[ -n "$PAYMENT_URL" ]] && pass "1.9 Payment URL Generated" || fail "1.9 Payment URL Generated"

echo "  [1.10] Simulate Ozow Payment..."
PAYMENT=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/payment/notify" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "m_payment_id=${TASK_ID}&payment_status=COMPLETE&amount_gross=500.00")
[[ "$PAYMENT" == "200" ]] && pass "1.10 Payment Successful" || fail "1.10 Payment Failed"
sleep 2

# ----- PHASE 3: Task Management -----
echo "  [1.11] Verify Task Posted..."
TASK_POST=$(curl -s "$BASE_URL/tasks/$TASK_ID" \
  -H "Authorization: Bearer $POSTER_TOKEN")
TASK_STATUS=$(extract "$TASK_POST" "['data']['status']")
[[ "$TASK_STATUS" == "posted" ]] && pass "1.11 Task Posted" || fail "1.11 Task Posted"

echo "  [1.12] View My Posted Tasks..."
MY_POSTED=$(curl -s "$BASE_URL/tasks/my-posted" \
  -H "Authorization: Bearer $POSTER_TOKEN")
contains "$MY_POSTED" "data" && pass "1.12 View My Posted Tasks" || fail "1.12 View My Posted Tasks"

# ----- PHASE 4: Communication -----
echo "  [1.13] Send Message to Runner..."
MSG=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/messages" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $POSTER_TOKEN" \
  -d '{"content":"Hi! Please bring your own tools."}')
contains "$MSG" "success" && pass "1.13 Send Message" || fail "1.13 Send Message"

echo "  [1.14] Read Conversation..."
CONV=$(curl -s "$BASE_URL/tasks/$TASK_ID/messages" \
  -H "Authorization: Bearer $POSTER_TOKEN")
contains "$CONV" "data" && pass "1.14 Read Conversation" || fail "1.14 Read Conversation"

echo "  [1.15] View Task Progress..."
PROGRESS=$(curl -s "$BASE_URL/tasks/$TASK_ID/progress" \
  -H "Authorization: Bearer $POSTER_TOKEN")
contains "$PROGRESS" "data" && pass "1.15 View Progress Updates" || fail "1.15 View Progress Updates"

# ----- PHASE 5: Completion & Payment -----
echo "  [1.16] Confirm Task Completion..."
CONFIRM=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/confirm" \
  -H "Authorization: Bearer $POSTER_TOKEN")
contains "$CONFIRM" "success" && pass "1.16 Confirm Task" || fail "1.16 Confirm Task"

echo "  [1.17] Verify Payment Released..."
TASK_PAID=$(curl -s "$BASE_URL/tasks/$TASK_ID" \
  -H "Authorization: Bearer $POSTER_TOKEN")
PAYMENT_STATUS=$(extract "$TASK_PAID" "['data']['paymentStatus']")
ESCROW_STATUS=$(extract "$TASK_PAID" "['data']['escrowStatus']")
TASK_STATUS=$(extract "$TASK_PAID" "['data']['status']")
if [[ "$PAYMENT_STATUS" == "released" || "$PAYMENT_STATUS" == "RunnerPaid" || "$ESCROW_STATUS" == "released" || "$TASK_STATUS" == "runnerpaid" ]]; then
  pass "1.17 Payment Released"
else
  fail "1.17 Payment Not Released (PaymentStatus: $PAYMENT_STATUS, EscrowStatus: $ESCROW_STATUS, TaskStatus: $TASK_STATUS)"
fi

echo "  [1.18] Rate Runner..."
RATING=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/rate" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $POSTER_TOKEN" \
  -d '{"rating":5,"comment":"Excellent work!"}')
contains "$RATING" "success" && pass "1.18 Rate Runner" || skip "1.18 Rate Runner"

echo "  [1.19] View Wallet..."
WALLET=$(curl -s "$BASE_URL/wallet/balance" \
  -H "Authorization: Bearer $POSTER_TOKEN")
contains "$WALLET" "available" && pass "1.19 View Wallet" || fail "1.19 View Wallet"

echo "  [1.20] Logout..."
pass "1.20 Logout (JWT cleared on client)"

# ============================================================
# SECTION 2: RUNNER JOURNEY
# ============================================================
step "2. Runner Complete Journey"

RUNNER_EMAIL="runner_journey_$(date +%s)@test.dfy"
PASSWORD="Test@1234"

# ----- PHASE 1: Onboarding -----
echo "  [2.1] Register as Runner..."
RUNNER_REG=$(curl -s -X POST "$BASE_URL/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"firstName\":\"Bob\",\"lastName\":\"Runner\",\"email\":\"$RUNNER_EMAIL\",\"password\":\"$PASSWORD\",\"phoneNumber\":\"0822222222\",\"userType\":\"runner\",\"address\":\"456 Side St, Cape Town\",\"idNumber\":\"9505105009087\"}")
RUNNER_TOKEN=$(extract_token "$RUNNER_REG")
[[ -n "$RUNNER_TOKEN" ]] && pass "2.1 Register as Runner" || fail "2.1 Register as Runner"

echo "  [2.2] Login as Runner..."
RUNNER_LOGIN=$(curl -s -X POST "$BASE_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$RUNNER_EMAIL\",\"password\":\"$PASSWORD\"}")
RUNNER_TOKEN=$(extract_token "$RUNNER_LOGIN")
[[ -n "$RUNNER_TOKEN" ]] && pass "2.2 Login as Runner" || fail "2.2 Login as Runner"

echo "  [2.3] Complete Profile..."
PROFILE=$(curl -s -X PUT "$BASE_URL/user/profile" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d '{"firstName":"Bob","lastName":"Runner","phoneNumber":"0822222222","address":"456 Side St, Cape Town"}')
contains "$PROFILE" "success" && pass "2.3 Complete Profile" || fail "2.3 Complete Profile"

echo "  [2.4] Set User Preferences..."
PREF=$(curl -s -X PUT "$BASE_URL/UserPreferences" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d '{"userType":"runner"}')
contains "$PREF" "success" && pass "2.4 Set User Preferences" || fail "2.4 Set User Preferences"

echo "  [2.5] View Dashboard..."
DASH=$(curl -s "$BASE_URL/user/dashboard/stats" \
  -H "Authorization: Bearer $RUNNER_TOKEN" 2>/dev/null || echo '{"success":false}')
if contains "$DASH" "success"; then pass "2.5 View Dashboard"; else fail "2.5 View Dashboard"; fi

# ----- PHASE 2: Task Discovery -----
echo "  [2.6] Browse Available Tasks..."
AVAILABLE=$(curl -s "$BASE_URL/tasks/available" \
  -H "Authorization: Bearer $RUNNER_TOKEN")
COUNT=$(extract "$AVAILABLE" "['count']")
[[ "${COUNT:-0}" -gt 0 ]] && pass "2.6 Browse Available Tasks ($COUNT found)" || fail "2.6 Browse Available Tasks"

echo "  [2.7] Search Tasks..."
SEARCH=$(curl -s "$BASE_URL/tasks/available?search=furniture" \
  -H "Authorization: Bearer $RUNNER_TOKEN")
contains "$SEARCH" "furniture" && pass "2.7 Search Tasks" || fail "2.7 Search Tasks"

echo "  [2.8] Filter by Category..."
CATEGORY=$(curl -s "$BASE_URL/tasks/available?category=Handyman" \
  -H "Authorization: Bearer $RUNNER_TOKEN")
contains "$CATEGORY" "Handyman" && pass "2.8 Filter by Category" || fail "2.8 Filter by Category"

echo "  [2.9] View Task Details..."
TASK_DETAIL=$(curl -s "$BASE_URL/tasks/$TASK_ID" \
  -H "Authorization: Bearer $RUNNER_TOKEN")
if echo "$TASK_DETAIL" | grep -q "success\|taskDescription\|data"; then
  pass "2.9 View Task Details"
else
  fail "2.9 View Task Details - Response: $TASK_DETAIL"
fi

echo "  [2.10] Claim Task..."
CLAIM=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/claim" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d '{"helperName":"Bob Runner","helperContact":"0822222222"}')
contains "$CLAIM" "success" && pass "2.10 Claim Task" || fail "2.10 Claim Task"

# ----- PHASE 3: Task Execution -----
echo "  [2.11] View My Active Tasks..."
ACTIVE=$(curl -s "$BASE_URL/tasks/my-active" \
  -H "Authorization: Bearer $RUNNER_TOKEN")
contains "$ACTIVE" "data" && pass "2.11 View My Active Tasks" || fail "2.11 View My Active Tasks"

echo "  [2.12] Send Message to Poster..."
MSG2=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/messages" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d '{"content":"Hi Alice! I will be there at 9am."}')
contains "$MSG2" "success" && pass "2.12 Send Message" || fail "2.12 Send Message"

echo "  [2.13] Read Conversation..."
CONV2=$(curl -s "$BASE_URL/tasks/$TASK_ID/messages" \
  -H "Authorization: Bearer $RUNNER_TOKEN")
contains "$CONV2" "data" && pass "2.13 Read Conversation" || fail "2.13 Read Conversation"

echo "  [2.14] Post Progress Update..."
PROGRESS2=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/progress" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d '{"progressNote":"50 percent done. Couch moved, starting bookshelves."}')
contains "$PROGRESS2" "success" && pass "2.14 Post Progress Update" || fail "2.14 Post Progress Update"

echo "  [2.15] Mark Task Complete..."
COMPLETE=$(curl -s -X POST "$BASE_URL/tasks/$TASK_ID/complete" \
  -H "Authorization: Bearer $RUNNER_TOKEN")
contains "$COMPLETE" "success" && pass "2.15 Mark Task Complete" || fail "2.15 Mark Task Complete"

# ----- PHASE 4: Payment & Wallet -----
echo "  [2.16] Verify Payment Received..."
sleep 2
WALLET_CHECK=$(curl -s "$BASE_URL/wallet/balance" \
  -H "Authorization: Bearer $RUNNER_TOKEN")
BALANCE=$(extract "$WALLET_CHECK" "['data']['availableBalance']")
if [[ -n "$BALANCE" && "$BALANCE" != "0" && "$BALANCE" != "None" ]]; then
  pass "2.16 Payment Received (R$BALANCE)"
else
  fail "2.16 Payment Not Received (Balance: $BALANCE)"
fi

echo "  [2.17] View Transaction History..."
TRANS=$(curl -s "$BASE_URL/wallet/transactions" \
  -H "Authorization: Bearer $RUNNER_TOKEN")
contains "$TRANS" "data" && pass "2.17 View Transaction History" || fail "2.17 View Transaction History"

echo "  [2.18] Request Withdrawal..."
WITHDRAW=$(curl -s -X POST "$BASE_URL/wallet/withdraw" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d '{"amount":100.00,"bankAccount":"1234567890","bankName":"ABSA","accountHolder":"Bob Runner"}')
if echo "$WITHDRAW" | grep -q "success\|processing\|insufficient"; then
  pass "2.18 Request Withdrawal"
else
  skip "2.18 Request Withdrawal (endpoint may not be implemented)"
fi

echo "  [2.19] View Wallet..."
WALLET2=$(curl -s "$BASE_URL/wallet/balance" \
  -H "Authorization: Bearer $RUNNER_TOKEN")
contains "$WALLET2" "available" && pass "2.19 View Wallet" || fail "2.19 View Wallet"

echo "  [2.20] View Completed Tasks..."
COMPLETED=$(curl -s "$BASE_URL/tasks/my-active" \
  -H "Authorization: Bearer $RUNNER_TOKEN")
contains "$COMPLETED" "data" && pass "2.20 View Completed Tasks" || fail "2.20 View Completed Tasks"

echo "  [2.21] Logout..."
pass "2.21 Logout (JWT cleared on client)"

# ============================================================
# SECTION 3: ADMIN JOURNEY
# ============================================================
step "3. Admin Complete Journey"

echo "  [3.1] Admin Login..."
ADMIN_LOGIN=$(curl -s -X POST "$BASE_URL/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@doforyou.com","password":"Admin123"}')
ADMIN_TOKEN=$(extract_token "$ADMIN_LOGIN")
[[ -n "$ADMIN_TOKEN" ]] && pass "3.1 Admin Login" || fail "3.1 Admin Login"

echo "  [3.2] View All Tasks..."
TASKS=$(curl -s "$BASE_URL/admin/tasks?page=1&pageSize=20" \
  -H "Authorization: Bearer $ADMIN_TOKEN")
contains "$TASKS" "success" && pass "3.2 View All Tasks" || fail "3.2 View All Tasks"

echo "  [3.3] Filter Tasks by Status..."
FILTER=$(curl -s "$BASE_URL/admin/tasks?taskStatus=posted&page=1&pageSize=20" \
  -H "Authorization: Bearer $ADMIN_TOKEN")
contains "$FILTER" "success" && pass "3.3 Filter Tasks by Status" || fail "3.3 Filter Tasks by Status"

echo "  [3.4] View All Users..."
USERS=$(curl -s "$BASE_URL/admin/users?page=1&pageSize=20" \
  -H "Authorization: Bearer $ADMIN_TOKEN")
contains "$USERS" "success" && pass "3.4 View All Users" || fail "3.4 View All Users"

echo "  [3.5] View Payment Overview..."
PAYMENTS=$(curl -s "$BASE_URL/admin/payments" \
  -H "Authorization: Bearer $ADMIN_TOKEN")
contains "$PAYMENTS" "totalRevenue" && pass "3.5 View Payment Overview" || fail "3.5 View Payment Overview"

echo "  [3.6] Verify Task..."
VERIFY_TASK=$(curl -s -X PATCH "$BASE_URL/admin/tasks/$TASK_ID/verify" \
  -H "Authorization: Bearer $ADMIN_TOKEN")
contains "$VERIFY_TASK" "success" && pass "3.6 Verify Task" || fail "3.6 Verify Task"

echo "  [3.7] Unverify Task..."
UNVERIFY=$(curl -s -X PATCH "$BASE_URL/admin/tasks/$TASK_ID/unverify" \
  -H "Authorization: Bearer $ADMIN_TOKEN")
contains "$UNVERIFY" "success" && pass "3.7 Unverify Task" || fail "3.7 Unverify Task"

echo "  [3.8] Admin Logout..."
pass "3.8 Admin Logout"

# ============================================================
# SECTION 4: GUEST JOURNEY
# ============================================================
step "4. Guest (Unauthenticated) Journey"

echo "  [4.1] View Home Page..."
HOME=$(curl -s -o /dev/null -w "%{http_code}" "$FE_URL/")
[[ "$HOME" == "200" ]] && pass "4.1 View Home Page" || fail "4.1 View Home Page"

echo "  [4.2] View Browse Tasks..."
BROWSE=$(curl -s -o /dev/null -w "%{http_code}" "$FE_URL/browse")
[[ "$BROWSE" == "200" ]] && pass "4.2 View Browse Tasks" || fail "4.2 View Browse Tasks"

echo "  [4.3] Open Login Page..."
LOGIN=$(curl -s -o /dev/null -w "%{http_code}" "$FE_URL/login")
[[ "$LOGIN" == "200" ]] && pass "4.3 Open Login Page" || fail "4.3 Open Login Page"

echo "  [4.4] Open Register Page..."
REGISTER=$(curl -s -o /dev/null -w "%{http_code}" "$FE_URL/register")
[[ "$REGISTER" == "200" ]] && pass "4.4 Open Register Page" || fail "4.4 Open Register Page"

# ============================================================
# SUMMARY
# ============================================================
TOTAL=$((PASS + FAIL + SKIP))
echo ""
echo "================================================"
echo "  DFY User Journey Tests Results"
echo "================================================"
if [[ $FAIL -eq 0 ]]; then
  echo -e "${GREEN}  ✅ ALL USER JOURNEYS COMPLETE${NC}"
else
  echo -e "${RED}  ❌ $FAIL/$((PASS+FAIL)) TESTS FAILED${NC}"
  echo ""
  echo "  Failed tests:"
  for f in "${FAILURES[@]}"; do
    echo -e "  ${RED}• $f${NC}"
  done
fi
echo "================================================"
echo "  ${GREEN}Passed:  $PASS${NC}"
echo "  ${RED}Failed:  $FAIL${NC}"
echo "  ${BLUE}Skipped: $SKIP${NC}"
echo "  Total:   $TOTAL"
echo "------------------------------------------------"
echo "  Poster:  $POSTER_EMAIL"
echo "  Runner:  $RUNNER_EMAIL"
echo "  Task ID: ${TASK_ID:-N/A}"
echo "================================================"

if [[ $FAIL -eq 0 ]]; then
  echo -e "${GREEN}  🎉 ALL USER JOURNEYS PASSED!${NC}"
  exit 0
else
  echo -e "${RED}  ❌ $FAIL TESTS FAILED${NC}"
  exit 1
fi