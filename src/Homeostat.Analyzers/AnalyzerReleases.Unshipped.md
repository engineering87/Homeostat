; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
HOMEO001 | Homeostat | Warning | HomeostatAnalyzer, inline action returns a task that is never awaited
HOMEO002 | Homeostat | Warning | HomeostatAnalyzer, condition does not select its signals
HOMEO003 | Homeostat | Warning | HomeostatAnalyzer, healing action ignores its cancellation token
HOMEO004 | Homeostat | Info | HomeostatAnalyzer, endpoints mapped without authorization
HOMEO005 | Homeostat | Warning | HomeostatAnalyzer, restarting a plain BackgroundService stops the application
