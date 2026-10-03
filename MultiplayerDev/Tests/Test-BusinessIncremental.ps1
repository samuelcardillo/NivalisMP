$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $devRoot 'Source/BusinessStateReplication.cs'))).GetRoot()
$owner=$tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'BusinessStateReplication'}
$nested=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in @('ApplyProfile','BusinessClientCursor','PreparedBusinessUpdate')}|ForEach-Object ToFullString)-join "`n"
$methods=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in @('CommitClientUnit','RememberClientAlias','ApplyClientPeopleUnit')}|ForEach-Object ToFullString)-join "`n"
$fixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'BusinessIncrementalTests.cs')).Replace('/*PRODUCTION*/',$nested+$methods)
$sources=@($fixture,[IO.File]::ReadAllText((Join-Path $devRoot 'Source/PreparedWorldUpdate.cs')),[IO.File]::ReadAllText((Join-Path $devRoot 'Shared/ClientMutationBudget.cs')))
Add-Type -TypeDefinition ("#pragma warning disable 0649,0169,0414`n"+(($sources|ForEach-Object {$_ -replace '(?m)^using System;\r?\n','' -replace '(?m)^using System.Diagnostics;\r?\n',''}) -join "`n"))
"PASS: $([NivalisTogether.Dedicated.BusinessIncrementalTests]::Run()) actual business incremental cursor, atomic rollback, publication, cancellation and alias assertions."
