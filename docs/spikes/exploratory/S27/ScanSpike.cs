// Isolated source-audit microbenchmark ONLY. Not production; not gameplay proof.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;
using Verse;
class ScanSpike
{
 static bool TextureStub(ref Texture2D __result) { __result=null;return false; }
 static bool LogStub() { return false; }
 static object Shell(Type t) { return FormatterServices.GetUninitializedObject(t); }
 static void Field(object x,string n,object v) { AccessTools.Field(x.GetType(),n).SetValue(x,v); }
 static Pawn Pawn() { return (Pawn)Shell(typeof(Pawn)); }
 static LogEntry Entry(Type t,Pawn a,Pawn b,Pawn original=null) {
  LogEntry e=(LogEntry)Shell(t);
  Field(e,"ticksAbs",100000);
  if(t==typeof(BattleLogEntry_MeleeCombat)){ Field(e,"initiator",a);Field(e,"recipientPawn",b); }
  else{Field(e,"initiatorPawn",a);Field(e,"recipientPawn",b);if(t==typeof(BattleLogEntry_RangedImpact))Field(e,"originalTargetPawn",original);}
  return e;
 }
 static bool Strong(LogEntry e,Pawn candidate,Pawn player,int startAbs) {
  if(e==null||e.Timestamp<startAbs)return false;
  Type t=e.GetType();
  bool ordinary=t==typeof(BattleLogEntry_MeleeCombat)||t==typeof(BattleLogEntry_RangedFire)||t==typeof(BattleLogEntry_ExplosionImpact);
  if(!ordinary&&t!=typeof(BattleLogEntry_RangedImpact))return false;
  Pawn first=null,second=null,third=null;int count=0;
  foreach(Thing thing in e.GetConcerns()){
   Pawn p=thing as Pawn;if(p==null)return false;
   if(count==0)first=p;else if(count==1)second=p;else if(count==2)third=p;else return false;
   count++;
  }
  if(ordinary&&count!=2)return false;
  if(!ordinary&&(count!=3||!ReferenceEquals(second,third)))return false;
  if(ReferenceEquals(first,second))return false;
  return ReferenceEquals(first,candidate)&&ReferenceEquals(second,player)||ReferenceEquals(second,candidate)&&ReferenceEquals(first,player);
 }
 static int Scan(BattleLog log,Pawn[] candidates,Pawn player) {
  int promoted=0;
  foreach(Pawn candidate in candidates){bool found=false;int n=Math.Min(32,log.Battles.Count);
   for(int i=0;i<n&&!found;i++){var entries=log.Battles[i].Entries;int m=Math.Min(128,entries.Count);
    for(int j=0;j<m;j++)if(Strong(entries[j],candidate,player,90000)){found=true;break;}
   }if(found)promoted++;
  }return promoted;
 }
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
 static void Main(){
  Harmony h=new Harmony("TheNetwork.Isolated.S27.SourceSpike");
  h.Patch(AccessTools.Method(typeof(ContentFinder<Texture2D>),"Get"),prefix:new HarmonyMethod(typeof(ScanSpike),"TextureStub"));
  foreach(var m in typeof(Log).GetMethods(BindingFlags.Static|BindingFlags.Public))if(m.Name=="Error"||m.Name=="Warning"||m.Name=="Message"||m.Name=="ErrorOnce"||m.Name=="WarningOnce")h.Patch(m,prefix:new HarmonyMethod(typeof(ScanSpike),"LogStub"));
  Pawn p=Pawn(),a=Pawn(),b=Pawn(),c=Pawn();
  Check(Strong(Entry(typeof(BattleLogEntry_MeleeCombat),a,p),a,p,90000),"candidate/player melee");
  Check(!Strong(Entry(typeof(BattleLogEntry_MeleeCombat),a,b),a,p,90000),"third-faction melee excluded");
  Check(Strong(Entry(typeof(BattleLogEntry_RangedFire),a,p),a,p,90000),"candidate/player ranged fire");
  Check(Strong(Entry(typeof(BattleLogEntry_ExplosionImpact),a,p),a,p,90000),"candidate/player explosion impact");
  Check(!Strong(Entry(typeof(BattleLogEntry_RangedFire),a,a),a,p,90000),"self-only concerns excluded");
  Check(!Strong((LogEntry)Shell(typeof(BattleLogEntry_Event)),a,p,90000),"generic battle event excluded");
  Check(!Strong(Entry(typeof(BattleLogEntry_RangedImpact),null,a,p),a,p,90000),"turret impact candidate/original-player excluded");
  Check(!Strong(Entry(typeof(BattleLogEntry_RangedImpact),a,b,p),a,p,90000),"three-party impact excluded");
  Check(Strong(Entry(typeof(BattleLogEntry_RangedImpact),a,p,p),a,p,90000),"impact actual==original duplicate confirms endpoints");
  Check(!Strong(Entry(typeof(BattleLogEntry_RangedImpact),a,a,p),a,p,90000),"self-hit with originalplayer excluded");
  Check(!Strong(Entry(typeof(BattleLogEntry_RangedImpact),a,p,null),a,p,90000),"two rawimpact concerns without confirmable roles excluded");
  Check(!Strong(Entry(typeof(BattleLogEntry_RangedFire),a,p),a,p,100001),"old timestamp excluded");
  BattleLog log=new BattleLog();
  for(int i=0;i<64;i++){
   Battle battle=(Battle)Shell(typeof(Battle));List<LogEntry> entries=new List<LogEntry>();
   for(int j=0;j<10000;j++)entries.Add(Entry(typeof(BattleLogEntry_RangedFire),b,c));
   Field(battle,"entries",entries);log.Battles.Add(battle);
  }
  Pawn[] candidates=new Pawn[8];for(int i=0;i<8;i++)candidates[i]=Pawn();
  Check(Scan(log,candidates,p)==0,"640000-entry synthetic history stays anonymous");
  // Place valid evidence OUTSIDE first128: must remainanonymous.
  log.Battles[0].Entries[128]=Entry(typeof(BattleLogEntry_RangedFire),candidates[0],p);
  Check(Scan(log,candidates,p)==0,"bounded false-negative beyond perbattle128");
  // Place valid evidence INSIDE bound: exactlyonepromotion.
  log.Battles[31].Entries[127]=Entry(typeof(BattleLogEntry_RangedFire),candidates[0],p);
  Check(Scan(log,candidates,p)==1,"one bounded promotion");
  // Worstcase allnegative reads4096each for8candidates.
  log.Battles[31].Entries[127]=Entry(typeof(BattleLogEntry_RangedFire),b,c);
  for(int i=0;i<20;i++)Scan(log,candidates,p);
  List<double> samples=new List<double>();int sum=0;
  for(int i=0;i<100;i++){var watch=Stopwatch.StartNew();sum+=Scan(log,candidates,p);watch.Stop();samples.Add(watch.Elapsed.TotalMilliseconds);}
  samples.Sort();Console.WriteLine("synthetic_real_getconcerns candidates=8 battles=64 entries=640000 visited_per_scan=32768 repetitions=100 median_ms="+samples[50].ToString("F3")+" p95_ms="+samples[95].ToString("F3")+" max_ms="+samples[99].ToString("F3")+" sum="+sum);
 }
}
