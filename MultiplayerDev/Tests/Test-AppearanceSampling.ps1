$ErrorActionPreference='Stop'
$source=Join-Path (Split-Path -Parent $PSScriptRoot) 'Source/PlayerAppearance.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source))
$visual=$tree.GetRoot().DescendantNodes() | Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'Visual'} | Select-Object -First 1
if (!$visual) { throw 'Production Visual class missing' }
$stubs=@'
using System;
using System.Collections.Generic;
using UnityEngine;
using Object=UnityEngine.Object;
namespace UnityEngine {
 public class Object {public static void Destroy(object o){}}
 public class GameObject {public Transform transform=new Transform(); public bool activeInHierarchy=true;}
 public class Transform {public string name="Root"; public Vector3 localPosition,localScale;public Quaternion localRotation;public Transform Child;public Transform Find(string name)=>name=="Pelvis"?Child:null;}
 public struct Vector3 {public float x,y,z;public Vector3(float a,float b,float c){x=a;y=b;z=c;}public static Vector3 Lerp(Vector3 a,Vector3 b,float t)=>new Vector3(a.x+(b.x-a.x)*t,a.y+(b.y-a.y)*t,a.z+(b.z-a.z)*t);}
 public struct Quaternion {public float value;public static Quaternion identity=>new Quaternion {value=1};public static float Angle(Quaternion a,Quaternion b)=>Math.Abs(a.value-b.value);public static Quaternion Slerp(Quaternion a,Quaternion b,float t)=>new Quaternion {value=a.value+(b.value-a.value)*t};}
 public static class Mathf {public static float Clamp(float v,float lo,float hi)=>Math.Max(lo,Math.Min(hi,v));public static float SmoothStep(float a,float b,float t){t=Clamp(t,0,1);return a+(b-a)*t*t*(3-2*t);}}
 public class AnimationClip {public string name;public float length=2;public Action<GameObject,float> Sample;public void SampleAnimation(GameObject obj,float at)=>Sample(obj,at);}
}
namespace NivalisTogether { public static class PlayerAppearance {
'@
$test=@'
}
public static class SamplingProbe {
 public static int Run(){
  int checks=0;
  void Check(bool v,string why){checks++;if(!v)throw new Exception(why);}
  var animator=new GameObject();var motion=new Transform();var pelvis=new Transform();var hand=new Transform();
  animator.transform.localPosition=new Vector3(2,3,4); animator.transform.localRotation=new Quaternion {value=9};
  motion.localPosition=new Vector3(0,0,0);motion.localRotation=new Quaternion {value=1};
  pelvis.localPosition=new Vector3(0,1,0);pelvis.localRotation=new Quaternion {value=3};
  var visual=new PlayerAppearance.Visual("native") {Root=new GameObject(),AnimationRoot=animator,MotionRoot=motion};
  visual.CaptureReferencePose(new[]{animator.transform,motion,pelvis,hand});
  float idleAt=-1,walkAt=-1;
  visual.Idle=new AnimationClip {name="standing",Sample=(obj,at)=>{
   idleAt=at;obj.transform.localPosition=new Vector3(20,30,40);obj.transform.localRotation=new Quaternion {value=20};
   motion.localPosition=new Vector3(50,60,70);motion.localRotation=new Quaternion {value=30};
   pelvis.localPosition=new Vector3(.1f,1.1f,.2f);pelvis.localRotation=new Quaternion {value=4};
   hand.localPosition=new Vector3(5,5,5);
  }};
  visual.Walk=new AnimationClip {name="walking",Sample=(obj,at)=>{
   walkAt=at;motion.localPosition=new Vector3(100,200,300);motion.localRotation=new Quaternion {value=100};
   pelvis.localPosition=new Vector3(.2f,1.2f,.3f);pelvis.localRotation=new Quaternion {value=5};
  }};
  visual.Sample(.1f,0);
  Check(animator.transform.localPosition.x==2 && animator.transform.localRotation.value==9,"Animator anchor remains immutable");
  Check(motion.localPosition.x==0 && motion.localPosition.y==0 && motion.localPosition.z==0 && motion.localRotation.value==1,"Authored motion root cannot shift or rotate the network avatar");
  Check(pelvis.localPosition.y==1.1f && pelvis.localRotation.value==4,"Pelvis gait is retained, not frozen as root motion");
  visual.Sample(.08f,1.4f);
  Check(pelvis.localPosition.y>1.1f && pelvis.localPosition.y<1.2f,"Standing to walking blends instead of snapping poses");
  Check(Math.Abs(walkAt-.08f)<.0001f,"Walking has its own phase rather than reusing idle time");
  visual.Sample(.1f,1.4f);
  Check(pelvis.localPosition.y==1.2f,"Transition reaches the full walking pose");
  Check(hand.localPosition.x==0 && hand.localPosition.y==0 && hand.localPosition.z==0,"New clip's unkeyed bone returns to reference pose after transition");
  visual.Sample(.02f,0);
  Check(Math.Abs(idleAt-.12f)<.0001f,"Idle phase is independent from walking");
  visual.Idle.Sample=(obj,at)=>{motion.localPosition=new Vector3(9,9,9);throw new InvalidOperationException("clip failure");};
  bool thrown=false;try{visual.Sample(.1f,0);}catch(InvalidOperationException){thrown=true;}
  Check(thrown && motion.localPosition.x==0 && motion.localRotation.value==1,"Sampling failure still restores the motion root");
  foreach(int mode in new[]{0,1,2,3,4}) {
   var body=new Transform {localPosition=new Vector3(.4f,1,-.2f)};
   var root=new Transform {Child=body,localRotation=new Quaternion {value=mode==4?2:1}};
   var leg=new Transform();var a=new GameObject();
   var v=new PlayerAppearance.Visual("humanoid") {Root=new GameObject(),AnimationRoot=a,MotionRoot=root};
   v.CaptureReferencePose(new[]{a.transform,root,body,leg});
   v.Walk=new AnimationClip {name=mode==1?"OtherWalk":"IN1_X0749. Normal Walk Forward long_m_fix_2(Clone)",Sample=(obj,at)=>{
    root.localPosition=new Vector3(0,0,mode==2?at:0);
    body.localPosition=new Vector3(.4f+at*(mode==3?.1f:1),1+at*.1f,-.2f+at*(mode==3?.1f:2));
    body.localRotation=new Quaternion {value=3+at};body.localScale=new Vector3(1,1+at*.1f,1);
    leg.localRotation=new Quaternion {value=5+at};
   }};
   v.Idle=new AnimationClip {name="standing",Sample=(obj,at)=>{body.localPosition=new Vector3(.7f,1,-.6f);}};
   v.InspectClipMotion();
   Check(body.localPosition.x==.4f && body.localPosition.z==-.2f,"Probe restores complete original pose");
   if(mode!=0) {
    Check(v.AnimationDescription.Contains("walkPelvisPlanarAnchor=False"),"Unverified clip, moving Root, small sway and rotated rig are excluded");
    v.Sample(.25f,1.4f);
    Check(body.localPosition.x>.4f,"Excluded rig retains native horizontal animation");
    continue;
   }
   Check(v.AnimationDescription.Contains("walkPelvisPlanarAnchor=True"),"Verified humanoid walk with large pelvis drift opts in");
   v.Sample(.2f,0);
   Check(body.localPosition.x==.7f && body.localPosition.z==-.6f,"Idle keeps its native body pose");
   for(int frame=0;frame<110;frame++) {
    v.Sample(.05f,1.4f);
    Check(body.localPosition.x==.4f && body.localPosition.z==-.2f,"Walking stays at nonzero bind X/Z through blending and multiple cycle wraps");
    if(frame>4)Check(body.localPosition.y>=1 && body.localRotation.value>=3 && body.localScale.y>=1 && leg.localRotation.value>=5,"Vertical gait, body rotation/scale and animated leg remain sampled");
   }
   v.Walk.Sample=(obj,at)=>{body.localPosition=new Vector3(9,1.2f,9);throw new InvalidOperationException("clip failure");};
   thrown=false;try{v.Sample(.1f,1.4f);}catch(InvalidOperationException){thrown=true;}
   Check(thrown && body.localPosition.x==.4f && body.localPosition.z==-.2f && body.localPosition.y==1.2f,"Failure still removes planar drift without freezing body height");
  }
  return checks;
 }
}
}
'@
Add-Type -TypeDefinition ($stubs+$visual.ToFullString()+$test)
"Production appearance sampling PASS $([NivalisTogether.SamplingProbe]::Run()) assertions"
