using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using PawnOwnership;
using PawnOwnership.Testing;

// Acceptance fixture links the complete production TestDriver.cs. Only engine APIs are stubbed.
namespace UnityEngine {
 public class Object { public static void DontDestroyOnLoad(object o) {} }
 public class MonoBehaviour {}
 public class GameObject { public static object Last; public GameObject(string n) {} public T AddComponent<T>() where T:new() { var v=new T(); Last=v; return v; } }
}
namespace RimWorld { public static class Root_Play { public static void SetupForQuickTestPlay() {} } }
namespace Verse {
 public enum ProgramState { Entry, Playing }
 public static class Current { public static ProgramState ProgramState; }
 public static class GenScene { public static bool InEntryScene=true; }
 public static class LongEventHandler { public static bool ShouldWaitForEvent; }
 public static class Prefs { public static bool DevMode; }
 public static class Log { public static void Message(string s) {} public static void Error(string s) {} public static void Warning(string s) {} }
 public static class QuickStarter { public static void CheckQuickStart() {} }
 public static class GameDataSaveLoader { public static void LoadGame(string s) {} }
 public enum ThingCategory { Item }
 public enum ThingRequestGroup { HaulableEver }
 public struct IntVec3 { public bool IsValid; public static IntVec3 Invalid => new IntVec3(); }
 public class ThingDef { public ThingCategory category; }
 public class Thing { public ThingDef def=new ThingDef(); public IntVec3 Position=new IntVec3 { IsValid=true }; }
 public class Lister { public List<Thing> Things=new List<Thing>(); public List<Thing> ThingsInGroup(ThingRequestGroup g)=>Things; }
 public class Parent { public string LabelCap="fixture"; }
 public class Info { public Parent parent=new Parent(); }
 public class Map { public Info info=new Info(); public int uniqueID=1; public Lister listerThings=new Lister(); public MapComponent_PawnOwnership Component=new MapComponent_PawnOwnership(); public T GetComponent<T>() where T:class => Component as T; }
 public class Camera { public void JumpToCurrentMapLoc(IntVec3 p) {} }
 public static class Find { public static Map CurrentMap; public static Camera CameraDriver=new Camera(); }
}
namespace PawnOwnership {
 public static class Mod_PawnOwnership { public static bool ShowMarkers=true; }
 public class MapComponent_PawnOwnership { public static int MarkersDrawnLastFrame, DrawCalls; public void SetOwner(Verse.Thing t,string s) {} }
}
class Program {
 static BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
 static void Set(object d,string f,object v)=>typeof(TestDriver).GetField(f,flags).SetValue(d,v);
 static object Call(object d,string m,params object[] a)=>typeof(TestDriver).GetMethod(m,flags).Invoke(d,a);
 static TestDriver Start(string dir,bool markers=true,bool dev=true) {
  Mod_PawnOwnership.ShowMarkers=markers; Verse.Prefs.DevMode=dev;
  TestDriver.Start("loadsave","fixture",dir); return (TestDriver)UnityEngine.GameObject.Last;
 }
 static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
 static string Root;
 static void Counts(string name,int owned,int on,int off,int back,int images,bool empty,bool expected) {
  string dir=Path.Combine(Root,name); var d=Start(dir,false,true);
  Set(d,"ownedCount",owned); Set(d,"markersOnDrawn",on); Set(d,"markersOffDrawn",off); Set(d,"markersBackDrawn",back);
  string[] files={"01-markers-on.png","02-markers-off.png","03-markers-back.png"};
  for(int i=0;i<images;i++) File.WriteAllBytes(Path.Combine(dir,files[i]),empty?Array.Empty<byte>():new byte[]{1});
  Call(d,"BeginGame"); Mod_PawnOwnership.ShowMarkers=true;
  Call(d,"Finish",true,null);
  using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"scenario-report.json")));
  Check(report.RootElement.GetProperty("ok").GetBoolean()==expected,name+" wrong result");
  Check(!Mod_PawnOwnership.ShowMarkers,name+" did not restore false preference");
  Check(Verse.Prefs.DevMode,name+" did not preserve loadsave DevMode=true");
  if(!expected) Check(!string.IsNullOrWhiteSpace(report.RootElement.GetProperty("error").GetString()),name+" missing error");
  Console.WriteLine("PASS "+name);
 }
 static void Preparation(string name,bool missing,bool empty) {
  var d=Start(Path.Combine(Root,name),false,false); Verse.Find.CurrentMap=new Verse.Map();
  if(missing) Verse.Find.CurrentMap.Component=null;
  if(!empty) for(int i=0;i<5;i++) Verse.Find.CurrentMap.listerThings.Things.Add(new Verse.Thing());
  // Drive the production Update catch and phase transition, rather than reproducing its logic.
  var phase=typeof(TestDriver).GetField("phase",flags);
  phase.SetValue(d,Enum.Parse(phase.FieldType,"Prepare")); Set(d,"frame",100);
  Call(d,"Update");
  if(missing||empty) {
   using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,name,"scenario-report.json")));
   Check(!report.RootElement.GetProperty("ok").GetBoolean(),name+" false positive");
   Check(!Verse.Prefs.DevMode,name+" did not restore DevMode=false");
  } else {
   Check(Mod_PawnOwnership.ShowMarkers,name+" initial false preference not enabled for first sample");
   Call(d,"Finish",false,"fixture cleanup"); Check(!Mod_PawnOwnership.ShowMarkers,name+" preference not restored");
  }
  Console.WriteLine("PASS "+name);
 }
 static int Main(string[] args) {
  Root=Path.GetFullPath(args[0]); Directory.CreateDirectory(Root);
  Counts("valid",5,5,0,5,3,false,true);
  Counts("all-zero",5,0,0,0,3,false,false);
  Counts("no-owned",0,5,0,5,3,false,false);
  Counts("off-nonzero",5,5,2,5,3,false,false);
  Counts("no-recovery",5,5,0,0,3,false,false);
  Counts("missing-image",5,5,0,5,2,false,false);
  Counts("empty-images",5,5,0,5,3,true,false);
  Preparation("initial-false",false,false);
  Preparation("missing-component",true,false);
  Preparation("empty-map",false,true);
  Console.WriteLine("RESULT 10/10"); return 0;
 }
}
