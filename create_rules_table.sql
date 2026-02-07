-- Business Rules Table
CREATE TABLE IF NOT EXISTS "BusinessRules" (
    "Id" SERIAL PRIMARY KEY,
    "RuleName" VARCHAR(100) NOT NULL UNIQUE,
    "RuleType" VARCHAR(50) NOT NULL, -- 'validation', 'permission', 'calculation', 'workflow'
    "Entity" VARCHAR(50) NOT NULL, -- 'User', 'Task', 'Payment', etc.
    "Condition" TEXT NOT NULL, -- JSON or SQL-like condition
    "Action" TEXT NOT NULL, -- What to do when rule applies
    "ErrorMessage" TEXT, -- Message to show when rule fails
    "IsActive" BOOLEAN DEFAULT true,
    "Priority" INTEGER DEFAULT 0, -- Higher priority rules execute first
    "CreatedAt" TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    "UpdatedAt" TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- User Permission Rules
INSERT INTO "BusinessRules" ("RuleName", "RuleType", "Entity", "Condition", "Action", "ErrorMessage") VALUES
('CanCreateTasks', 'permission', 'User', '{"userType": ["creator", "both"], "profileCompleted": true, "isVerified": true}', 'allow', 'Complete your profile and verify your account to create tasks'),
('CanAcceptTasks', 'permission', 'User', '{"userType": ["runner", "both"], "profileCompleted": true}', 'allow', 'Complete your profile to accept tasks'),
('AdminCannotCreateTasks', 'permission', 'User', '{"roles": ["Admin"]}', 'deny', 'Admin users cannot create tasks'),
('MinTaskBudget', 'validation', 'Task', '{"budget": {"min": 50}}', 'validate', 'Task budget must be at least R50'),
('MaxTaskBudget', 'validation', 'Task', '{"budget": {"max": 10000}}', 'validate', 'Task budget cannot exceed R10,000'),
('RequiredTaskFields', 'validation', 'Task', '{"required": ["taskDescription", "category", "area", "dateNeeded", "budget"]}', 'validate', 'All required fields must be completed');

-- User Profile Rules
INSERT INTO "BusinessRules" ("RuleName", "RuleType", "Entity", "Condition", "Action", "ErrorMessage") VALUES
('ProfileCompletionRequired', 'validation', 'User', '{"required": ["firstName", "lastName", "email", "phoneNumber", "address", "userType"]}', 'validate', 'Complete all required profile fields'),
('ValidPhoneNumber', 'validation', 'User', '{"phoneNumber": {"pattern": "^0[0-9]{9}$"}}', 'validate', 'Phone number must be a valid South African number'),
('ValidUserType', 'validation', 'User', '{"userType": ["creator", "runner", "both"]}', 'validate', 'User type must be creator, runner, or both');

-- Task Workflow Rules
INSERT INTO "BusinessRules" ("RuleName", "RuleType", "Entity", "Condition", "Action", "ErrorMessage") VALUES
('TaskStatusProgression', 'workflow', 'Task', '{"allowedTransitions": {"PendingPayment": ["Posted", "Cancelled"], "Posted": ["Claimed", "Cancelled"], "Claimed": ["Completed", "Cancelled"], "Completed": [], "Cancelled": []}}', 'validate', 'Invalid task status transition'),
('PaymentBeforePosting', 'workflow', 'Task', '{"taskStatus": "Posted", "paymentStatus": "Completed"}', 'require', 'Payment must be completed before task can be posted'),
('CannotClaimOwnTask', 'permission', 'Task', '{"createdByUserId": "!=currentUserId"}', 'validate', 'You cannot claim your own task');