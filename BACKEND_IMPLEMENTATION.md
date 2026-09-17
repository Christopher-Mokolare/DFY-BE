# DoForYou API - Backend Implementation & Business Rules

## Table of Contents
1. [Architecture Overview](#architecture-overview)
2. [Database Schema](#database-schema)
3. [Business Rules Engine](#business-rules-engine)
4. [API Endpoints](#api-endpoints)
5. [Authentication & Authorization](#authentication--authorization)
6. [Task Workflow](#task-workflow)
7. [Payment Integration](#payment-integration)
8. [Data Models](#data-models)

---

## Architecture Overview

### Technology Stack
- **Framework**: .NET 8 Web API
- **Database**: PostgreSQL with Entity Framework Core
- **Authentication**: JWT Bearer Tokens
- **Password Hashing**: BCrypt
- **Payment Gateway**: Ozow (Sandbox)
- **API Documentation**: Swagger/OpenAPI

### Project Structure
```
DoForYou-API/
├── Controllers/          # API endpoint controllers
├── Models/              # Domain models
├── DTOs/                # Data Transfer Objects
├── Data/                # Database context
├── Services/            # Business logic services
├── Middleware/          # Custom middleware
└── Migrations/          # EF Core migrations
```

### Ozow Mode
- Default mode is sandbox for local and staging test environments.
- Set `OZOW_MODE=sandbox` to force sandbox checkout URLs.
- Set `OZOW_MODE=live` only for production deployment.
- The checkout URL uses `https://sandbox.ozow.co.za/eng/process` in sandbox mode and `https://www.ozow.co.za/eng/process` in live mode.

### Key Design Patterns
- **Repository Pattern**: Via Entity Framework DbContext
- **Dependency Injection**: Built-in .NET DI container
- **Rules Engine Pattern**: Dynamic business rule evaluation
- **DTO Pattern**: Separation of domain and API models

---

## Database Schema

### Core Tables

#### Users Table
```sql
- Id (PK, int)
- FirstName (string)
- LastName (string)
- Email (string, unique)
- PhoneNumber (string)
- UserType (string: "Poster", "Runner", "Both")
- IdNumber (string, nullable)
- Address (string, nullable)
- PasswordHash (string)
- IsVerified (bool)
- ProfileCompleted (bool)
- Rating (decimal)
- CompletedTasks (int)
- CreatedAt (datetime)
- LastLoginAt (datetime, nullable)
- Roles (string: "User", "Admin")
- EmailVerified (bool)
- PhoneVerified (bool)
- DateOfBirth (datetime, nullable)
- Username (string, nullable)
- WalletBalance (decimal)
```

#### Tasks Table
```sql
- Id (PK, int)
- TaskId (string, unique: "DFY-{timestamp}-{random}")
- TaskDescription (string)
- Category (string)
- Area (string)
- DateNeeded (datetime)
- Budget (decimal, range: 50-10000)
- Notes (string, nullable)
- PaymentStatus (string: "Pending", "Completed")
- TaskStatus (string: "PendingPayment", "Posted", "Claimed", "Completed", "RunnerPaid")
- Priority (string: "Standard", "Urgent")
- CreatedByUserId (FK, int)
- AcceptedByUserId (FK, int, nullable)
- HelperName (string, nullable)
- HelperContact (string, nullable)
- HelperEmail (string, nullable)
- CreatedAt (datetime)
- UpdatedAt (datetime)
- CompletedAt (datetime, nullable)
- PaidToRunnerAt (datetime, nullable)
```

#### Categories Table
```sql
- Id (PK, int)
- Name (string)
- Description (string, nullable)
- Icon (string, nullable)
- SortOrder (int)
- CreatedAt (datetime)
```

#### BusinessRules Table
```sql
- Id (PK, int)
- RuleName (string)
- RuleType (string: "validation", "permission", "calculation", "workflow")
- Entity (string: "User", "Task", "Payment")
- Condition (JSON string)
- Action (string: "allow", "deny", "validate", "require")
- ErrorMessage (string, nullable)
- IsActive (bool)
- Priority (int)
- CreatedAt (datetime)
- UpdatedAt (datetime)
```

#### Supporting Tables
- **Payments**: Payment transaction records
- **TaskMessages**: Communication between task creator and runner
- **TaskProgressUpdates**: Progress updates from runners
- **Ratings**: User ratings after task completion
- **Disputes**: Dispute management
- **WalletTransactions**: Wallet transaction history

### Relationships
- User (1) → Tasks (Many) as Creator
- User (1) → Tasks (Many) as Runner
- Task (1) → TaskMessages (Many)
- Task (1) → TaskProgressUpdates (Many)
- Task (1) → Payments (Many)

---

## Business Rules Engine

### Overview
The Rules Engine provides dynamic, database-driven business rule evaluation without code changes.

### Rule Structure
```json
{
  "RuleName": "Rule description",
  "RuleType": "validation|permission|calculation|workflow",
  "Entity": "User|Task|Payment",
  "Condition": {
    "field": "expectedValue",
    "field2": {"operator": "value"}
  },
  "Action": "allow|deny|validate|require",
  "ErrorMessage": "User-friendly error message",
  "IsActive": true,
  "Priority": 0
}
```

### Rule Types

#### 1. Validation Rules
Validate entity data before operations.

**Example: Task Budget Validation**
```json
{
  "RuleName": "Task Budget Must Be Between 50 and 10000",
  "RuleType": "validation",
  "Entity": "Task",
  "Condition": {
    "budget": {
      "min": 50,
      "max": 10000
    }
  },
  "Action": "validate",
  "ErrorMessage": "Task budget must be between R50 and R10,000"
}
```

#### 2. Permission Rules
Control who can perform actions.

**Example: Only Verified Users Can Create Tasks**
```json
{
  "RuleName": "Only Verified Users Can Create Tasks",
  "RuleType": "permission",
  "Entity": "User",
  "Condition": {
    "isVerified": true,
    "profileCompleted": true
  },
  "Action": "allow",
  "ErrorMessage": "You must complete your profile and verify your account to create tasks"
}
```

**Example: Users Cannot Claim Their Own Tasks**
```json
{
  "RuleName": "Users Cannot Claim Their Own Tasks",
  "RuleType": "permission",
  "Entity": "Task",
  "Condition": {
    "createdByUserId": "!=currentUserId"
  },
  "Action": "deny",
  "ErrorMessage": "You cannot claim your own task"
}
```

#### 3. Workflow Rules
Control state transitions.

**Example: Task Status Workflow**
```json
{
  "RuleName": "Task Must Be Paid Before Posting",
  "RuleType": "workflow",
  "Entity": "Task",
  "Condition": {
    "paymentStatus": "Completed"
  },
  "Action": "require",
  "ErrorMessage": "Payment must be completed before task can be posted"
}
```

### Supported Condition Operators

#### User Conditions
- `userType`: String or array of allowed user types
- `profileCompleted`: Boolean
- `isVerified`: Boolean
- `roles`: String or array of required roles

#### Task Conditions
- `budget`: Object with `min` and `max` values
- `required`: Array of required field names
- `createdByUserId`: Comparison operators (`==currentUserId`, `!=currentUserId`)

#### Custom Conditions
Extensible for business-specific logic.

### Rules Engine API

```csharp
public interface IRulesEngine
{
    // Validate entity against rules
    Task<List<RuleValidationResult>> ValidateAsync(
        string entity, 
        string ruleType, 
        RuleContext context
    );
    
    // Check if user can perform action
    Task<bool> CanPerformActionAsync(
        string entity, 
        string action, 
        RuleContext context
    );
    
    // Validate entire entity
    Task<RuleValidationResult> ValidateEntityAsync(
        object entity, 
        RuleContext context
    );
}
```

### Rule Context
```csharp
public class RuleContext
{
    public User? CurrentUser { get; set; }
    public object? Entity { get; set; }
    public string? Action { get; set; }
    public Dictionary<string, object> Parameters { get; set; }
}
```

### Rule Evaluation Flow
1. Fetch active rules for entity and type, ordered by priority
2. Evaluate each rule's condition against context
3. Apply rule action (allow/deny/validate)
4. Return aggregated results
5. For permissions: deny takes precedence over allow

---

## API Endpoints

### Authentication Endpoints

#### POST /api/v1/auth/register
Register a new user.

**Request Body:**
```json
{
  "firstName": "John",
  "lastName": "Doe",
  "email": "john@example.com",
  "password": "<your_password>",
  "phoneNumber": "0123456789",
  "userType": "Both",
  "idNumber": "9001010000000",
  "address": "123 Main St, City",
  "dateOfBirth": "1990-01-01",
  "username": "johndoe"
}
```

**Response:**
```json
{
  "success": true,
  "token": "eyJhbGciOiJIUzI1NiIs...",
  "user": {
    "id": 1,
    "firstName": "John",
    "lastName": "Doe",
    "email": "john@example.com",
    "phoneNumber": "0123456789",
    "profileCompleted": true,
    "rating": 0,
    "completedTasks": 0,
    "roles": "User",
    "isAdmin": false,
    "userType": "Both",
    "isVerified": true
  },
  "message": "Registration successful"
}
```

**Business Rules Applied:**
- Email uniqueness validation
- Password hashing with BCrypt
- Auto-verification for new users
- Default role assignment

#### POST /api/v1/auth/login
Authenticate user and receive JWT token.

**Request Body:**
```json
{
  "email": "john@example.com",
  "password": "<your_password>"
}
```

**Response:**
```json
{
  "success": true,
  "token": "eyJhbGciOiJIUzI1NiIs...",
  "user": { /* UserDto */ },
  "message": "Login successful"
}
```

**Business Rules Applied:**
- Email/password verification
- BCrypt password comparison
- JWT token generation with 7-day expiry
- LastLoginAt timestamp update

---

### Task Management Endpoints

#### POST /api/v1/tasks
Create a new task (requires authentication).

**Request Body:**
```json
{
  "taskDescription": "Need help moving furniture",
  "category": "Transportation",
  "area": "Johannesburg",
  "dateNeeded": "2024-02-15T10:00:00Z",
  "budget": 500,
  "notes": "3 bedroom apartment",
  "priority": "Standard",
  "termsAccepted": true
}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "task": {
      "id": 1,
      "taskId": "DFY-1707123456-7890",
      "taskDescription": "Need help moving furniture",
      "category": "Transportation",
      "area": "Johannesburg",
      "dateNeeded": "2024-02-15T10:00:00Z",
      "budget": 500,
      "paymentStatus": "Pending",
      "taskStatus": "PendingPayment",
      "priority": "Standard"
    },
    "paymentUrl": "https://sandbox.ozow.co.za/eng/process?..."
  },
  "message": "Task created successfully. Complete payment to activate."
}
```

**Business Rules Applied:**
- User must be verified and have completed profile
- Budget must be between R50 and R10,000
- Task ID generation: `DFY-{timestamp}-{random}`
- Initial status: `PendingPayment`
- Ozow payment URL generation

#### GET /api/v1/tasks/available
Browse available tasks (public endpoint).

**Query Parameters:**
- `page` (default: 1)
- `pageSize` (default: 10)
- `search` (optional)
- `category` (optional)

**Response:**
```json
{
  "success": true,
  "count": 25,
  "page": 1,
  "pageSize": 10,
  "totalPages": 3,
  "tasks": [
    {
      "id": 1,
      "taskId": "DFY-1707123456-7890",
      "userName": "John Doe",
      "userContact": "0123456789",
      "taskDescription": "Need help moving furniture",
      "category": "Transportation",
      "area": "Johannesburg",
      "budget": 500,
      "taskStatus": "Posted",
      "priority": "Standard"
    }
  ]
}
```

**Business Rules Applied:**
- Only shows tasks with `PaymentStatus = "Completed"` and `TaskStatus = "Posted"`
- Pagination support
- Search by description or area
- Filter by category


#### GET /api/v1/tasks/my-posted
Get tasks created by current user (requires authentication).

**Response:**
```json
{
  "success": true,
  "data": {
    "success": true,
    "count": 5,
    "tasks": [/* TaskDto array */]
  }
}
```

#### GET /api/v1/tasks/my-active
Get tasks claimed by current user (requires authentication).

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": 1,
      "taskId": "DFY-1707123456-7890",
      "title": "Need help moving furniture",
      "category": "Transportation",
      "budget": 500,
      "status": "claimed",
      "creatorName": "John Doe",
      "creatorContact": "0123456789"
    }
  ]
}
```

#### GET /api/v1/tasks/{taskId}
Get detailed task information with progress updates.

**Response:**
```json
{
  "success": true,
  "data": {
    "id": 1,
    "taskId": "DFY-1707123456-7890",
    "title": "Need help moving furniture",
    "description": "Need help moving furniture",
    "category": "Transportation",
    "location": "Johannesburg",
    "budget": 500,
    "status": "claimed",
    "priority": "standard",
    "createdAt": "2024-02-01T10:00:00Z",
    "dueDate": "2024-02-15T10:00:00Z",
    "creatorName": "John Doe",
    "creatorContact": "0123456789",
    "runnerName": "Jane Smith",
    "runnerContact": "0987654321",
    "runnerId": 2,
    "createdByUserId": 1,
    "progressUpdates": [
      {
        "id": 1,
        "message": "Started packing",
        "timestamp": "2024-02-10T09:00:00Z",
        "userId": 2,
        "userName": "Jane Smith"
      }
    ],
    "canEdit": false,
    "canComplete": true,
    "canCancel": true
  }
}
```

#### POST /api/v1/tasks/{taskId}/claim
Claim an available task (requires authentication).

**Request Body:**
```json
{
  "helperName": "Jane Smith",
  "helperContact": "0987654321"
}
```

**Response:**
```json
{
  "success": true,
  "data": true,
  "message": "Task claimed successfully!"
}
```

**Business Rules Applied:**
- User cannot claim their own tasks
- Task must be in "Posted" status
- Task status changes to "Claimed"
- AcceptedByUserId set to current user

#### POST /api/v1/tasks/{taskId}/complete
Mark task as completed (requires authentication).

**Response:**
```json
{
  "success": true,
  "data": true,
  "message": "Task marked as completed successfully!"
}
```

**Business Rules Applied:**
- Only the runner (AcceptedByUserId) can complete
- Task must be in "Claimed" status
- Task status changes to "Completed"
- CompletedAt timestamp set

#### PATCH /api/v1/tasks/{taskId}/payment-status
Update payment status (requires authentication).

**Request Body:**
```json
{
  "paymentStatus": "Completed"
}
```

**Response:**
```json
{
  "success": true,
  "data": true,
  "message": "Payment status updated successfully!"
}
```

**Business Rules Applied:**
- Only task creator can update
- Can only update from "Pending" to "Completed"
- Task status changes from "PendingPayment" to "Posted"

#### GET /api/v1/tasks/filters
Get available filter options.

**Response:**
```json
{
  "success": true,
  "data": {
    "categories": ["Cleaning", "Handyman", "Transportation", ...],
    "statuses": ["Posted", "Claimed", "Completed"]
  }
}
```

---

### Communication Endpoints

#### POST /api/v1/tasks/{taskId}/messages
Send a message on a task.

**Request Body:**
```json
{
  "content": "When can you start?"
}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "id": 1,
    "taskId": 1,
    "senderId": 1,
    "content": "When can you start?",
    "isRead": false,
    "createdAt": "2024-02-10T10:00:00Z"
  }
}
```

#### GET /api/v1/tasks/{taskId}/messages
Get all messages for a task.

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": 1,
      "senderId": 1,
      "senderName": "John Doe",
      "content": "When can you start?",
      "isRead": false,
      "createdAt": "2024-02-10T10:00:00Z"
    }
  ]
}
```

#### POST /api/v1/tasks/{taskId}/progress
Add a progress update.

**Request Body:**
```json
{
  "message": "Completed packing all items"
}
```

**Response:**
```json
{
  "success": true,
  "data": {
    "id": 1,
    "taskId": 1,
    "userId": 2,
    "message": "Completed packing all items",
    "createdAt": "2024-02-10T14:00:00Z"
  }
}
```

#### GET /api/v1/tasks/{taskId}/progress
Get all progress updates for a task.

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": 1,
      "userId": 2,
      "userName": "Jane Smith",
      "message": "Completed packing all items",
      "createdAt": "2024-02-10T14:00:00Z"
    }
  ]
}
```

---

### User Management Endpoints

#### GET /api/v1/users/profile
Get current user profile (requires authentication).

**Response:**
```json
{
  "success": true,
  "data": {
    "id": 1,
    "firstName": "John",
    "lastName": "Doe",
    "email": "john@example.com",
    "phoneNumber": "0123456789",
    "userType": "Both",
    "address": "123 Main St",
    "rating": 4.5,
    "completedTasks": 10,
    "isVerified": true,
    "profileCompleted": true,
    "walletBalance": 1500.00
  }
}
```

#### PUT /api/v1/users/profile
Update user profile (requires authentication).

**Request Body:**
```json
{
  "firstName": "John",
  "lastName": "Doe",
  "phoneNumber": "0123456789",
  "address": "456 New St",
  "userType": "Both"
}
```

**Response:**
```json
{
  "success": true,
  "data": {/* Updated UserDto */},
  "message": "Profile updated successfully"
}
```

#### GET /api/v1/users/dashboard/stats
Get dashboard statistics (requires authentication).

**Response:**
```json
{
  "success": true,
  "data": {
    "postedTasks": 5,
    "activeTasks": 2,
    "completedTasks": 10,
    "totalEarnings": 1500.00
  }
}
```

---

### Category Endpoints

#### GET /api/v1/categories
Get all categories (public endpoint).

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": 1,
      "name": "Cleaning",
      "description": "House cleaning, office cleaning",
      "icon": "fas fa-broom",
      "sortOrder": 1
    }
  ]
}
```

---

### Payment Endpoints

#### POST /api/v1/payment/notify
Ozow webhook for payment notifications.

**Request Body:** (Ozow IPN format)
```
m_payment_id=DFY-1707123456-7890
pf_payment_id=12345
payment_status=COMPLETE
item_name=Task Payment
amount_gross=500.00
```

**Response:**
```
HTTP 200 OK
```

**Business Rules Applied:**
- Verify Ozow signature
- Update task payment status to "Completed"
- Change task status from "PendingPayment" to "Posted"

#### GET /api/v1/payment/return
Ozow return URL after successful payment.

**Query Parameters:**
- Standard Ozow return parameters

**Response:**
Redirect to frontend success page

#### GET /api/v1/payment/cancel
Ozow cancel URL when payment is cancelled.

**Response:**
Redirect to frontend cancel page

---

### Admin Endpoints

#### GET /api/v1/admin/tasks
Get all tasks with filters (requires Admin role).

**Query Parameters:**
- `page` (default: 1)
- `pageSize` (default: 10)
- `status` (optional)
- `paymentStatus` (optional)

**Response:**
```json
{
  "success": true,
  "count": 100,
  "page": 1,
  "pageSize": 10,
  "totalPages": 10,
  "tasks": [/* TaskDto array */]
}
```

#### POST /api/v1/admin/tasks/{taskId}/verify
Verify task payment (requires Admin role).

**Response:**
```json
{
  "success": true,
  "message": "Task payment verified successfully"
}
```

#### POST /api/v1/admin/tasks/{taskId}/unverify
Unverify task payment (requires Admin role).

**Response:**
```json
{
  "success": true,
  "message": "Task payment unverified successfully"
}
```

---

## Authentication & Authorization

### JWT Token Structure

**Claims:**
- `NameIdentifier`: User ID
- `Email`: User email
- `Role`: User roles (comma-separated)

**Token Configuration:**
- Algorithm: HMAC-SHA256
- Expiry: 7 days
- Issuer: "DoForYou"
- Audience: "DoForYou"

### Authorization Levels

#### Public Endpoints
- GET /api/v1/tasks/available
- GET /api/v1/categories
- GET /api/v1/tasks/filters
- POST /api/v1/auth/register
- POST /api/v1/auth/login

#### Authenticated Endpoints
All other endpoints require valid JWT token in Authorization header:
```
Authorization: Bearer {token}
```

#### Admin Endpoints
Require "Admin" role in JWT claims:
- /api/v1/admin/*

### Password Security
- Hashing: BCrypt with automatic salt generation
- Minimum strength: Enforced by frontend
- Storage: Only hashed passwords stored in database

---

## Task Workflow

### Task Lifecycle States

```
PendingPayment → Posted → Claimed → Completed → RunnerPaid
```

### State Transitions

#### 1. PendingPayment
**Initial state after task creation**
- Task created by user
- Payment URL generated
- Waiting for payment completion

**Allowed Transitions:**
- → Posted (when payment completed)

#### 2. Posted
**Task is publicly available**
- Payment verified
- Visible in available tasks list
- Can be claimed by runners

**Allowed Transitions:**
- → Claimed (when runner claims task)

#### 3. Claimed
**Task assigned to runner**
- Runner has accepted the task
- Creator and runner can communicate
- Runner can add progress updates

**Allowed Transitions:**
- → Completed (when runner marks complete)

#### 4. Completed
**Task finished by runner**
- Waiting for creator verification
- Payment can be released to runner

**Allowed Transitions:**
- → RunnerPaid (when payment released)

#### 5. RunnerPaid
**Final state**
- Runner has been paid
- Task lifecycle complete

### Payment Status States

```
Pending → Completed
```

**Pending:**
- Initial state after task creation
- Waiting for Ozow payment

**Completed:**
- Payment verified by Ozow
- Task can be posted

### Business Rules by State

#### Creating Tasks
- User must be verified
- User must have completed profile
- Budget must be R50-R10,000
- All required fields must be provided

#### Claiming Tasks
- User cannot claim own tasks
- Task must be in "Posted" state
- Only one runner can claim a task

#### Completing Tasks
- Only the assigned runner can complete
- Task must be in "Claimed" state

#### Payment Release
- Only admin or automated system can release payment
- Task must be in "Completed" state

---

## Payment Integration

### Ozow Configuration

**Sandbox Credentials:**
- Merchant ID: `10000100`
- Merchant Key: `<merchant_key>`
- Passphrase: (optional for sandbox)

**URLs:**
- Process URL: `https://sandbox.ozow.co.za/eng/process`
- Return URL: `http://localhost:4200/tasks/payment-success`
- Cancel URL: `http://localhost:4200/tasks/payment-cancel`
- Notify URL: `http://localhost:5001/api/v1/payment/notify`

### Payment Flow

1. **Task Creation**
   - User creates task
   - System generates Ozow payment URL
   - Task status: "PendingPayment"

2. **Payment Processing**
   - User redirected to Ozow
   - User completes payment
   - Ozow sends IPN to notify URL

3. **Payment Verification**
   - System receives Ozow IPN
   - Verifies payment signature
   - Updates task payment status to "Completed"
   - Changes task status to "Posted"

4. **Payment Release (Future)**
   - After task completion
   - Admin or automated system releases payment
   - Funds transferred to runner's wallet

### Ozow Parameters

```csharp
merchant_id: Merchant ID
merchant_key: Merchant Key
return_url: Success redirect URL
cancel_url: Cancel redirect URL
notify_url: IPN webhook URL
name_first: User first name
name_last: User last name
email_address: User email
m_payment_id: Task ID (DFY-xxx)
amount: Task budget (decimal)
item_name: Task description
```

### Security Considerations
- Signature verification on IPN
- HTTPS for production
- Idempotent payment processing
- Payment status validation


---

## Data Models

### User Model
```csharp
public class User
{
    public int Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Email { get; set; }
    public string PhoneNumber { get; set; }
    public string UserType { get; set; } // "Poster", "Runner", "Both"
    public string? IdNumber { get; set; }
    public string? Address { get; set; }
    public string PasswordHash { get; set; }
    public bool IsVerified { get; set; }
    public bool ProfileCompleted { get; set; }
    public decimal Rating { get; set; }
    public int CompletedTasks { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? Roles { get; set; } // "User", "Admin"
    public DateTime? EmailVerificationExpiry { get; set; }
    public string? EmailVerificationToken { get; set; }
    public bool EmailVerified { get; set; }
    public string? PhoneVerificationCode { get; set; }
    public DateTime? PhoneVerificationExpiry { get; set; }
    public bool PhoneVerified { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Username { get; set; }
    public decimal WalletBalance { get; set; }
}
```

### Task Model
```csharp
public class Task
{
    public int Id { get; set; }
    public string TaskId { get; set; } // DFY-{timestamp}-{random}
    public string TaskDescription { get; set; }
    public string Category { get; set; }
    public string Area { get; set; }
    public DateTime DateNeeded { get; set; }
    
    [Range(50, 10000)]
    public decimal Budget { get; set; }
    
    public string? Notes { get; set; }
    public string PaymentStatus { get; set; } // "Pending", "Completed"
    public string TaskStatus { get; set; } // "PendingPayment", "Posted", "Claimed", "Completed"
    public string Priority { get; set; } // "Standard", "Urgent"
    public int CreatedByUserId { get; set; }
    public int? AcceptedByUserId { get; set; }
    public string? HelperName { get; set; }
    public string? HelperContact { get; set; }
    public string? HelperEmail { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? PaidToRunnerAt { get; set; }
    
    // Navigation properties
    public User CreatedByUser { get; set; }
    public User? AcceptedByUser { get; set; }
}
```

### Category Model
```csharp
public class Category
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

### BusinessRule Model
```csharp
public class BusinessRule
{
    public int Id { get; set; }
    public string RuleName { get; set; }
    public string RuleType { get; set; } // validation, permission, calculation, workflow
    public string Entity { get; set; } // User, Task, Payment
    public string Condition { get; set; } // JSON condition
    public string Action { get; set; } // allow, deny, validate, require
    public string? ErrorMessage { get; set; }
    public bool IsActive { get; set; }
    public int Priority { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

### Payment Model
```csharp
public class Payment
{
    public int Id { get; set; }
    public string PaymentId { get; set; }
    public int TaskId { get; set; }
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; }
    public string Status { get; set; }
    public string? PaymentUrl { get; set; }
    public string? TransactionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    
    public Task Task { get; set; }
}
```

### TaskMessage Model
```csharp
public class TaskMessage
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int SenderId { get; set; }
    public string Content { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
    
    public User Sender { get; set; }
}
```

### TaskProgressUpdate Model
```csharp
public class TaskProgressUpdate
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int UserId { get; set; }
    public string Message { get; set; }
    public DateTime CreatedAt { get; set; }
    
    public User User { get; set; }
}
```

### Rating Model
```csharp
public class Rating
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int RatedByUserId { get; set; }
    public int RatedUserId { get; set; }
    public int RatingValue { get; set; } // 1-5
    public string? Review { get; set; }
    public DateTime CreatedAt { get; set; }
    
    public Task Task { get; set; }
    public User RatedByUser { get; set; }
    public User RatedUser { get; set; }
}
```

### Dispute Model
```csharp
public class Dispute
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int ReportedByUserId { get; set; }
    public string Issue { get; set; }
    public string Category { get; set; }
    public string Status { get; set; }
    public string? Resolution { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    
    public Task Task { get; set; }
    public User ReportedByUser { get; set; }
}
```

### WalletTransaction Model
```csharp
public class WalletTransaction
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public decimal Amount { get; set; }
    public string TransactionType { get; set; } // "Credit", "Debit"
    public string Status { get; set; }
    public string? Description { get; set; }
    public string? Reference { get; set; }
    public DateTime CreatedAt { get; set; }
    
    public User User { get; set; }
}
```

---

## Complete Business Rules Reference

### User Management Rules

#### Rule: Only Verified Users Can Create Tasks
```json
{
  "RuleName": "Only Verified Users Can Create Tasks",
  "RuleType": "permission",
  "Entity": "User",
  "Condition": {
    "isVerified": true,
    "profileCompleted": true
  },
  "Action": "allow",
  "ErrorMessage": "You must complete your profile and verify your account to create tasks",
  "IsActive": true,
  "Priority": 10
}
```

#### Rule: User Type Validation
```json
{
  "RuleName": "User Must Have Valid User Type",
  "RuleType": "validation",
  "Entity": "User",
  "Condition": {
    "userType": ["Poster", "Runner", "Both"]
  },
  "Action": "validate",
  "ErrorMessage": "User type must be Poster, Runner, or Both",
  "IsActive": true,
  "Priority": 5
}
```

### Task Management Rules

#### Rule: Task Budget Range
```json
{
  "RuleName": "Task Budget Must Be Between 50 and 10000",
  "RuleType": "validation",
  "Entity": "Task",
  "Condition": {
    "budget": {
      "min": 50,
      "max": 10000
    }
  },
  "Action": "validate",
  "ErrorMessage": "Task budget must be between R50 and R10,000",
  "IsActive": true,
  "Priority": 10
}
```

#### Rule: Required Task Fields
```json
{
  "RuleName": "Task Must Have Required Fields",
  "RuleType": "validation",
  "Entity": "Task",
  "Condition": {
    "required": ["TaskDescription", "Category", "Area", "DateNeeded", "Budget"]
  },
  "Action": "validate",
  "ErrorMessage": "All required fields must be provided",
  "IsActive": true,
  "Priority": 10
}
```

#### Rule: Cannot Claim Own Task
```json
{
  "RuleName": "Users Cannot Claim Their Own Tasks",
  "RuleType": "permission",
  "Entity": "Task",
  "Condition": {
    "createdByUserId": "!=currentUserId"
  },
  "Action": "deny",
  "ErrorMessage": "You cannot claim your own task",
  "IsActive": true,
  "Priority": 10
}
```

#### Rule: Task Must Be Paid Before Posting
```json
{
  "RuleName": "Task Must Be Paid Before Posting",
  "RuleType": "workflow",
  "Entity": "Task",
  "Condition": {
    "paymentStatus": "Completed"
  },
  "Action": "require",
  "ErrorMessage": "Payment must be completed before task can be posted",
  "IsActive": true,
  "Priority": 10
}
```

### Payment Rules

#### Rule: Payment Amount Must Match Task Budget
```json
{
  "RuleName": "Payment Amount Must Match Task Budget",
  "RuleType": "validation",
  "Entity": "Payment",
  "Condition": {
    "amount": "==task.budget"
  },
  "Action": "validate",
  "ErrorMessage": "Payment amount must match task budget",
  "IsActive": true,
  "Priority": 10
}
```

### Role-Based Access Rules

#### Rule: Admin Can Access All Tasks
```json
{
  "RuleName": "Admin Can Access All Tasks",
  "RuleType": "permission",
  "Entity": "Task",
  "Condition": {
    "roles": ["Admin"]
  },
  "Action": "allow",
  "ErrorMessage": null,
  "IsActive": true,
  "Priority": 100
}
```

#### Rule: Only Admin Can Verify Payments
```json
{
  "RuleName": "Only Admin Can Verify Payments",
  "RuleType": "permission",
  "Entity": "Payment",
  "Condition": {
    "roles": ["Admin"]
  },
  "Action": "allow",
  "ErrorMessage": "Only administrators can verify payments",
  "IsActive": true,
  "Priority": 10
}
```

---

## Error Handling

### Error Response Format
```json
{
  "success": false,
  "message": "Error description",
  "error": "Detailed error message"
}
```

### HTTP Status Codes
- `200 OK`: Successful request (even for business logic failures)
- `400 Bad Request`: Invalid request format
- `401 Unauthorized`: Missing or invalid authentication
- `403 Forbidden`: Insufficient permissions
- `404 Not Found`: Resource not found
- `500 Internal Server Error`: Server error

### Error Handling Middleware
Custom middleware catches all exceptions and returns consistent error responses:

```csharp
public class ErrorHandlingMiddleware
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }
}
```

---

## Database Seeding

### Default Data

#### Categories
1. Cleaning - "House cleaning, office cleaning"
2. Handyman - "Repairs, maintenance, installations"
3. Transportation - "Moving, delivery, driving"
4. Tutoring - "Academic help, lessons"
5. Gardening - "Lawn care, landscaping"
6. Pet Care - "Pet sitting, walking, grooming"
7. Technology - "Computer help, setup, repair"
8. General - "Other miscellaneous tasks"

#### Admin User
- Email: `admin@doforyou.com`
- Password: `<set_via_env>`
- Role: Admin
- Verified: Yes

#### Test User
- Email: `<test_email>`
- Password: `<test_password>`
- Role: User
- UserType: Both
- Verified: Yes

---

## Security Best Practices

### Authentication
- JWT tokens with 7-day expiry
- Secure token storage (client-side)
- Token refresh mechanism (future enhancement)

### Password Security
- BCrypt hashing with automatic salting
- No plain text password storage
- Password strength validation (frontend)

### API Security
- CORS configured for specific origins
- HTTPS required in production
- Rate limiting (future enhancement)
- Input validation on all endpoints

### Data Protection
- SQL injection prevention via EF Core parameterization
- XSS prevention via input sanitization
- CSRF protection via token validation

### Authorization
- Role-based access control
- Resource ownership validation
- Business rules engine for fine-grained permissions

---

## Performance Considerations

### Database Optimization
- Indexed columns: Email, TaskId, UserId
- Eager loading for related entities
- Pagination for large result sets
- Connection pooling via EF Core

### Caching Strategy (Future)
- Cache categories (rarely change)
- Cache business rules (reload on change)
- Redis for distributed caching

### Query Optimization
- Select only required fields
- Use projections (Select) instead of full entities
- Avoid N+1 queries with Include

---

## Monitoring & Logging

### Logging Levels
- Information: Normal operations
- Warning: Potential issues
- Error: Exceptions and failures

### Logged Events
- User authentication attempts
- Task creation and status changes
- Payment processing
- Business rule violations
- API errors

### Console Logging
Detailed logging for development:
```csharp
Console.WriteLine($"=== LOGIN REQUEST RECEIVED ===");
Console.WriteLine($"Email: {request?.Email}");
Console.WriteLine($"Password valid: {passwordValid}");
```

---

## Testing Strategy

### Unit Tests (Recommended)
- Business rules engine logic
- Task workflow state transitions
- Payment calculations
- User authentication

### Integration Tests (Recommended)
- API endpoint responses
- Database operations
- Ozow integration
- Authentication flow

### Manual Testing
- Swagger UI for API testing
- Ozow sandbox for payment testing
- Frontend integration testing

---

## Deployment Considerations

### Environment Configuration
- Development: `appsettings.json`
- Production: Environment variables or Azure Key Vault

### Database Migration
```bash
dotnet ef migrations add MigrationName
dotnet ef database update
```

### Production Checklist
- [ ] Update JWT secret key
- [ ] Configure production database connection
- [ ] Enable HTTPS
- [ ] Update CORS origins
- [ ] Configure Ozow production credentials
- [ ] Set up logging infrastructure
- [ ] Configure rate limiting
- [ ] Set up monitoring and alerts
- [ ] Database backup strategy
- [ ] SSL certificate configuration

### Scaling Considerations
- Stateless API design (horizontal scaling ready)
- Database connection pooling
- Load balancer configuration
- CDN for static assets (future)

---

## API Versioning

### Current Version: v1
All endpoints prefixed with `/api/v1/`

### Future Versioning Strategy
- URL-based versioning: `/api/v2/`
- Maintain backward compatibility
- Deprecation notices for old versions

---

## Future Enhancements

### Planned Features
1. **Real-time Communication**
   - SignalR for live messaging
   - Push notifications

2. **Advanced Payment Features**
   - Escrow system
   - Automatic payment release
   - Refund handling

3. **Rating & Review System**
   - Mutual ratings after task completion
   - Review moderation

4. **Dispute Resolution**
   - Dispute filing and tracking
   - Admin mediation tools

5. **Analytics Dashboard**
   - Task completion metrics
   - Revenue tracking
   - User engagement analytics

6. **Mobile API Optimization**
   - Reduced payload sizes
   - Offline support
   - Push notifications

7. **Advanced Search**
   - Full-text search
   - Geolocation-based filtering
   - AI-powered task matching

8. **Wallet System Enhancement**
   - Withdrawal requests
   - Transaction history
   - Multiple payment methods

---

## Troubleshooting Guide

### Common Issues

#### Database Connection Failed
**Symptom:** Application fails to start
**Solution:** 
- Verify PostgreSQL is running
- Check connection string in `appsettings.json`
- Ensure database exists

#### JWT Token Invalid
**Symptom:** 401 Unauthorized responses
**Solution:**
- Verify token in Authorization header
- Check token expiry
- Ensure JWT secret key matches

#### Ozow Webhook Not Received
**Symptom:** Payment status not updating
**Solution:**
- Verify notify URL is publicly accessible
- Check Ozow IPN logs
- Ensure signature validation is correct

#### CORS Errors
**Symptom:** Frontend cannot access API
**Solution:**
- Verify CORS policy includes frontend origin
- Check browser console for specific error
- Ensure credentials are allowed

---

## API Documentation

### Swagger/OpenAPI
Available at: `http://localhost:5001/swagger`

### Postman Collection
Import OpenAPI spec from Swagger for Postman testing.

### Authentication in Swagger
1. Click "Authorize" button
2. Enter: `Bearer {your-jwt-token}`
3. Click "Authorize"

---

## Support & Maintenance

### Code Maintenance
- Regular dependency updates
- Security patch monitoring
- Performance profiling
- Code quality reviews

### Database Maintenance
- Regular backups
- Index optimization
- Query performance monitoring
- Data archival strategy

### Documentation Updates
- Keep API docs synchronized with code
- Update business rules documentation
- Maintain changelog

---

## Changelog

### Version 1.0.0 (Current)
- Initial release
- Complete task management system
- Ozow payment integration
- Business rules engine
- JWT authentication
- Admin panel
- Communication system
- Progress tracking

---

## Contact & Support

For technical support or questions:
- Email: support@doforyou.com
- Documentation: This file
- API Docs: http://localhost:5001/swagger

---

**Last Updated:** February 2024  
**Version:** 1.0.0  
**Author:** DoForYou Development Team
