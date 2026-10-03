$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$path = Join-Path $gameRoot 'MultiplayerDev/Source/NpcWorldReplication.cs'
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path)).GetRoot()
function ExtractClass([string]$name) {
    $node = $tree.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq $name } | Select-Object -First 1
    if (!$node) { throw "Production class absent: $name" }
    $node.ToFullString()
}
$tests = @'
public static class NpcPlaybackProbe {
 public static readonly List<string> Metrics=new List<string>();
 public static int Run() {
  int checks=0;
  void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
  void Near(double value,double expected,double tolerance,string reason)=>Check(Math.Abs(value-expected)<=tolerance,reason+"; actual="+value+", expected="+expected);
  NpcPlayback<float> New()=>new NpcPlayback<float>((a,b,t)=>a+(b-a)*t);
  const double tick=1.0/60.0;
  var basic=New();
  Near(basic.IntervalSeconds,.5,.000001,"Initial estimated sample interval is half a second");
  Check(basic.Add(0,0),"First authoritative pose is accepted");
  Check(basic.Add(.5,.5f),"Next authoritative pose is accepted");
  Near(basic.Current,0,.000001,"Receiving a later sample does not jump the rendered pose");
  for(int i=0;i<30;i++)basic.Advance(tick);
  Near(basic.Current,.5,.0001,"Half-second confirmed motion plays over half a second");
  for(int i=0;i<120;i++)basic.Advance(tick);
  Near(basic.Current,.5,.0001,"Playback holds the last confirmed endpoint without extrapolation");
  int priorCount=basic.SampleCount;
  Check(!basic.Add(.5,100),"Duplicate source timestamps are ignored");
  Check(!basic.Add(.25,100),"Older source timestamps are ignored");
  Check(basic.SampleCount==priorCount,"Rejected samples do not change history size");
  Near(basic.Current,.5,.0001,"Rejected samples do not alter rendered position");
  foreach(double malformed in new[]{double.NaN,double.PositiveInfinity,-1d}) {
   int count=basic.SampleCount;float current=basic.Current;double interval=basic.IntervalSeconds;bool rejected=false;
   try{basic.Add(malformed,999,true);}catch(ArgumentException){rejected=true;}
   Check(rejected,"Malformed source clock is rejected even for an explicit reset");
   Check(basic.SampleCount==count&&basic.IntervalSeconds==interval,"Rejected source clocks preserve history and measured cadence");
   Near(basic.Current,current,.000001,"Malformed source clock cannot change rendered position");
  }
  var invalidDelta=New();invalidDelta.Add(0,0);invalidDelta.Add(.5,.5f);invalidDelta.Advance(tick);
  foreach(double malformed in new[]{-1d,double.NaN,double.PositiveInfinity}) {
   float current=invalidDelta.Current;int count=invalidDelta.SampleCount;
   Near(invalidDelta.Advance(malformed),current,.000001,"Invalid elapsed render time cannot advance or rewind playback");
   Check(invalidDelta.SampleCount==count,"Invalid render delta leaves pending history unchanged");
  }

  // Positions equal capture times: the source moves at exactly one metre/second.
  // Arrival cadence must change latency, not playback speed or endpoint safety.
  foreach(double cadence in new[]{.5,2.4}) {
   var playback=New();playback.Add(0,0);
   int totalTicks=(int)(cadence*18*60), next=1;double newest=0;
   float previous=0;double distance=0,measuredSeconds=0;int idleTicks=0;
   for(int frame=1;frame<=totalTicks;frame++) {
    double elapsed=frame*tick;
    while(next*cadence<=elapsed+1e-8) {
     newest=next*cadence;float before=playback.Current;
     Check(playback.Add(newest,(float)newest),"Increasing source timestamp is accepted at cadence "+cadence);
     Near(playback.Current,before,.000001,"Arrival must not snap ongoing constant movement");next++;
    }
    float current=playback.Advance(tick);
    Check(float.IsFinite(current)&&current>=previous-.00001f&&current<=newest+.0001,"Playback stays between confirmed endpoints at cadence "+cadence);
    if(elapsed>=cadence*6) {
     double step=current-previous;
     Check(step<=tick*1.05+.0001,"Regular playback does not rush through sparse snapshots at cadence "+cadence);
     distance+=step;measuredSeconds+=tick;if(step<.00001)idleTicks++;
    }
    Check(playback.SampleCount<=NpcPlayback<float>.MaxSamples,"Steady movement keeps bounded sample history");
    previous=current;
   }
   Near(distance/measuredSeconds,1,.035,"Sustained one-metre/second motion retains source speed at cadence "+cadence);
   Check(idleTicks<=3,"Valid steady source movement does not repeatedly stop between captures at cadence "+cadence);
   Near(playback.IntervalSeconds,cadence,.025,"Measured interval follows actual source cadence");
   Check(playback.FreshnessSeconds>=cadence*1.9,"Animation freshness covers ordinary sparse source updates");
  }

  var hitch=New();hitch.Add(0,0);hitch.Add(.5,.5f);
  for(int i=0;i<6;i++)hitch.Advance(tick);
  float beforeHitch=hitch.Current;float afterHitch=hitch.Advance(1);
  Check(afterHitch>beforeHitch&&afterHitch-beforeHitch<=NpcPlayback<float>.MaxAdvanceSeconds+.0001,"One-second frame hitch advances at most the capped playback time");
  Check(afterHitch<=.5,"Frame hitch cannot overshoot the newest endpoint");
  Near(hitch.Advance(0),afterHitch,.000001,"Zero frame time leaves playback unchanged");
  for(int i=0;i<120;i++)hitch.Advance(tick);
  Near(hitch.Current,.5,.0001,"After a hitch the confirmed target is still reachable");

  // Delayed packets sometimes arrive together. Receive calls must not each
  // rewrite the rendered pose; ordinary ticks still progress monotonically.
  var jitter=New();jitter.Add(0,0);
  double[] arrivals={.58,1.08,1.74,1.75,2.72,3.05,3.51,4.3,4.31,5.17};
  int arrived=0;float previousJitter=0;double latest=0;
  for(int frame=1;frame<=480;frame++) {
   double elapsed=frame*tick;
   while(arrived<arrivals.Length&&arrivals[arrived]<=elapsed) {
    latest=(arrived+1)*.5;float before=jitter.Current;
    Check(jitter.Add(latest,(float)latest),"Jittered increasing sample is accepted");
    Near(jitter.Current,before,.000001,"A network burst cannot teleport the rendered character");arrived++;
   }
   float current=jitter.Advance(tick);
   Check(current>=previousJitter-.00001f&&current<=latest+.0001,"Jittered playback is monotonic and cannot extrapolate");
   Check(jitter.SampleCount<=NpcPlayback<float>.MaxSamples,"Jitter cannot grow history without bound");previousJitter=current;
  }
  Near(jitter.Current,5,.0001,"All delayed movement eventually reaches its final confirmed endpoint");

  var overflow=New();overflow.Add(0,0);overflow.Add(.5,.5f);
  for(int i=0;i<6;i++)overflow.Advance(tick);
  float beforeOverflow=overflow.Current;
  for(int i=2;i<=40;i++) {
   Check(overflow.Add(i*.5,i*.5f),"New samples remain acceptable while history compacts");
   Check(overflow.SampleCount<=NpcPlayback<float>.MaxSamples,"Overflow keeps a strict history bound");
   Near(overflow.Current,beforeOverflow,.000001,"Compaction preserves the currently rendered pose");
  }
  float firstAfterOverflow=overflow.Advance(tick);
  Check(firstAfterOverflow>beforeOverflow&&firstAfterOverflow<20,"Overflow resumes continuously from Current rather than jumping to the latest position");
  float lastOverflow=firstAfterOverflow;
  for(int i=0;i<1500;i++) {
   float current=overflow.Advance(tick);
   Check(current>=lastOverflow-.0001f&&current<=20.0001f,"Compacted history remains monotonic and endpoint bounded");
   Check(current-lastOverflow<=tick*NpcPlayback<float>.MaxPlaybackRate+.00001,"Overflow recovery stays within the bounded catch-up rate");lastOverflow=current;
  }
  Near(overflow.Current,20,.0001,"Compaction retains the newest authoritative target");

  Check(overflow.Add(30,-20,true),"Explicit map/view discontinuity accepts a reset");
  Near(overflow.Current,-20,.000001,"Only an explicit discontinuity snaps directly to its new pose");
  Check(overflow.SampleCount<=1,"Reset discards the old map's interpolation history");
  Check(overflow.Add(30.5,-19.5f),"Post-reset movement accepts fresh timestamps");
  Near(overflow.Current,-20,.000001,"Post-reset packet reception does not snap again");
  for(int i=0;i<30;i++)overflow.Advance(tick);
  Near(overflow.Current,-19.5,.0001,"Post-reset playback does not blend through the previous map");

  // Frame stalls advance the real source clock but may not be replayed as one
  // visual jump. Continued ordinary frames must repay their accumulated lag.
  void SimulateHitches(double cadence,double hitchSeconds,bool repeated) {
   var playback=New();playback.Add(0,0);
   double elapsed=0,nextHitch=repeated?2:20,limit=repeated?120:90,newest=0;
   double earlyLag=0,earlyWeight=0,lateLag=0,lateWeight=0,maxWarmLag=0,maxRate=0,maxStep=0;
   int nextSample=1,hitches=0;float previous=0;
   while(elapsed<limit-1e-8) {
    double delta=tick;
    if(elapsed+tick>=nextHitch-1e-8) {delta=hitchSeconds;hitches++;nextHitch=repeated?nextHitch+2:double.PositiveInfinity;}
    delta=Math.Min(delta,limit-elapsed);elapsed+=delta;
    while(nextSample*cadence<=elapsed+1e-8) {
     newest=nextSample*cadence;float before=playback.Current;
     Check(playback.Add(newest,(float)newest),"Hitch simulation accepts increasing source samples");
     Near(playback.Current,before,.000001,"A packet arriving after a hitch cannot change rendered position directly");nextSample++;
    }
    float current=playback.Advance(delta);double step=current-previous;
    Check(step>=-.00003&&current<=newest+.00003,"Hitch recovery never reverses or extrapolates beyond confirmed positions");
    Check(step<=NpcPlayback<float>.MaxAdvanceSeconds+.00003,"Repeated hitches cannot create a visual step larger than the per-frame cap");
    Check(step<=delta*NpcPlayback<float>.MaxPlaybackRate+.00003,"Hitch recovery never exceeds the permitted catch-up speed");
    Check(playback.SampleCount<=NpcPlayback<float>.MaxSamples,"Long hitch runs retain bounded presentation history");
    double lag=elapsed-current;maxRate=Math.Max(maxRate,step/delta);maxStep=Math.Max(maxStep,step);
    if(elapsed>=30)maxWarmLag=Math.Max(maxWarmLag,lag);
    if(elapsed>=(repeated?40:10)&&elapsed<=(repeated?60:18)+1e-8){earlyLag+=lag*delta;earlyWeight+=delta;}
    if(elapsed>=(repeated?100:80)){lateLag+=lag*delta;lateWeight+=delta;}
    previous=current;
   }
   Check(hitches==(repeated?60:1),"The simulation actually exercises every scheduled hitch");
   double firstMean=earlyLag/earlyWeight,lastMean=lateLag/lateWeight,finalLag=elapsed-playback.Current;
   if(repeated) {
    double bound=cadence*1.85+.25;
    Check(maxWarmLag<=bound&&finalLag<=bound,"Repeated hitches keep source-time lag bounded for cadence "+cadence+", hitch "+hitchSeconds+"; maximum="+maxWarmLag+", final="+finalLag+", bound="+bound);
    Check(lastMean<=firstMean+.25,"Source lag settles instead of growing throughout 120 seconds; cadence="+cadence+", hitch="+hitchSeconds+", firstMean="+firstMean+", lastMean="+lastMean);
   } else {
    Check(lastMean<=firstMean+.12,"Playback recovers its ordinary latency after a finite one-second hitch; cadence="+cadence+", before="+firstMean+", recovered="+lastMean);
    Check(finalLag<=cadence+.18,"Finite hitch does not leave permanent source-time debt");
   }
   Metrics.Add("cadence="+cadence.ToString("F1")+" hitch="+hitchSeconds.ToString("F2")+" repeated="+repeated+" earlyLag="+firstMean.ToString("F3")+" lateLag="+lastMean.ToString("F3")+" finalLag="+finalLag.ToString("F3")+" maxWarmLag="+maxWarmLag.ToString("F3")+" maxRate="+maxRate.ToString("F3")+" maxStep="+maxStep.ToString("F5"));
  }
  Near(NpcPlayback<float>.MaxPlaybackRate,1.15,.000001,"Catch-up rate remains limited to fifteen percent above source speed");
  foreach(double cadence in new[]{.5,1,2.4}) {
   foreach(double duration in new[]{.1,.15,.2})SimulateHitches(cadence,duration,true);
   SimulateHitches(cadence,1,false);
  }
  return checks;
 }
}
'@
$source = 'using System; using System.Collections.Generic; namespace NivalisTogether.Dedicated {'
$source += ExtractClass 'NpcPlayback'
$source += $tests + '}'
Add-Type -TypeDefinition $source
"NPC timestamped playback actual production class PASS $([NivalisTogether.Dedicated.NpcPlaybackProbe]::Run()) assertions"
[NivalisTogether.Dedicated.NpcPlaybackProbe]::Metrics
