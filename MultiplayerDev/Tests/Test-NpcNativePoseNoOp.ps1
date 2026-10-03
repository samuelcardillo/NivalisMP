$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $devRoot 'Source/NpcWorldReplication.cs'))).GetRoot()
$method=@($tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'ApplyNativePose'})
$apply=@($tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'ApplyState'})
if($method.Count -ne 1 -or $apply.Count -ne 1){throw 'Production native pose helper/caller absent.'}
$source=@'
using System;
namespace NpcNativePoseNoOp {
 public struct Vector3 {public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}}
 public struct Quaternion {public float x,y,z,w;public Quaternion(float x,float y,float z,float w){this.x=x;this.y=y;this.z=z;this.w=w;}}
 public sealed class AgentGhost {
  public Vector3 RawPosition;public Quaternion RawRotation;public int PositionReads,RotationReads,PositionWrites,RotationWrites;
  public bool PositionChangesRotation;
  public Vector3 Position{get{PositionReads++;return RawPosition;}set{PositionWrites++;RawPosition=value;if(PositionChangesRotation)RawRotation=new Quaternion(0,0,1,0);}}
  public Quaternion Rotation{get{RotationReads++;return RawRotation;}set{RotationWrites++;RawRotation=value;}}
  public void ClearCounts(){PositionReads=RotationReads=PositionWrites=RotationWrites=0;}
 }
 public static class Probe {
'@+$method[0].ToFullString()+@'
 public static int Run(){
  int checks=0;void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
  var position=new Vector3(1,2,3);var rotation=new Quaternion(.1f,.2f,.3f,.9f);
  var ghost=new AgentGhost{RawPosition=position,RawRotation=rotation};
  for(int i=0;i<100;i++)ApplyNativePose(ghost,position,rotation);
  Check(ghost.PositionWrites==0&&ghost.RotationWrites==0,"Identical snapshots never repeat spatial-index/rotation setters");
  Check(ghost.PositionReads==100&&ghost.RotationReads==100,"Every snapshot freshly observes native pose; no cached incoming-state shortcut");
  for(int field=0;field<7;field++){
   ghost.RawPosition=position;ghost.RawRotation=rotation;ghost.ClearCounts();
   if(field==0)ghost.RawPosition.x=MathF.BitIncrement(position.x);
   if(field==1)ghost.RawPosition.y=MathF.BitIncrement(position.y);
   if(field==2)ghost.RawPosition.z=MathF.BitIncrement(position.z);
   if(field==3)ghost.RawRotation.x=MathF.BitIncrement(rotation.x);
   if(field==4)ghost.RawRotation.y=MathF.BitIncrement(rotation.y);
   if(field==5)ghost.RawRotation.z=MathF.BitIncrement(rotation.z);
   if(field==6)ghost.RawRotation.w=MathF.BitIncrement(rotation.w);
   ApplyNativePose(ghost,position,rotation);
   Check(ghost.PositionWrites==(field<3?1:0)&&ghost.RotationWrites==(field<3?0:1),"One-ULP local drift is repaired only through the changed setter: "+field);
   ghost.ClearCounts();ApplyNativePose(ghost,position,rotation);
   Check(ghost.PositionWrites==0&&ghost.RotationWrites==0,"Corrected native pose is freshly verified without repeat writes: "+field);
  }
  ghost.RawPosition=new Vector3(99,2,3);ghost.RawRotation=rotation;ghost.PositionChangesRotation=true;ghost.ClearCounts();
  ApplyNativePose(ghost,position,rotation);
  Check(ghost.PositionWrites==1&&ghost.RotationWrites==1&&ghost.RawRotation.w==rotation.w,"Rotation is observed after native Position side effects");
  ghost.PositionChangesRotation=false;ghost.RawPosition=new Vector3(float.NaN,2,3);ghost.RawRotation=new Quaternion(float.NaN,.2f,.3f,.9f);ghost.ClearCounts();
  ApplyNativePose(ghost,position,rotation);
  Check(ghost.PositionWrites==1&&ghost.RotationWrites==1,"Invalid current native values cannot compare equal to authoritative finite values");
  ghost.RawPosition=position;ghost.RawRotation=new Quaternion(-rotation.x,-rotation.y,-rotation.z,-rotation.w);ghost.ClearCounts();
  ApplyNativePose(ghost,position,rotation);
  Check(ghost.PositionWrites==0&&ghost.RotationWrites==1,"Equivalent quaternion orientation still restores exact authoritative components");
  return checks;
 }
 }
}
'@
Add-Type -TypeDefinition $source
$checks=[NpcNativePoseNoOp.Probe]::Run()
$body=$apply[0].ToFullString()-replace '\s+',' '
foreach($fragment in @('if (changeActive)','if (changeViewless)','if (changeScene)','GhostManager.TransferGhostToScene(ghost, state.Scene, new PositionRotation(position, rotation))','else ApplyNativePose(ghost, position, rotation)','if (changeScene || changeActive || changeViewless || !ghost.HasLoadedView)')){
    if(!$body.Contains($fragment)){throw "Required native transfer/presence wiring changed: $fragment"}
}
"PASS: $checks production fresh-native NPC pose checks plus 6 transfer/presence wiring guards; no native calls."
