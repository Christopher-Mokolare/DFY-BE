-- Quick fix: Add escrow columns with PascalCase to match C# model
ALTER TABLE "Tasks" 
ADD COLUMN IF NOT EXISTS "CommissionPercentage" DECIMAL(5,2) DEFAULT 15.00,
ADD COLUMN IF NOT EXISTS "CommissionAmount" DECIMAL(10,2) DEFAULT 0,
ADD COLUMN IF NOT EXISTS "PayoutAmount" DECIMAL(10,2) DEFAULT 0,
ADD COLUMN IF NOT EXISTS "EscrowStatus" VARCHAR(20) DEFAULT 'none',
ADD COLUMN IF NOT EXISTS "EscrowHoldUntil" TIMESTAMP;

-- Update existing tasks with commission
UPDATE "Tasks" 
SET 
    "CommissionPercentage" = 15.00,
    "CommissionAmount" = GREATEST("Budget" * 0.15, 5.00),
    "PayoutAmount" = "Budget" - GREATEST("Budget" * 0.15, 5.00)
WHERE "CommissionAmount" = 0 OR "CommissionAmount" IS NULL;
