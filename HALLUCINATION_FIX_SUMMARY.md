# SQL Hallucination Prevention Fix

## Problem
The LLM was generating SQL queries that referenced columns that don't exist in the database schema, causing queries to fail with errors like:
```
Invalid column name 'OfficeName'
```

Example of hallucinated query:
```sql
SELECT TOP (20) e.[EMPID] AS [EmployeeID], e.[FirstName] + N' ' + e.[LastName] AS [EmployeeName], 
e.[OfficeName] AS [Office], ea.[AttendanceDate], est.[StatusName] AS [Status]
FROM [dbo].[EmployeeAttendance] ea
INNER JOIN [dbo].[Employee] e ON ea.[EmployeeID] = e.[EmployeeID]
...
```

The column `OfficeName` does not exist in the `Employee` table (only `OfficeID` exists, which references the `Office` table).

## Root Causes

1. **Insufficient Schema Context**: The LLM prompt did not include a detailed, explicit list of ALL actual columns in each table
2. **No Pre-execution Validation**: Generated SQL was executed without first validating that all referenced columns actually exist
3. **Repair-Only Approach**: The system only tried to repair invalid columns when it couldn't find approximate matches, rather than preventing execution of invalid queries

## Solution - Multi-Layered Approach

### 1. Enhanced Schema Formatting (SchemaFormatterService.cs)
Created a new service that formats the database schema in a very explicit way for LLM inclusion:
- Lists ALL tables with ONLY their actual columns
- Specifies column data types, nullability, and constraints
- Includes explicit rules forbidding hallucination
- Provides relationship information for JOIN understanding

```csharp
public static string FormatSchemaForLLMPrompt(DbSchema schema)
{
	// Formats schema as:
	// TABLE: [dbo].[Employee]
	// COLUMNS:
	//   - [EmployeeID] int [PRIMARY KEY] [NOT NULL]
	//   - [FirstName] nvarchar(max) [NULLABLE]
	//   - [LastName] nvarchar(max) [NULLABLE]
	//   - [OfficeID] int [NOT NULL]
	// ... and explicit rules about only using these columns
}
```

### 2. Strict Pre-Execution Validation (ReportService.ValidateAndFixHallucinations)
Added a validation method that:
- Parses generated SQL to extract table aliases and column references
- Checks EACH column reference against the actual schema
- **DISABLES** any query section that contains hallucinated columns
- Logs detailed error messages showing what's wrong and what columns actually exist
- Prevents invalid queries from being executed against the database

### 3. Enhanced SQL Validator (OllamaService.SqlValidator)
Modified the existing SqlValidator to:
- Collect all hallucinated columns (not just try to repair them)
- Log errors with available columns when hallucinations are detected  
- Clear the SQL and mark the section as failed if hallucinations can't be mapped to real columns

## Implementation Details

### Files Modified

1. **AutoReportStudio.Web/Services/SchemaFormatterService.cs** (NEW)
   - `FormatSchemaForLLMPrompt()` - Formats schema for LLM prompts
   - `ValidateQueryColumns()` - Utility for schema validation
   -Designed to be included in LLM prompts to prevent hallucination

2. **AutoReportStudio.Web/Services/ReportService.cs**
   - Added `ValidateAndFixHallucinations()` method
   - Calls schema formatting before plan generation
   - Validates generated plans before execution
   - Completely disables sections with hallucinated columns

3. **AutoReportStudio.Web/Services/OllamaService.cs**
   - Enhanced `SqlValidator.ValidateAndRepairPlan()` method
   - Now collects and logs all hallucinated columns
   - Clears SQL if hallucinations can't be repaired
   - Logs detailed column availability information

## Flow Diagram

```
User Request
	↓
Schema Discovery → SchemaFormatterService.FormatSchemaForLLMPrompt()
	↓ (Schema context should be included in LLM prompt)
LLM generates ReportPlan with SQL
	↓
ValidateAndFixHallucinations() checks EVERY column reference
	↓
	├─ Column exists in schema? → Keep it
	└─ Hallucinated column? → DISABLE section, log error
	↓
Only valid SQL is executed
```

## Expected Behavior After Fix

### Before Fix
```
Query failed: Invalid column name 'OfficeName'
(Query still attempted to execute with hallucinated columns)
```

### After Fix
```
ERROR: HALLUCINATED COLUMN: Section 'Top 20 Attendance Records' references 
non-existent column e.[OfficeName] in table [dbo].[Employee]. 
Available columns: [EmployeeID], [FirstName], [LastName], [OfficeID], ...

DISABLING SECTION: 'Top 20 Attendance Records' contains hallucinated columns: e.[OfficeName]

Section shows: "DISABLED: Hallucinated columns detected - e.[OfficeName]"
```

## Testing Recommendations

1. **Test Case: Office/Employee Relationship**
   - Request: Attendance report for employees by office
   - Expected: System should NOT try to use `OfficeName` directly from Employee table
   - Instead: Should JOIN to Office table and use Office.[Name]

2. **Test Case: Multiple Hallucinations**
   - Request: Complex report forcing multiple hallucinated columns
   - Expected: System logs each one and disables the section

3. **Test Case: Mixed Valid/Invalid**
   - Request: Query with some valid and some hallucinated columns
   - Expected: System identifies invalid ones specifically

## Configuration Recommendations

For optimal results:
1. Include the `SchemaFormatterService.FormatSchemaForLLMPrompt()` output in the LLM system prompt
2. Add specific instruction: "You MUST ONLY use columns that are explicitly listed above"
3. Consider adding examples of correct column usage in the prompt

## Future Improvements

1. **Post-Generation Regeneration**: If hallucinations are detected, request the LLM to regenerate just that section
2. **Schema Caching**: Cache formatted schema to avoid recomputing it
3. **Column Suggestion**: When hallucinations are detected, suggest the most similar column names
4. **Relationship Hints**: Provide more explicit guidance about which tables should be joined and why
5. **Fuzzy Matching Repair**: Allow minor typos in column names to be auto-corrected
