$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$source=Join-Path $devRoot 'Source/TravelReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source))
$nodes=$tree.GetRoot().DescendantNodes()
function Read-Class([string]$Name){$match=@($nodes|Where-Object{$_-is[Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax]-and$_.Identifier.ValueText-eq$Name});if($match.Count-ne1){throw "Expected one actual class $Name"};$match[0].ToFullString()}
function Method([string]$Name){$match=@($nodes|Where-Object{$_-is[Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]-and$_.Identifier.ValueText-eq$Name});if($match.Count-ne1){throw "Expected one actual method $Name"};$match[0].ToFullString()}
$classes=@('GroupTravelEvent','GroupTravelIntent','GroupTravelAcknowledgement','GroupTravelBarrier')|ForEach-Object{Read-Class $_}
$members=@('PlayerOperation','DeparturePose','PoseSample')|ForEach-Object{Read-Class $_}
$methodNames=@('Handle','Handles','RevalidateOperation','SetPlayerPresent','UpdatePlayerPose','OperationEvent','CancelOperation','FailOperation','Standalone','Copy','Remember','ValidateIntent','ValidTravelMode','Bounded','Cut','FindPortal','FindTransition','FindBoatPortal','SamePortalIdentity','ValidateDestination','ValidatePublicMapDestination')
$members+=@($methodNames|ForEach-Object{Method $_})
$fixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'TravelDepartureLeaseTests.cs'))
$fixture=$fixture.Replace('/*PRODUCTION_TYPES*/',($classes-join[Environment]::NewLine)).Replace('/*PRODUCTION_MEMBERS*/',($members-join[Environment]::NewLine))
# Reuse only the native asset facades from the existing portal-selection tests.
$portalTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'TravelPortalResolutionTests.cs')))
$facades=@($portalTree.GetRoot().Members|Where-Object{$_-is[Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax]}|ForEach-Object ToFullString)-join[Environment]::NewLine
Add-Type -TypeDefinition ($fixture+[Environment]::NewLine+$facades)
$count=[NivalisTogether.Dedicated.TravelReplication]::RunDepartureTests()
[pscustomobject]@{Utc=[DateTime]::UtcNow.ToString('o');Passed=$true;Assertions=$count;SourceHash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash;ActualMethods=$methodNames;Scope='Actual production operation/ACK lifecycle and immutable lease, using detached native asset and membership facades; no game runtime or installation.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'travel-departure-lease-results.json')
"PASS $count actual production departure-lease assertions."
