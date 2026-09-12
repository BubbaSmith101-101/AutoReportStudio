# LLM Self-Correction Feature

## Overview

When the LLM generates SQL with hallucinated columns, instead of immediately disabling the section, the application now:

1. **Detects** the hallucinated columns
2. **Informs the LLM** of the specific problem with available columns listed
3. **Requests correction** from the LLM
4. **Validates** the corrected SQL
5. **Retries up to 3 times** if hallucinations persist
6. **Gracefully disables** the section only after max retries exceeded

## Architecture

### Components

```
ReportService.Generate()
	↓
	1. ValidateAndFixHallucinations() [async]
		↓
		2. Detects hallucinated columns via regex validation
		↓
		3. For each section with hallucinations:
			↓
			4. ILLMCorrectionService.CorrectHallucinations()
				↓
				5. Loop up to 3 times:
					a. SchemaFormatterService.CreateCorrectionPrompt()
					b. Call LLM API directly with correction prompt
					c. Extract SQL from LLM response
					d. Re-validate for hallucinations
					e. If valid → Return corrected plan
					f. If still invalid → Try again
				↓
				6. Return corrected plan OR null (if max retries exceeded)
		↓
		7. If correction successful → Use corrected SQL
		8. If correction failed → Disable section with error message
```

### Class Diagram

```
ReportService
	├─ Inject: ILLMCorrectionService
	├─ Method: ValidateAndFixHallucinations()
	│   └─ Calls: ILLMCorrectionService.CorrectHallucinations()
	│
LLMCorrectionService : ILLMCorrectionService
	├─ Field: HttpClient (for direct LLM API calls)
	├─ Field: MaxCorrectionAttempts = 3
	├─ Method: CorrectHallucinations()
	│   ├─ Calls: SchemaFormatterService.CreateCorrectionPrompt()
	│   ├─ Calls: GenerateCorrectionFromLLM()
	│   ├─ Calls: ExtractSqlFromResponse()
	│   └─ Calls: ValidateSQL()
	├─ Method: GenerateCorrectionFromLLM()
	│   └─ Direct HTTP call to Ollama API
	├─ Method: ValidateSQL()
	│   └─ Regex validation of columns
	└─ Method: ExtractSqlFromResponse()
		└─ Parse SQL from markdown or plain text

SchemaFormatterService
	├─ Method: CreateCorrectionPrompt()
	│   └─ Formats detailed correction request for LLM
	└─ Method: ExtractTableNamesFromSql()
		└─ Identifies tables being used
```

## Flow Diagram

### Happy Path (Correction Succeeds)

```
LLM generates SQL with hallucinations
	↓
ValidateAndFixHallucinations() detects: e.[OfficeName] is invalid
	↓
Create correction prompt showing:
  - What was wrong: e.[OfficeName] doesn't exist
  - What's available: [EmployeeID], [FirstName], [LastName], [OfficeID]
  ↓
Call LLM with correction prompt
	↓
LLM returns corrected SQL using e.[OfficeID] instead
	↓
Validate corrected SQL → No hallucinations!
	↓
✅ Use corrected SQL in report
```

### Unhappy Path (Correction Fails)

```
LLM generates SQL with hallucinations
	↓
Attempt 1 correction → LLM suggests another wrong column
	↓
Attempt 2 correction → LLM still gets it wrong
	↓
Attempt 3 correction → LLM still can't fix it
	↓
MaxCorrectionAttempts (3) exceeded
	↓
🚫 Disable section with error message
✅ Other valid sections still execute
```

## Usage

### For Developers

The self-correction is **automatic**. No code changes needed. Just register the service in `Program.cs`:

```csharp
builder.Services.AddScoped<ILLMCorrectionService, LLMCorrectionService>();
```

### For Users

As a user, you won't see the correction attempts. You'll see:
- ✅ **Best case**: Report section works (LLM self-corrected)
- ⚠️ **Fallback case**: Section disabled with reason (correction failed)

### Configuration

The feature uses existing Ollama configuration:
- `Ollama:BaseUrl` - URL to Ollama API (default: http://localhost:11434)
- `Ollama:Model` - Model to use for corrections (default: same as main model)

## Detailed Process

### Step 1: Detection

ReportService detects hallucinations by:
1. Parsing SQL for table aliases: `[schema].[table] AS alias`
2. Parsing column references: `alias.[column]`
3. Looking up each column in actual schema
4. Collecting invalid column names

Example:
```sql
SELECT e.[OfficeName] FROM [dbo].[Employee] e
		 ↑
	  Invalid!
```

### Step 2: Correction Prompt Creation

SchemaFormatterService creates a detailed prompt:

```
=== SQL CORRECTION NEEDED ===
Section: Top 20 Attendance Records

The following columns DO NOT EXIST in the database and must be corrected:
  ❌ e.[OfficeName]

Available columns in the tables being used:
  [dbo].[Employee]:
	- [EmployeeID] (int)
	- [FirstName] (nvarchar(max))
	- [LastName] (nvarchar(max))
	- [OfficeID] (int)

ORIGINAL SQL (with errors):
SELECT TOP (20) e.[EMPID] AS [EmployeeID], e.[FirstName] + N' ' + e.[LastName] AS [EmployeeName], 
e.[OfficeName] AS [Office], ea.[AttendanceDate], est.[StatusName] AS [Status]
FROM [dbo].[EmployeeAttendance] ea
...

Please rewrite the SQL above, replacing all hallucinated columns with valid columns from the available columns listed above.
IMPORTANT: Use ONLY the column names shown above. Do not invent or guess column names.
```

### Step 3: LLM Correction

LLMCorrectionService:
1. Calls Ollama API directly with the correction prompt
2. Passes the same model used for initial generation
3. Awaits corrected SQL from LLM
4. Extracts SQL from response (handles markdown code blocks)

### Step 4: Re-validation

Corrected SQL is re-validated:
- Same column validation logic as initial detection
- If no hallucinations found → Done!
- If hallucinations still present → Retry with updated list

### Step 5: Retry Logic

```
if (validationErrors.Count == 0)
	return correctedSection;  // Success!

halluccinatedColumns = validationErrors;  // Try again with new list
// Loop continues until MaxCorrectionAttempts reached
```

## Error Handling

### Scenario 1: LLM Returns Empty Response
- Log warning
- Continue to next retry attempt

### Scenario 2: LLM Returns Non-SQL
- Extract SQL from markdown or plain text
- Validate extracted SQL
- If invalid, retry

### Scenario 3: LLM Still Generates Hallucinations
- Log warning with count of remaining invalid columns
- Retry with updated hallucination list
- After 3 attempts, give up

### Scenario 4: Exception During Correction
- Log error with full exception
- Continue to next retry attempt
- After 3 attempts, disable section

## Logging

### Log Messages Generated

**Starting Correction**:
```
INFO: Starting LLM self-correction for section 'Top 20 Attendance Records' with 1 hallucinated columns
```

**Each Attempt**:
```
INFO: Correction attempt 1/3 for section 'Top 20 Attendance Records'
DEBUG: Sending correction prompt to LLM for section 'Top 20 Attendance Records'
DEBUG: Correction prompt: === SQL CORRECTION NEEDED ===...
DEBUG: LLM correction response: SELECT...
INFO: LLM provided corrected SQL for section 'Top 20 Attendance Records'
```

**Success**:
```
INFO: Correction successful for section 'Top 20 Attendance Records' on attempt 1
```

**Failure**:
```
WARN: Corrected SQL still contains 1 hallucinated columns. Retrying...
ERROR: Failed to correct hallucinations in section 'Top 20 Attendance Records' after 3 attempts. Giving up.
ERROR: LLM correction failed after max attempts for section 'Top 20 Attendance Records'. Disabling section.
```

## Testing

### Test Case 1: Single Hallucination → Corrected on First Attempt

**Input SQL**:
```sql
SELECT e.[OfficeName] FROM [dbo].[Employee] e
```

**Expected Result**:
- LLM corrects to: `e.[OfficeID]` or joins to Office table
- Section uses corrected SQL
- Logs show "Correction successful on attempt 1"

### Test Case 2: Multiple Hallucinations → Partially Corrected

**Input SQL**:
```sql
SELECT e.[OfficeName], e.[DepartmentTitle] FROM [dbo].[Employee] e
```

**Expected Result**:
- LLM corrects some but not all
- Second attempt fixes remaining
- Logs show "Correction successful on attempt 2"

### Test Case 3: Persistent Hallucination → Disabled

**Input SQL**:
```sql
SELECT e.[NonExistentColumn123] FROM [dbo].[Employee] e
```

**Expected Result**:
- Attempts 1, 2, 3 all fail (no similar column name)
- Section disabled after 3 attempts
- Logs show "Failed to correct hallucinations...after 3 attempts"
- Section Purpose updated: "[DISABLED: Failed to correct...after multiple attempts]"

### Test Case 4: LLM API Down → Fallback

**Scenario**: Ollama API is unreachable

**Expected Result**:
- Exception caught
- Retry 3 times
- Section disabled
- Logs show exception details

### Test Case 5: Only Wrong Column Corrected → Still Has Hallucinations

**Input SQL**:
```sql
SELECT e.[OfficeName], e.[FakeColumn] FROM [dbo].[Employee] e
```

**Attempt 1**: LLM corrects to `e.[OfficeID]` but still has `e.[FakeColumn]`

**Attempt 2**: LLM corrects to different column but still misses something

**Attempt 3**: Gives up

**Expected Result**: Section disabled after max attempts

## Configuration Examples

### appsettings.json

```json
{
  "Ollama": {
	"BaseUrl": "http://localhost:11434",
	"Model": "qwen3-coder:30b"
  }
}
```

## Performance Considerations

### Correction Adds Time

**First-time cost per hallucination**:
- Create correction prompt: 1ms
- Call LLM API: 5-30 seconds (depends on model)
- Extract and validate SQL: 5ms
- **Total per attempt**: 5-30 seconds

**Retry scenarios**:
- Best case (corrected on attempt 1): 1 LLM call
- Worst case (3 attempts): 3 LLM calls = 15-90 seconds additional

### Mitigation

- Only corrects sections with hallucinations (not all sections)
- Limited to 3 attempts (prevents infinite loops)
- Runs same LLM as initial generation (no additional model load)
- Runs sequentially in report generation (no parallel overhead)

## Monitoring

### Metrics to Track

1. **Correction Success Rate**:
   - Definition: Sections corrected successfully / total sections attempted
   - Target: >80%
   - Log message: "Correction successful on attempt X"

2. **Correction Attempts**:
   - Log entry: "Correction attempt X/3 for section..."
   - Track distribution: attempt 1, 2, or 3

3. **Rollback Rate**:
   - Sections disabled due to failed correction
   - Log message: "Failed to correct hallucinations...after 3 attempts"

4. **Time Impact**:
   - Additional time during report generation
   - Track in structured logs with timing

### Example Monitoring Query

```
Filter logs for: "Starting LLM self-correction" OR "Correction successful" OR "Failed to correct"
Calculate: success_count / total_correction_attempts
```

## Troubleshooting

### Issue: Corrections Failing Repeatedly

**Symptoms**: Logs show "Failed to correct hallucinations" frequently

**Causes**:
1. LLM has systematic misunderstanding of schema
2. Column names too similar to real names (e.g., `OfficeName` vs `OfficeID`)
3. LLM model not capable enough (consider upgrading model)

**Solutions**:
1. Include more detailed schema in LLM system prompt (add examples)
2. Add explicit warnings: "Do NOT add 'Name' to end of ID fields"
3. Use better model (e.g., `qwen3-coder:70b` instead of 30b)

### Issue: Correction Takes Too Long

**Symptoms**: Report generation slow when corrections needed

**Causes**:
1. LLM model slow
2. All sections have hallucinations (3 corrections × 3 attempts = 9 LLM calls)
3. Ollama server overloaded

**Solutions**:
1. Reduce MaxCorrectionAttempts to 2 (faster failure)
2. Disable corrections for non-critical sections
3. Use faster model
4. Increase Ollama server resources

### Issue: LLM Still Generates Same Hallucination

**Symptoms**: Correction prompt shows available columns, but LLM still repeats hallucination

**Causes**:
1. LLM not reading prompt carefully
2. Model has low reasoning capability
3. Hallucinated column similar to real column

**Solutions**:
1. Make correction prompt even more explicit with examples
2. Use stricter model instructions
3. Consider using retrieval-augmented generation (RAG) for schema

## Advanced: Tuning MaxCorrectionAttempts

### Adjust in Code

**File**: `LLMCorrectionService.cs`

```csharp
private const int MaxCorrectionAttempts = 3;  // Change this value
```

### Guidance

- **Value = 1**: Fast failure, no retries (not recommended)
- **Value = 2**: Good balance (attempt + 1 retry)
- **Value = 3**: Generous (current default) - allows 2 retries
- **Value = 4+**: Too slow, diminishing returns

## Future Enhancements

### Priority 1: Smarter Correction Prompt

Enhance `CreateCorrectionPrompt()` to:
- Show example transformations: "WRONG: [OfficeName], RIGHT: [OfficeID]"
- Highlight most likely correct column
- Provide column similarity matching

### Priority 2: Partial Correction

Allow partial corrections:
- If LLM corrects 50% of hallucinations, accept and use it
- Track partially corrected sections separately

### Priority 3: Machine Learning

Track which hallucinations are hardest to fix:
- "OfficeName → OfficeID" always fails on first attempt
- Pre-correct known patterns before asking LLM

### Priority 4: Alternative Correction Strategies

1. **Regex replacement**: Use regex to suggest replacements
2. **Fuzzy matching**: Find closest matching column
3. **Hybrid**: First try regex/fuzzy, then LLM if fails

## Summary

The **LLM Self-Correction** feature provides:

- ✅ Automatic detection of hallucinated columns
- ✅ Intelligent correction attempts (up to 3)
- ✅ Clear error messages when correction fails
- ✅ Graceful degradation (partial report if some sections fail)
- ✅ Comprehensive logging for monitoring
- ✅ Zero configuration needed (works out of the box)
- ✅ Prevents wasted resources (stops after max attempts)

This bridges the gap between immediate failure and lazy acceptance, allowing the LLM to learn from its mistakes while maintaining stability and predictable behavior.
