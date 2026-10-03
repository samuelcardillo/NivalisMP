$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$session=[IO.File]::ReadAllText((Join-Path $devRoot 'Source/DedicatedSession.cs'))
$initializer=[regex]::Match($session,'clientMutationBudget\s*=\s*(new ClientMutationBudget\([^;]+\));')
if(!$initializer.Success){throw 'Session mutation budget initializer was not found.'}
$create=$initializer.Groups[1].Value.Replace('Stopwatch.Frequency','1000000')
$budget=[IO.File]::ReadAllText((Join-Path $devRoot 'Shared/ClientMutationBudget.cs'))
$fixture=@'
namespace NivalisTogether.Dedicated {
 public static class ClientMutationThroughputTests {
  private static int checks;
  private static void Check(bool value,string reason){checks++;if(!value)throw new System.Exception(reason);}
  private static ClientMutationBudget SessionBudget()=>CREATE;
  private static int CompleteRoster(ClientMutationBudget budget){
   int units=0,frames=0;long now=0;
   while(units<900){
    budget.BeginFrame(++frames);int before=units;
    while(units<900&&budget.TryStart(now)){units++;now+=12;}
    Check(units>before,"Pending roster always advances on a fresh frame");
    now+=40000;
   }
   return frames;
  }
  public static int Run(){
   var budget=SessionBudget();budget.BeginFrame(1);int grants=0;
   while(budget.TryStart(0))grants++;
   Check(grants==256,"Session grants at most256 cheap atomic units");
   budget.BeginFrame(1);Check(!budget.TryStart(0),"Nested pump cannot reset the same frame action cap");
   budget.BeginFrame(2);Check(budget.TryStart(10000),"Next frame resumes pending work");
   Check(budget.TryStart(13999),"Work just before4ms still fits the shared time budget");
   Check(!budget.TryStart(14000),"No extra action at4ms even with254 count grants unused");
   budget.BeginFrame(2);Check(!budget.TryStart(15000),"Later delivery cannot reset an elapsed same-frame deadline");
   budget.BeginFrame(3);Check(budget.TryStart(20000)&&!budget.TryStart(30000),"One slow native action still prevents another same-frame action");
   int revised=CompleteRoster(SessionBudget()),previous=CompleteRoster(new ClientMutationBudget(1000000));
   Check(revised==4&&previous==29,"900 cheap12us units finish in4 frames rather than29 without changing unit size");
   return checks;
  }
 }
}
'@
Add-Type -TypeDefinition ($budget+"`n"+$fixture.Replace('CREATE',$create))
"PASS: $([NivalisTogether.Dedicated.ClientMutationThroughputTests]::Run()) actual session budget configuration, bounded throughput, deadline and same-frame fairness assertions."
