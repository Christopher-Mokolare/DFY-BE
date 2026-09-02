#!/bin/bash
# scenario-test.sh
# 3 users · 6 tasks (2 failed/cancelled, 1 unclaimed, 3 claimed) · chat · dispute + resolution · admin chat access
# Usage: ./scenario-test.sh [BASE_URL]

BASE_URL="${1:-https://qa-api.doforyou.co.za/api/v1}"
TS=$(date +%s)
PASSWORD="Test@1234"
FUTURE=$(python3 -c "from datetime import datetime,timedelta; print((datetime.utcnow()+timedelta(days=7)).strftime('%Y-%m-%dT%H:%M:%SZ'))")

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
PASS=0; FAIL=0; FAILURES=()

pass()  { echo -e "${GREEN}  ✅ $1${NC}"; ((PASS++)); }
fail()  { echo -e "${RED}  ❌ $1${NC}"; ((FAIL++)); FAILURES+=("$1"); }
step()  { echo -e "\n${YELLOW}━━━ $1 ━━━${NC}"; }
info()  { echo -e "${CYAN}     $1${NC}"; }

extract() { echo "$1" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d$2)" 2>/dev/null; }
ok()      { echo "$1" | python3 -c "import sys,json; d=json.load(sys.stdin); print('True' if d.get('success') else 'False')" 2>/dev/null; }

register() {
  local first=$1 last=$2 email=$3 type=$4 phone=$5
  curl -s -X POST "$BASE_URL/auth/register" -H "Content-Type: application/json" -d \
    "{\"firstName\":\"$first\",\"lastName\":\"$last\",\"email\":\"$email\",\"password\":\"$PASSWORD\",
      \"phoneNumber\":\"$phone\",\"userType\":\"$type\",\"address\":\"1 Test St, Johannesburg\",\"idNumber\":\"9001015009087\"}"
}

create_task() {
  local token=$1 desc=$2 cat=$3 area=$4 budget=$5
  curl -s -X POST "$BASE_URL/tasks" -H "Content-Type: application/json" -H "Authorization: Bearer $token" -d \
    "{\"taskDescription\":\"$desc\",\"category\":\"$cat\",\"area\":\"$area\",
      \"dateNeeded\":\"$FUTURE\",\"budget\":$budget,\"priority\":\"Standard\",\"termsAccepted\":true}"
}

admin_verify() {
  local token=$1 taskId=$2
  curl -s -X PATCH "$BASE_URL/admin/tasks/$taskId/verify" -H "Authorization: Bearer $token"
}

echo "════════════════════════════════════════════════"
echo "  DFY Scenario Test  ·  $(date '+%Y-%m-%d %H:%M')"
echo "  API: $BASE_URL"
echo "════════════════════════════════════════════════"

# ─────────────────────────────────────────────────────
step "1. Register 3 users (Creator · Runner · Disputer)"
# ─────────────────────────────────────────────────────

CREATOR_EMAIL="creator.sc_${TS}@test.dfy"
RUNNER_EMAIL="runner.sc_${TS}@test.dfy"
DISPUTER_EMAIL="disputer.sc_${TS}@test.dfy"

C_REG=$(register "Sam" "Creator" "$CREATOR_EMAIL" "creator" "0811110001")
R_REG=$(register "Ray" "Runner"  "$RUNNER_EMAIL"  "runner"  "0822220002")
D_REG=$(register "Dana" "Disputer" "$DISPUTER_EMAIL" "creator" "0833330003")

CREATOR_TOKEN=$(extract "$C_REG" "['token']")
RUNNER_TOKEN=$(extract  "$R_REG" "['token']")
DISPUTER_TOKEN=$(extract "$D_REG" "['token']")
CREATOR_ID=$(extract "$C_REG" "['user']['id']")
RUNNER_ID=$(extract  "$R_REG" "['user']['id']")

[[ -n "$CREATOR_TOKEN" ]] && pass "Creator registered (ID: $CREATOR_ID)" || fail "Creator registration"
[[ -n "$RUNNER_TOKEN"  ]] && pass "Runner registered  (ID: $RUNNER_ID)"  || fail "Runner registration"
[[ -n "$DISPUTER_TOKEN" ]] && pass "Disputer registered" || fail "Disputer registration"

# Admin login
ADMIN_LOGIN=$(curl -s -X POST "$BASE_URL/auth/login" -H "Content-Type: application/json" \
  -d '{"email":"admin@doforyou.com","password":"Admin123"}')
ADMIN_TOKEN=$(extract "$ADMIN_LOGIN" "['token']")
[[ -n "$ADMIN_TOKEN" ]] && pass "Admin logged in" || fail "Admin login"

# ─────────────────────────────────────────────────────
step "2. Create 6 tasks"
# ─────────────────────────────────────────────────────

# Task 1 — will be cancelled (failed) by creator
T1=$(create_task "$CREATOR_TOKEN" "Deep clean 3-bedroom house" "Cleaning" "Sandton" 350)
TASK1=$(extract "$T1" "['data']['task']['taskId']")
[[ -n "$TASK1" ]] && pass "Task 1 created: $TASK1 (will cancel)" || fail "Task 1 creation"

# Task 2 — will be cancelled (failed) by admin unverify path
T2=$(create_task "$CREATOR_TOKEN" "Grocery run and delivery" "Delivery" "Rosebank" 150)
TASK2=$(extract "$T2" "['data']['task']['taskId']")
[[ -n "$TASK2" ]] && pass "Task 2 created: $TASK2 (will cancel)" || fail "Task 2 creation"

# Task 3 — posted but never claimed
T3=$(create_task "$CREATOR_TOKEN" "Fix leaking kitchen tap" "Handyman" "Midrand" 200)
TASK3=$(extract "$T3" "['data']['task']['taskId']")
[[ -n "$TASK3" ]] && pass "Task 3 created: $TASK3 (unclaimed)" || fail "Task 3 creation"

# Task 4 — claimed, with chat
T4=$(create_task "$CREATOR_TOKEN" "Move furniture from lounge to bedroom" "Handyman" "Fourways" 500)
TASK4=$(extract "$T4" "['data']['task']['taskId']")
[[ -n "$TASK4" ]] && pass "Task 4 created: $TASK4 (claimed + chat)" || fail "Task 4 creation"

# Task 5 — claimed, completed
T5=$(create_task "$CREATOR_TOKEN" "Urgent grocery run for the week" "Home" "Pretoria" 180)
TASK5=$(extract "$T5" "['data']['task']['taskId']")
[[ -n "$TASK5" ]] && pass "Task 5 created: $TASK5 (claimed + completed)" || fail "Task 5 creation"

# Task 6 — claimed, dispute raised and resolved by admin
T6=$(create_task "$DISPUTER_TOKEN" "Garden cleanup and weeding" "Gardening" "Centurion" 420)
TASK6=$(extract "$T6" "['data']['task']['taskId']")
[[ -n "$TASK6" ]] && pass "Task 6 created: $TASK6 (dispute)" || fail "Task 6 creation"

# ─────────────────────────────────────────────────────
step "3. Admin verifies payment for tasks 3–6 (posts them)"
# ─────────────────────────────────────────────────────

for TID in "$TASK3" "$TASK4" "$TASK5" "$TASK6"; do
  R=$(admin_verify "$ADMIN_TOKEN" "$TID")
  [[ $(ok "$R") == "True" ]] && pass "Admin verified $TID" || fail "Admin verify $TID"
done

# ─────────────────────────────────────────────────────
step "4. Cancel tasks 1 & 2 (simulate failed/cancelled tasks)"
# ─────────────────────────────────────────────────────

# Task 1: creator cancels before payment (still PendingPayment — delete it)
DEL1=$(curl -s -X DELETE "$BASE_URL/tasks/$TASK1" -H "Authorization: Bearer $CREATOR_TOKEN")
DEL1_OK=$(ok "$DEL1")
[[ "$DEL1_OK" == "True" ]] && pass "Task 1 deleted (creator abandoned before payment)" || { echo "  DEL1 response: $DEL1"; fail "Task 1 delete"; }

# Task 2: admin hard-cancels via unverify then force status
CANCEL2=$(curl -s -X PATCH "$BASE_URL/admin/tasks/$TASK2/unverify" -H "Authorization: Bearer $ADMIN_TOKEN")
[[ $(ok "$CANCEL2") == "True" ]] && pass "Task 2 unverified (payment rejected / failed)" || fail "Task 2 cancel"
info "Tasks 1 & 2 represent failed/cancelled payment scenarios"

# ─────────────────────────────────────────────────────
step "5. Task 3 — posted, never claimed (sits open)"
# ─────────────────────────────────────────────────────

T3_DETAIL=$(curl -s "$BASE_URL/tasks/$TASK3" -H "Authorization: Bearer $CREATOR_TOKEN")
T3_STATUS=$(extract "$T3_DETAIL" "['data']['status']")
[[ "$T3_STATUS" == "posted" ]] && pass "Task 3 is posted and unclaimed (status: $T3_STATUS)" || fail "Task 3 status (got: $T3_STATUS)"

# ─────────────────────────────────────────────────────
step "6. Task 4 — claimed + 6-message chat between creator and runner"
# ─────────────────────────────────────────────────────

CLAIM4=$(curl -s -X POST "$BASE_URL/tasks/$TASK4/claim" \
  -H "Content-Type: application/json" -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d '{"helperName":"Ray Runner","helperContact":"0822220002"}')
[[ $(ok "$CLAIM4") == "True" ]] && pass "Task 4 claimed by runner" || fail "Task 4 claim"

# 6 messages alternating creator ↔ runner
MSGS=(
  "$CREATOR_TOKEN|Hi Ray! Please bring your own tools. The couch is very heavy."
  "$RUNNER_TOKEN|No problem Sam! I have a trolley. What time works for you?"
  "$CREATOR_TOKEN|Saturday at 10am would be perfect. The address is 12 Oak Ave, Fourways."
  "$RUNNER_TOKEN|Got it. I will be there at 10am sharp. Should take about 2 hours."
  "$CREATOR_TOKEN|Great! Please also move the bookshelf in the study if you have time."
  "$RUNNER_TOKEN|Sure, I will handle everything. See you Saturday!"
)

MSG_COUNT=0
for ENTRY in "${MSGS[@]}"; do
  TOK="${ENTRY%%|*}"
  CONTENT="${ENTRY#*|}"
  R=$(curl -s -X POST "$BASE_URL/tasks/$TASK4/messages" \
    -H "Content-Type: application/json" -H "Authorization: Bearer $TOK" \
    -d "{\"content\":\"$CONTENT\"}")
  [[ $(ok "$R") == "True" ]] && ((MSG_COUNT++))
done
[[ $MSG_COUNT -eq 6 ]] && pass "6 messages sent on Task 4 ($MSG_COUNT/6)" || fail "Task 4 chat ($MSG_COUNT/6 sent)"

# Runner posts a progress update
PROG4=$(curl -s -X POST "$BASE_URL/tasks/$TASK4/progress" \
  -H "Content-Type: application/json" -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d '{"progressNote":"Arrived on site. Couch moved to bedroom. Starting on bookshelf now."}')
[[ $(ok "$PROG4") == "True" ]] && pass "Task 4 progress update posted" || fail "Task 4 progress"

# Verify creator can read the conversation
CONV4=$(curl -s "$BASE_URL/tasks/$TASK4/messages" -H "Authorization: Bearer $CREATOR_TOKEN")
CONV4_COUNT=$(echo "$CONV4" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d.get('data',[])))" 2>/dev/null)
[[ "${CONV4_COUNT:-0}" -ge 6 ]] && pass "Creator reads $CONV4_COUNT messages on Task 4" || fail "Creator read messages ($CONV4_COUNT)"

# ─────────────────────────────────────────────────────
step "7. Task 5 — claimed, completed, payment released"
# ─────────────────────────────────────────────────────

CLAIM5=$(curl -s -X POST "$BASE_URL/tasks/$TASK5/claim" \
  -H "Content-Type: application/json" -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d '{"helperName":"Ray Runner","helperContact":"0822220002"}')
[[ $(ok "$CLAIM5") == "True" ]] && pass "Task 5 claimed" || fail "Task 5 claim"

# Quick chat
curl -s -X POST "$BASE_URL/tasks/$TASK5/messages" \
  -H "Content-Type: application/json" -H "Authorization: Bearer $CREATOR_TOKEN" \
  -d '{"content":"Please get the full list from the fridge door."}' > /dev/null
curl -s -X POST "$BASE_URL/tasks/$TASK5/messages" \
  -H "Content-Type: application/json" -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d '{"content":"Got it, on my way now!"}' > /dev/null

COMPLETE5=$(curl -s -X POST "$BASE_URL/tasks/$TASK5/complete" -H "Authorization: Bearer $RUNNER_TOKEN")
[[ $(ok "$COMPLETE5") == "True" ]] && pass "Task 5 marked complete by runner" || fail "Task 5 complete"

CONFIRM5=$(curl -s -X POST "$BASE_URL/tasks/$TASK5/confirm" -H "Authorization: Bearer $CREATOR_TOKEN")
[[ $(ok "$CONFIRM5") == "True" ]] && pass "Task 5 confirmed by creator — payment released" || fail "Task 5 confirm"

T5_FINAL=$(curl -s "$BASE_URL/tasks/$TASK5" -H "Authorization: Bearer $CREATOR_TOKEN")
T5_STATUS=$(extract "$T5_FINAL" "['data']['status']")
[[ "$T5_STATUS" == "runnerpaid" || "$T5_STATUS" == "completed" ]] \
  && pass "Task 5 final status: $T5_STATUS" \
  || fail "Task 5 final status unexpected: $T5_STATUS"

# ─────────────────────────────────────────────────────
step "8. Task 6 — claimed, runner completes, creator disputes, admin resolves"
# ─────────────────────────────────────────────────────

CLAIM6=$(curl -s -X POST "$BASE_URL/tasks/$TASK6/claim" \
  -H "Content-Type: application/json" -H "Authorization: Bearer $RUNNER_TOKEN" \
  -d '{"helperName":"Ray Runner","helperContact":"0822220002"}')
[[ $(ok "$CLAIM6") == "True" ]] && pass "Task 6 claimed" || fail "Task 6 claim"

COMPLETE6=$(curl -s -X POST "$BASE_URL/tasks/$TASK6/complete" -H "Authorization: Bearer $RUNNER_TOKEN")
[[ $(ok "$COMPLETE6") == "True" ]] && pass "Task 6 marked complete by runner" || fail "Task 6 complete"

# Disputer (creator of task 6) raises dispute
DISPUTE=$(curl -s -X POST "$BASE_URL/disputes" \
  -H "Content-Type: application/json" -H "Authorization: Bearer $DISPUTER_TOKEN" \
  -d "{\"taskId\":\"$TASK6\",\"issue\":\"The garden was not cleaned properly. Weeds were left in the back section and the waste was not collected as agreed.\",\"category\":\"Quality\"}")
DISPUTE_ID=$(extract "$DISPUTE" "['data']['id']")
[[ -n "$DISPUTE_ID" ]] && pass "Dispute raised on Task 6 (ID: $DISPUTE_ID)" || fail "Raise dispute"

# Admin reads all disputes
DISPUTES=$(curl -s "$BASE_URL/disputes" -H "Authorization: Bearer $ADMIN_TOKEN")
DISPUTE_COUNT=$(echo "$DISPUTES" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d.get('data',{}).get('disputes',[])))" 2>/dev/null)
[[ "${DISPUTE_COUNT:-0}" -ge 1 ]] && pass "Admin sees $DISPUTE_COUNT dispute(s)" || fail "Admin list disputes"

# Admin resolves — refund creator
RESOLVE=$(curl -s -X PATCH "$BASE_URL/disputes/$DISPUTE_ID/resolve" \
  -H "Content-Type: application/json" -H "Authorization: Bearer $ADMIN_TOKEN" \
  -d '{"resolution":"Work was incomplete. Refund issued to creator after review of evidence.","action":"refund_creator"}')
[[ $(ok "$RESOLVE") == "True" ]] && pass "Admin resolved dispute (refund_creator)" || fail "Admin resolve dispute"

# Verify task is now cancelled/refunded
T6_FINAL=$(curl -s "$BASE_URL/tasks/$TASK6" -H "Authorization: Bearer $DISPUTER_TOKEN")
T6_STATUS=$(extract "$T6_FINAL" "['data']['status']")
[[ "$T6_STATUS" == "cancelled" ]] \
  && pass "Task 6 status after resolution: $T6_STATUS" \
  || fail "Task 6 status after resolution: $T6_STATUS (expected cancelled)"

# ─────────────────────────────────────────────────────
step "9. Admin reads chat for Task 4 (admin message access)"
# ─────────────────────────────────────────────────────

# Via dedicated admin endpoint
ADMIN_MSGS=$(curl -s "$BASE_URL/admin/tasks/$TASK4/messages" -H "Authorization: Bearer $ADMIN_TOKEN")
ADMIN_MSG_COUNT=$(echo "$ADMIN_MSGS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d.get('data',[])))" 2>/dev/null)
[[ "${ADMIN_MSG_COUNT:-0}" -ge 6 ]] \
  && pass "Admin reads Task 4 chat via /admin/tasks/{id}/messages ($ADMIN_MSG_COUNT messages)" \
  || fail "Admin admin-endpoint chat ($ADMIN_MSG_COUNT messages)"

# Via standard task messages endpoint (admin bypass)
ADMIN_MSGS2=$(curl -s "$BASE_URL/tasks/$TASK4/messages" -H "Authorization: Bearer $ADMIN_TOKEN")
ADMIN_MSG_COUNT2=$(echo "$ADMIN_MSGS2" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d.get('data',[])))" 2>/dev/null)
[[ "${ADMIN_MSG_COUNT2:-0}" -ge 6 ]] \
  && pass "Admin reads Task 4 chat via standard /tasks/{id}/messages ($ADMIN_MSG_COUNT2 messages)" \
  || fail "Admin standard-endpoint chat ($ADMIN_MSG_COUNT2 messages)"

# Spot-check message content
FIRST_SENDER=$(echo "$ADMIN_MSGS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['data'][0].get('senderName',''))" 2>/dev/null)
info "First message sender: $FIRST_SENDER"
[[ -n "$FIRST_SENDER" ]] && pass "Message senderName present in admin response" || fail "senderName missing"

# ─────────────────────────────────────────────────────
step "10. Admin dashboard & audit log verification"
# ─────────────────────────────────────────────────────

DASH=$(curl -s "$BASE_URL/admin/dashboard" -H "Authorization: Bearer $ADMIN_TOKEN")
[[ $(ok "$DASH") == "True" ]] && pass "Admin dashboard loads" || fail "Admin dashboard"

TOTAL_USERS=$(extract "$DASH" "['data']['totalUsers']")
TOTAL_TASKS=$(extract "$DASH" "['data']['totalTasks']")
OPEN_DISPUTES=$(extract "$DASH" "['data']['openDisputes']")
info "Dashboard: $TOTAL_USERS users · $TOTAL_TASKS tasks · $OPEN_DISPUTES open disputes"

AUDIT=$(curl -s "$BASE_URL/admin/audit-logs?page=1&pageSize=10" -H "Authorization: Bearer $ADMIN_TOKEN")
AUDIT_COUNT=$(echo "$AUDIT" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d.get('data',{}).get('logs',[])))" 2>/dev/null)
[[ "${AUDIT_COUNT:-0}" -ge 1 ]] && pass "Audit log has $AUDIT_COUNT entries" || fail "Audit log empty"

# Admin notifications (should have entries from this test run)
NOTIFS=$(curl -s "$BASE_URL/notifications" -H "Authorization: Bearer $ADMIN_TOKEN")
NOTIF_COUNT=$(echo "$NOTIFS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d.get('data',[])))" 2>/dev/null)
[[ "${NOTIF_COUNT:-0}" -ge 1 ]] && pass "Admin has $NOTIF_COUNT notification(s)" || fail "Admin notifications empty"

# ─────────────────────────────────────────────────────
step "11. Summary of all task states"
# ─────────────────────────────────────────────────────

ALL_TASKS=$(curl -s "$BASE_URL/admin/tasks?pageSize=100" -H "Authorization: Bearer $ADMIN_TOKEN")

for TID_LABEL in "$TASK2:Task2(cancelled)" "$TASK3:Task3(unclaimed)" "$TASK4:Task4(claimed+chat)" "$TASK5:Task5(completed)" "$TASK6:Task6(disputed)"; do
  TID="${TID_LABEL%%:*}"
  LABEL="${TID_LABEL#*:}"
  STATUS=$(echo "$ALL_TASKS" | python3 -c "
import sys,json
d=json.load(sys.stdin)
tasks=d.get('data',{}).get('tasks',[])
t=next((t for t in tasks if t.get('taskId')=='$TID'),None)
print(t['taskStatus'] if t else 'NOT_FOUND')
" 2>/dev/null)
  info "$LABEL → $STATUS"
done

# ─────────────────────────────────────────────────────
echo ""
echo "════════════════════════════════════════════════"
echo "  SCENARIO TEST RESULTS"
echo "════════════════════════════════════════════════"
echo -e "  ${GREEN}Passed: $PASS${NC}"
echo -e "  ${RED}Failed: $FAIL${NC}"
echo ""
echo "  Users:"
echo "    Creator:  $CREATOR_EMAIL"
echo "    Runner:   $RUNNER_EMAIL"
echo "    Disputer: $DISPUTER_EMAIL"
echo ""
echo "  Tasks:"
echo "    $TASK1  → deleted (abandoned)"
echo "    $TASK2  → cancelled (payment failed)"
echo "    $TASK3  → posted, unclaimed"
echo "    $TASK4  → claimed, 6-message chat"
echo "    $TASK5  → completed, paid"
echo "    $TASK6  → disputed, refunded"
echo "    Dispute: ID $DISPUTE_ID"

if [[ $FAIL -gt 0 ]]; then
  echo ""
  echo -e "  ${RED}Failed checks:${NC}"
  for f in "${FAILURES[@]}"; do echo -e "  ${RED}• $f${NC}"; done
  echo "════════════════════════════════════════════════"
  exit 1
else
  echo ""
  echo -e "  ${GREEN}✅ ALL SCENARIOS PASSED${NC}"
  echo "════════════════════════════════════════════════"
  exit 0
fi
