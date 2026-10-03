$ErrorActionPreference='Stop'
$source=Join-Path (Split-Path -Parent $PSScriptRoot) 'Source/PlayerReplicaRenderer.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source))
$policy=$tree.GetRoot().DescendantNodes() | Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'RemoteLocomotion'} | Select-Object -First 1
if (!$policy) { throw 'Production RemoteLocomotion class missing' }
$test=@'
public static class RemoteLocomotionProbe {
 public static int Run() {
  int checks=0;
  void Check(bool ok,string description){checks++;if(!ok)throw new Exception(description);}
  var motion=new RemoteLocomotion();
  motion.Observe(0,0,0,true);
  for(int frame=1;frame<=120;frame++) {
   float t=frame/60f;
   if(frame%6==0) {motion.Observe(3*t,0,t,false);motion.Observe(3*t,0,t,false);}
   float v=motion.Tick(t,1/60f);
   if(frame>30) Check(v>1.5f && v<4.5f,"Regular 10 Hz walking must remain continuous, including duplicate packets");
  }
  for(int frame=121;frame<=180;frame++) {
   float t=frame/60f;
   if(frame%6==0)motion.Observe(6,0,t,false);
   motion.Tick(t,1/60f);
  }
  Check(motion.Tick(3,0)==0,"Stopping returns to standing");
  for(int frame=181;frame<=240;frame++) {
   float t=frame/60f;
   motion.Observe(6+(frame%2==0?.001f:0),0,t,false);
   Check(motion.Tick(t,1/60f)==0,"Submillimetre pose noise must not animate walking");
  }
  motion.Observe(100,100,4.1f,false);
  Check(motion.Tick(4.1f,.1f)==0,"A teleport must not cause a locomotion burst");
  motion.Observe(101,100,4.25f,false);
  motion.Tick(4.25f,.15f);
  motion.Observe(101,100,4.3f,true);
  Check(motion.Tick(4.3f,.1f)==0,"Scene/inactive reset clears motion history");
  motion.Observe(104,100,6,false);
  Check(motion.Tick(6,.25f)==0,"A stale connection resumes without a false sprint");
  return checks;
 }
}
'@
Add-Type -TypeDefinition ('using System; namespace NivalisTogether {'+$policy.ToFullString()+$test+'}')
"Remote locomotion PASS $([NivalisTogether.RemoteLocomotionProbe]::Run()) assertions"
