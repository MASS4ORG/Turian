; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
TUR0002 | Gameplay | Error | Component async code must return an observed result
TUR0003 | Gameplay | Warning | Gameplay waits use the game clock
TUR0004 | Gameplay | Warning | Shared async results use Task fields
TURDDD001 | DataAssetDrivenDesign | Error | DataAsset handlers match their typed event members
TURDDD002 | DataAssetDrivenDesign | Error | Typed event definitions use valid stable identities
