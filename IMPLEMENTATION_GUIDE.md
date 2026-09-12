# Implementation Guide: SQL Hallucination Prevention

## Overview

A comprehensive, multi-layered solution has been implemented to prevent the LLM from generating SQL queries with non-existent column names (hallucinations).

## Problem Statement

The LLM was generating SQL that referenced columns that don't exist in the database, such as:
```sql
SELECT e.[OfficeName] FROM [dbo].[Employee] e  -- ERROR: OfficeName doesn't exist!
```

This caused runtime failures with messages like: `Invalid column name 'OfficeName'`

## Solution Architecture

### Layer 1: Schema Context for LLM (SchemaFormatterService)
**Purpose**: Provide the LLM with explicit, detailed schema information to prevent hallucinations at generation time

**File**: `AutoReportStudio.Web/Services/SchemaFormatterService.cs` (NEW)

**Key Methods**:
- `FormatSchemaForLLMPrompt(DbSchema schema)` - Returns a formatted schema string suitable for including in LLM prompts
- `ValidateQueryColumns(string sql, DbSchema schema)` - Utility for post-generation validation

**Format Example**:
```
=== DATABASE SCHEMA ===
The database contains the following tables and columns ONLY.
You MUST use ONLY these exact column names. Do NOT invent or hallucinate column names.

TABLE: [dbo].[Employee]
COLUMNS:
  - [EmployeeID] int [PRIMARY KEY] [NOT NULL]
  - [FirstName] nvarchar(max) [NULLABLE]
  - [LastName] nvarchar(max) [NULLABLE]
  - [OfficeID] int [NOT NULL]

TABLE: [dbo].[Office]
COLUMNS:
  - [OfficeID] int [PRIMARY KEY] [NOT NULL]
  - [Name] nvarchar(max) [NOT NULL]
...

=== CRITICAL RULES ===
1. ONLY use columns that are explicitly listed above
2. If a column name you want to use is not listed, use one that IS listed
3. Do NOT create or assume columns that don't exist
4. Always qualify column names with table aliases: alias.[ColumnName]
5. Match column names EXACTLY as shown
```

**Integration Point**: 
In `ReportService.Generate()`, before calling `ollama.Plan()`:
```csharp
var schemaDescription = SchemaFormatterService.FormatSchemaForLLMPrompt(db);
logger.LogInformation("AI Schema Context: {SchemaDescription}", schemaDescription);
```

> **TODO**: This schema string should be included in the LLM system prompt when calling `ollama.Plan()`

---

### Layer 2: Pre-Execution Validation (ReportService.ValidateAndFixHallucinations)
**Purpose**: Catch hallucinated columns BEFORE SQL execution

**File**: `AutoReportStudio.Web/Services/ReportService.cs`

**Method**: `private void ValidateAndFixHallucinations(ReportPlan plan, DbSchema db, ...)`

**What It Does**:
1. Parses generated SQL using regex to extract:
   - Table aliases from `FROM` and `JOIN` clauses
   - Column references in the form `alias.[ColumnName]`

2. For each column reference found:
   - Looks up the table using the alias
   - Verifies the column exists in that table
   - If not found, adds it to the invalid list

3. If invalid columns are found:
   - **Clears the SQL** to prevent execution
   - **Updates section metadata** with error message
   - **Logs detailed errors** with available columns

**Example Output**:
```
ERROR: HALLUCINATED COLUMN: Section 'Top 20 Attendance Records' 
references non-existent column e.[OfficeName] in table [dbo].[Employee]. 
Available columns: [EmployeeID], [FirstName], [LastName], [OfficeID], ...

ERROR: DISABLING SECTION: 'Top 20 Attendance Records' 
contains hallucinated columns: e.[OfficeName]
```

**Call Point**:
```csharp
public async Task<ReportResult> Generate(...) 
{
	var plan = await ollama.Plan(db, request, selectedModel, ct);

	// CRITICAL: Validate before execution
	ValidateAndFixHallucinations(plan, db, logger);

	// Sections with hallucinated columns now have empty SQL
	// They will be skipped during execution
}
```

---

### Layer 3: Server-Side SQL Validator (SqlValidator in OllamaService)
**Purpose**: Additional validation layer in case issues slip through

**File**: `AutoReportStudio.Web/Services/OllamaService.cs`

**Enhancement**: Modified `SqlValidator.ValidateAndRepairPlan()`
- Now collects all hallucinated columns
- Logs detailed diagnostics
- Clears SQL if hallucinations can't be auto-repaired
- Provides list of available columns for each table

---

## Data Flow

```
1. User initiates report generation
   ↓
2. Schema discovery: ISchemaService.Discover()
   ↓
3. Schema formatting: SchemaFormatterService.FormatSchemaForLLMPrompt()
   └─> Logged for audit trail
   ↓
4. LLM call: ollama.Plan()
   └─> [FUTURE] Schema should be included in system prompt here
   ↓
5. Plan validation: ReportService.ValidateAndFixHallucinations()
   ├─ Parses SQL
   ├─ Validates each column reference
   └─ Disables sections with hallucinations
   ↓
6. Sections with valid SQL execute normally
   Sections with hallucinations are skipped with error message
```

---

## Key Features

### ✅ Automatic Detection
All generated SQL queries are automatically validated against the actual database schema

### ✅ Explicit Error Reporting  
When hallucinations are detected:
- Section is disabled (SQL cleared)
- User sees error in report: `[DISABLED: Hallucinated columns detected - e.[OfficeName]]`
- Logs show exactly what went wrong and what columns should have been used

### ✅ Column Name Matching
Uses case-insensitive matching to handle SQL identifier casing variations

### ✅ Relationship Awareness
Validates column references against the correct table using alias mapping

### ✅ Non-Breaking
Invalid sections are disabled gracefully - valid sections still execute

---

## Configuration

### Current Configuration
No configuration files need to be modified. The system works with default settings.

### Recommended Enhancement: Include Schema in LLM Prompt

**File to modify**: `AutoReportStudio.Web/Services/OllamaService.cs` (in the `Plan` method)

**Goal**: Include the formatted schema in the system prompt sent to the LLM

**Pseudo-code example**:
```csharp
public async Task<ReportPlan> Plan(DbSchema db, string userRequest, string? model = null, ...)
{
	var schemaContext = SchemaFormatterService.FormatSchemaForLLMPrompt(db);

	var systemPrompt = $@"You are an SQL query generator...

{schemaContext}

Important: You MUST ONLY use the columns listed above. Do not invent columns.";

	// Include systemPrompt when calling the LLM API...
}
```

---

## Testing

### Test Case 1: Hallucinated Column Detection
**Scenario**: Force an LLM-generated query with a non-existent column
**Expected**: Section is disabled with clear error message
**Verification**: Check logs for "HALLUCINATED COLUMN" messages

### Test Case 2: Valid SQL Still Works
**Scenario**: Generate report with correct column usage
**Expected**: Report executes normally, all sections return data
**Verification**: No disabling messages, data appears in report

### Test Case 3: Mixed Valid/Invalid
**Scenario**: Plan has 3 sections, 1 uses hallucinated columns
**Expected**: 2 sections work, 1 is disabled
**Verification**: Report shows 2 sections with data, 1 with disabled message

### Test Case 4: Column Name Case Sensitivity
**Scenario**: LLM uses `[firstname]` instead of `[FirstName]`
**Expected**: Column is still found (case-insensitive matching)
**Verification**: Report executes successfully

---

## Troubleshooting

### Issue: Sections still showing "hallucinated columns" errors
**Check**: 
1. Look at logs for "HALLUCINATED COLUMN" messages
2. Verify the table structure matches what's in the database
3. Confirm schema discovery is running correctly

### Issue: Too many sections being disabled
**Causes**:
1. LLM is consistently generating wrong column names
2. Schema not included in LLM system prompt yet (TODO)
3. Column naming patterns the LLM doesn't understand

**Solution**: See "Recommended Enhancement" section above - include schema in prompt

### Issue: False Positives (Valid columns marked as hallucinated)
**Likely cause**: Schema not properly refreshed or alias mapping issue
**Actions**:
1. Check logs for alias mapping details
2. Verify column names match exactly (including spaces, special characters)
3. Check for CTEs (Common Table Expressions) in the SQL

---

## Files Modified/Created

| File | Change | Type |
|------|--------|------|
| `SchemaFormatterService.cs` | NEW | New service for schema formatting |
| `ReportService.cs` | MODIFIED | Added ValidateAndFixHallucinations() method |
| `OllamaService.cs` | MODIFIED | Enhanced SqlValidator.ValidateAndRepairPlan() |

---

## Performance Impact

**Minimal**: 
- Schema formatting: One string build per report (logged, not executed)
- Validation: Regex parsing of SQL queries (very fast)
- Zero database impact - runs before any SQL execution

---

## Future Enhancements

1. **Regeneration on hallucination**: Request LLM to regenerate just the failed section
2. **Column suggestions**: When invalid columns found, suggest closest matching valid columns
3. **Learning**: Track hallucinations to improve future prompts
4. **Schema caching**: Cache formatted schema between requests
5. **Relationship hints**: Provide explicit table relationship information in schema context

---

## Success Criteria

The fix is working when:
1. ✅ Hallucinated columns are detected before SQL execution
2. ✅ Sections with hallucinations are disabled gracefully
3. ✅ User receives clear error messages with available columns
4. ✅ Valid sections continue to work normally
5. ✅ No invalid SQL reaches the database

