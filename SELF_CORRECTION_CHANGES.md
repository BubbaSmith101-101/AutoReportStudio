# Self-Correction Feature - Implementation Changes

## Summary of Changes

This document outlines all code changes made to implement **LLM Self-Correction** for hallucinated columns.

---

## 1. SchemaFormatterService.cs (ENHANCED)

### Added Method: `CreateCorrectionPrompt()`

**Purpose**: Creates a detailed prompt to send to LLM explaining what went wrong and what to fix

**Signature**:
```csharp
public static string CreateCorrectionPrompt(
	string originalSql,
	string sectionHeading,
	List<string> halluccinatedColumns,
	DbSchema schema)
```

**Returns**: A formatted string that looks like:
```
=== SQL CORRECTION NEEDED ===
Section: [Section Name]

The following columns DO NOT EXIST in the database and must be corrected:
  ❌ e.[OfficeName]

Available columns in the tables being used:
  [dbo].[Employee]:
	- [EmployeeID] (int)
	- [FirstName] (nvarchar(max))
	- [OfficeID] (int)

ORIGINAL SQL (with errors):
[SQL HERE]

Please rewrite the SQL above...
```

### Added Method: `ExtractTableNamesFromSql()`

**Purpose**: Extracts table names from SQL to show available columns for those specific tables

**Signature**:
```csharp
private static List<string> ExtractTableNamesFromSql(string sql)
```

**Returns**: List of table names used in the SQL

---

## 2. NEW FILE: LLMCorrectionService.cs

Complete new service that handles the self-correction loop.

### Interface: `ILLMCorrectionService`

```csharp
public interface ILLMCorrectionService
{
	Task<SectionPlan?> CorrectHallucinations(
		SectionPlan originalPlan,
		DbSchema schema,
		List<string> halluccinatedColumns,
		string userRequest,
		string? selectedModel = null,
		CancellationToken ct = default);
}
```

### Class: `LLMCorrectionService`

**Key Fields**:
- `logger` - ILogger<LLMCorrectionService>
- `httpClient` - HttpClient for direct API calls to Ollama
- `configuration` - IConfiguration for Ollama settings
- `MaxCorrectionAttempts` = 3

**Key Methods**:

1. **CorrectHallucinations()** - Main entry point
   - Loops up to 3 times
   - Each iteration: create prompt → call LLM → validate
   - Returns corrected plan or null

2. **GenerateCorrectionFromLLM()** - Calls Ollama API directly
   - Uses HttpClient to POST to `/api/generate`
   - Passes correction prompt
   - Returns LLM response

3. **ValidateSQL()** - Re-validates corrected SQL
   - Uses same regex logic as ReportService
   - Returns list of remaining hallucinated columns

4. **ExtractSqlFromResponse()** - Parses LLM output
   - Handles markdown code blocks
   - Extracts SELECT/WITH statements
   - Returns clean SQL

---

## 3. ReportService.cs (SIGNIFICANT CHANGES)

### Constructor Change

**Before**:
```csharp
public ReportService(ISchemaService s, IOllamaService o, ILogger<ReportService> l)
```

**After**:
```csharp
public ReportService(ISchemaService s, IOllamaService o, ILLMCorrectionService c, ILogger<ReportService> l)
{
	schema = s;
	ollama = o;
	correction = c;  // NEW
	logger = l;
}
```

### ValidateAndFixHallucinations() Method

**Changed from**:
- Synchronous method
- Only detected hallucinations
- Immediately disabled sections

**Changed to**:
- Asynchronous method (`async Task`)
- Takes additional parameters: `selectedModel`, `CancellationToken`
- Two-pass approach:
  1. **First pass**: Identifies all sections with hallucinations
  2. **Second pass**: Attempts correction for each
- Only disables sections after correction fails

**New Flow**:
```csharp
// First pass: detect hallucinations
foreach (section in plan.Sections)
{
	// Same regex validation logic
	if (invalidColumns.Count > 0)
		sectionsToCorrect.Add((index, section, invalidColumns));
}

// Second pass: attempt corrections
foreach (var (index, section, invalidColumns) in sectionsToCorrect)
{
	var correctedSection = await correction.CorrectHallucinations(
		section, db, invalidColumns, request, selectedModel, ct);

	if (correctedSection != null)
		plan.Sections[index] = correctedSection;  // Use corrected
	else
		plan.Sections[index].Sql = "";  // Disable if correction failed
}
```

### Generate() Method Change

**Before**:
```csharp
ValidateAndFixHallucinations(plan, db, logger);
```

**After**:
```csharp
await ValidateAndFixHallucinations(plan, db, selectedModel, ct);
```

**Also added**: Logging message about validation and correction:
```csharp
logger.LogInformation(
	"Validating generated SQL for hallucinated columns and attempting LLM self-correction...");
```

---

## 4. Program.cs (REGISTRATION)

### Added Service Registration

**Before**:
```csharp
builder.Services.AddScoped<ISchemaService, SchemaService>();
builder.Services.AddScoped<IOllamaService, OllamaService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IExcelService, ExcelService>();
```

**After**:
```csharp
builder.Services.AddScoped<ISchemaService, SchemaService>();
builder.Services.AddScoped<IOllamaService, OllamaService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<ILLMCorrectionService, LLMCorrectionService>();  // NEW
builder.Services.AddScoped<IExcelService, ExcelService>();
```

---

## 5. No Changes to OllamaService.cs

The OllamaService was **NOT modified** for this feature. The LLMCorrectionService calls Ollama API directly via HttpClient, which is simpler and more efficient.

---

## Data Flow

### Before (Direct Disable)

```
LLM generates plan
	↓
ValidateAndFixHallucinations() (sync)
	- Detects hallucinations
	- Logs error
	- Disables section immediately
	↓
Section has empty SQL
```

### After (With Self-Correction)

```
LLM generates plan (await)
	↓
ValidateAndFixHallucinations() (async/await)
	├─ First pass: Detect hallucinations
	├─ Second pass: For each hallucination:
	│   ├─ ILLMCorrectionService.CorrectHallucinations() (await)
	│   │   ├─ Loop up to 3 times:
	│   │   │   ├─ Create correction prompt
	│   │   │   ├─ Call LLM API (await)
	│   │   │   ├─ Extract and validate SQL
	│   │   │   └─ Success? Return corrected plan
	│   │   └─ Max retries exceeded? Return null
	│   ├─ If corrected: Use new plan
	│   └─ If failed: Disable section
	↓
Section has corrected SQL OR empty SQL if correction failed
```

---

## Interface & Dependency Injection

### Before

```
ReportService
	├─ Depends on: ISchemaService
	├─ Depends on: IOllamaService
	└─ Depends on: ILogger<ReportService>
```

### After

```
ReportService
	├─ Depends on: ISchemaService
	├─ Depends on: IOllamaService
	├─ Depends on: ILLMCorrectionService (NEW)
	└─ Depends on: ILogger<ReportService>

ILLMCorrectionService
	├─ Depends on: ILogger<LLMCorrectionService>
	├─ Depends on: HttpClient
	└─ Depends on: IConfiguration
```

---

## Async/Await Changes

### ValidateAndFixHallucinations()

**Before**:
```csharp
private void ValidateAndFixHallucinations(ReportPlan plan, DbSchema db, ILogger<ReportService> logger)
```

**After**:
```csharp
private async Task ValidateAndFixHallucinations(ReportPlan plan, DbSchema db, string? selectedModel, CancellationToken ct)
```

**Reason**: Must await LLMCorrectionService.CorrectHallucinations() which returns `Task<SectionPlan?>`

### Call Site in Generate()

**Before**:
```csharp
ValidateAndFixHallucinations(plan, db, logger);
```

**After**:
```csharp
await ValidateAndFixHallucinations(plan, db, selectedModel, ct);
```

---

## Configuration Requirements

No new configuration files needed. Uses existing `appsettings.json`:

```json
{
  "Ollama": {
	"BaseUrl": "http://localhost:11434",
	"Model": "qwen3-coder:30b"
  }
}
```

The LLMCorrectionService reads these values:
```csharp
var baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
var model = selectedModel ?? configuration["Ollama:Model"] ?? "qwen3-coder:30b";
```

---

## Logging Changes

### New Log Messages

**Level: Information**
```
Starting LLM self-correction for section '{SectionHeading}' with {HallucColumnCount} hallucinated columns
Attempting LLM self-correction for section '{SectionHeading}' with {HallucColumnCount} hallucinated columns
Correction attempt {AttemptNumber}/{MaxAttempts} for section '{SectionHeading}'
LLM provided corrected SQL for section '{SectionHeading}'
Correction successful for section '{SectionHeading}' on attempt {AttemptNumber}
Successfully corrected section '{SectionHeading}'
```

**Level: Warning**
```
Hallucinated column detected: Section '{SectionHeading}' references {InvalidColumn}
Corrected SQL still contains {HallucColumnCount} hallucinated columns. Retrying...
LLM returned empty correction response for section '{SectionHeading}'
```

**Level: Error**
```
LLM correction failed after max attempts for section '{SectionHeading}'. Disabling section.
Failed to correct hallucinations in section '{SectionHeading}' after {MaxAttempts} attempts. Giving up.
Error during LLM correction attempt {AttemptNumber} for section '{SectionHeading}': {ErrorMessage}
Exception during LLM self-correction for section '{SectionHeading}': {ErrorMessage}
LLM API returned status {StatusCode}
Error calling LLM for correction: {ErrorMessage}
```

**Level: Debug**
```
Sending correction prompt to LLM for section '{SectionHeading}'
Correction prompt: {CorrectionPrompt}
LLM correction response: {Response}
```

---

## Error Handling Strategy

### Design Pattern

```csharp
try
{
	// Attempt correction
}
catch (Exception ex)
{
	logger.LogError(ex, "Error message");
	// Treat as failed attempt, continue to next retry
}

if (attemptNumber >= MaxCorrectionAttempts)
{
	// Give up and return null
	logger.LogError("Failed after {MaxAttempts} attempts");
	return null;
}
```

### Exception Scenarios Handled

1. **Timeout**: HttpClient timeout during LLM call → Log and retry
2. **Connection refused**: Ollama service down → Log and retry
3. **Invalid JSON**: Malformed LLM response → Log and retry
4. **Empty response**: LLM returns null/empty → Log and retry
5. **General exception**: Any other error → Log and retry

**In all cases**: After 3 failed attempts, return null and section is disabled

---

## Testing Considerations

### Unit Tests Needed

1. **SchemaFormatterService.CreateCorrectionPrompt()**
   - Test with various hallucinated columns
   - Verify format and content

2. **LLMCorrectionService.ValidateSQL()**
   - Test valid SQL (no errors)
   - Test SQL with hallucinations
   - Test edge cases (CTEs, subqueries)

3. **LLMCorrectionService.ExtractSqlFromResponse()**
   - Test markdown code blocks
   - Test plain SQL
   - Test with extra text around SQL

4. **LLMCorrectionService.CorrectHallucinations()**
   - Mock HttpClient
   - Test max retries logic
   - Test success on attempt 1, 2, 3

### Integration Tests Needed

1. End-to-end: Generate report with hallucinated SQL → Corrected
2. Max retries: Verify stops after 3 attempts
3. Partial failure: One section corrected, one disabled

---

## Backward Compatibility

**Status**: ✅ Fully backward compatible

- No breaking changes to interfaces
- No changes to database schema
- No changes to existing API contracts
- ValidateAndFixHallucinations() is internal (private)
- New functionality is transparent to callers

---

## Performance Impact

### Timing

- **No hallucinations**: No impact (validation only, <10ms)
- **With hallucinations (corrected on attempt 1)**: +5-30 seconds (LLM call time)
- **With hallucinations (failed)**: +15-90 seconds (3 × LLM call time)

### Resource Usage

- **CPU**: Low (mostly waiting for LLM)
- **Memory**: Minimal (just string/object allocations)
- **Network**: One or more HTTP calls to Ollama per hallucination

### Database

- No impact (queries validated before execution)

---

## Configuration Tuning

### Adjust Retry Attempts

**File**: `LLMCorrectionService.cs`, Line ~26

```csharp
private const int MaxCorrectionAttempts = 3;
```

**Recommended values**:
- `1`: No retries (fast but less forgiving)
- `2`: One retry (good balance)
- `3`: Two retries (current default, recommended)
- `4+`: Too many (diminishing returns)

### Adjust Model for Corrections

Uses same model as initial generation. Can't override without code change.

**To use different model for corrections**:
1. Extend IConfiguration or create new config section
2. Modify LLMCorrectionService to read it
3. Pass to GenerateCorrectionFromLLM()

---

## Summary of Changes

| File | Type | Changes |
|------|------|---------|
| `SchemaFormatterService.cs` | Enhanced | Added `CreateCorrectionPrompt()`, `ExtractTableNamesFromSql()` |
| `LLMCorrectionService.cs` | New | Entire new service for self-correction loop |
| `ReportService.cs` | Modified | Constructor injection, async ValidateAndFixHallucinations(), integration point |
| `Program.cs` | Modified | Service registration for ILLMCorrectionService |
| `OllamaService.cs` | No change | - |

**Total lines added**: ~400-500  
**Complexity**: Medium (async/await, retry logic, regex)  
**Breaking changes**: None  
**New dependencies**: None (HttpClient already available)

---

## Deployment Checklist

- [ ] All files modified as documented
- [ ] Program.cs service registration added
- [ ] Project compiles without errors
- [ ] No compilation warnings introduced
- [ ] Tests passing
- [ ] Appsettings.json has Ollama configuration
- [ ] HttpClient is registered in DI (required by LLMCorrectionService)
- [ ] Ready for production deployment

