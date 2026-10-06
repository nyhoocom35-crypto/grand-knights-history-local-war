using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Xml.Serialization;

public sealed class GkhCampaignSettings {
 public int WarSeconds=1800, AiTickSeconds=30, VoteWindowSeconds=60, AiVotersPerNation=20;
 public void Validate() {
  if(WarSeconds<60 || WarSeconds>86400 || AiTickSeconds<1 || AiTickSeconds>300 || AiTickSeconds>=WarSeconds || VoteWindowSeconds<1 || VoteWindowSeconds>=WarSeconds || AiVotersPerNation<1 || AiVotersPerNation>1000)
   throw new InvalidDataException("Invalid campaign settings: war 60..86400 sec, AI tick 1..300 sec and less than war length, vote window 1..war-1 sec, voters 1..1000.");
 }
}
public sealed class GkhFinishedWar {
 public byte[] War;
 public int[] Votes, NextFronts;
 public long EndTicks;
 public string Reason;
 public int[] Owners;
}
public sealed class GkhContract {
 public string Id;
 public int Nation, Duration, Remaining, Rate, Amount;
 public bool Started, PaymentConfirmed, PaymentReview;
 public int BalanceBefore, BalanceTarget;
 public uint StartCampaign, StartRound;
}
public sealed class GkhCampaignState {
 public int Version=10;
 public GkhContract Contract;
 public string LastContractRequest="", ContractMessage="";
 public int LastPlayerMoney=-1, LastPlayerNation=-1;
 public int BorderPolicyRevision=0;
 public int[] Owners;
 public int[] BallotTargets=new int[]{-1,-1,-1};
 public byte[] War;
 public int[] Candidates;
 public long StartTicks, NextAiTicks;
 public bool Voted;
 public int[] Votes=new int[9];
 public long[] NationOperations=new long[3], NationAiPoints=new long[3];
 public uint RandomState=0x71c3a5d9;
 public GkhCampaignSettings Settings=new GkhCampaignSettings();
 public List<GkhFinishedWar> History=new List<GkhFinishedWar>();
 public List<string> BattleKeys=new List<string>();
}
public static class GkhCampaign {
 static readonly object Gate=new object();
 static GkhCampaignState State;
 static GkhCampaignSettings Desired=new GkhCampaignSettings();
 static string Folder;
 public static string LastNote="Campaign waits for the first war sync.";
 const long Second=TimeSpan.TicksPerSecond;
 public static void Configure(string folder,int warSeconds,int aiSeconds,int voteSeconds,int voters) {
  lock(Gate) {
   Desired=new GkhCampaignSettings();Desired.WarSeconds=warSeconds;Desired.AiTickSeconds=aiSeconds;Desired.VoteWindowSeconds=voteSeconds;Desired.AiVotersPerNation=voters;Desired.Validate();
   Folder=folder;Directory.CreateDirectory(folder);
   string path=Path.Combine(folder,"campaign_state.bin");
   if(File.Exists(path)) { State=Decode(File.ReadAllBytes(path));Migrate(State);UpgradePolicy(State);ValidateState(State);Save();LastNote="Campaign resumed. "+Describe(State,DateTime.UtcNow.Ticks); }
   else State=null;
  }
 }
 static uint Crc(byte[] bytes,int count) {
  uint value=0xffffffff;
  for(int i=0;i<count;i++) { value^=bytes[i];for(int bit=0;bit<8;bit++)value=(value&1)!=0?(value>>1)^0xedb88320:value>>1; }
  return value^0xffffffff;
 }
 static void ValidateWar(byte[] war) {
  if(war==null || war.Length!=138 || BitConverter.ToUInt32(war,0)==0 || BitConverter.ToUInt32(war,8)<2 || BitConverter.ToUInt32(war,8)>10000 || BitConverter.ToUInt32(war,4)>=BitConverter.ToUInt32(war,8))throw new InvalidDataException("Unsupported campaign war layout.");
  HashSet<int> fronts=new HashSet<int>(),nations=new HashSet<int>();
  for(int i=0;i<3;i++) {
   if(war[132+i]==255 && war[135+i]==3)continue;
   if(war[132+i]>12 || !fronts.Add(war[132+i]) || war[135+i]>2 || !nations.Add(war[135+i]))throw new InvalidDataException("Invalid active front/nation.");
  }
 }
 static void ValidateState(GkhCampaignState s) {
  if(s==null || s.Version!=10)throw new InvalidDataException("Unsupported campaign state version.");ValidateWar(s.War);s.Settings.Validate();ValidateContract(s);
  if(s.Owners==null || s.Owners.Length!=13 || s.Votes==null || s.Votes.Length!=39 || s.BallotTargets==null || s.BallotTargets.Length!=3 || s.NationOperations==null || s.NationOperations.Length!=3 || s.NationAiPoints==null || s.NationAiPoints.Length!=3 || s.History==null || s.History.Count>16 || s.BattleKeys==null || s.BattleKeys.Count>5000 || s.RandomState==0 || s.StartTicks<0 || s.StartTicks>DateTime.MaxValue.Ticks-s.Settings.WarSeconds*Second || s.NextAiTicks<=s.StartTicks)throw new InvalidDataException("Invalid campaign fields. State was not reset.");
  for(int t=0;t<13;t++)if(s.Owners[t]<0 || s.Owners[t]>2)throw new InvalidDataException("Invalid territory owner.");
  for(int f=0;f<3;f++)if(s.War[132+f]!=255 && (s.Owners[s.War[132+f]]==s.War[135+f] || !Eligible(s,s.War[135+f],s.War[132+f])))throw new InvalidDataException("Friendly or non-adjacent active front.");
  for(int n=0;n<3;n++) {
   if(s.NationOperations[n]<0 || s.NationAiPoints[n]<0)throw new InvalidDataException("Invalid campaign totals.");
   int total=0;for(int t=0;t<13;t++) { int v=s.Votes[n*13+t];if(v<0 || v>s.Settings.AiVotersPerNation || (v>0 && !Eligible(s,n,t)))throw new InvalidDataException("Invalid adjacency ballot.");total+=v; }
   int expected=s.Voted && Candidates(s,n).Count>0?s.Settings.AiVotersPerNation:0;
   if(total!=expected)throw new InvalidDataException("Invalid ballot total.");
  }
  foreach(GkhFinishedWar h in s.History) { ValidateWar(h.War);if(h.Votes==null || (h.Votes.Length!=9 && h.Votes.Length!=39) || h.NextFronts==null || h.NextFronts.Length!=3 || (h.Owners!=null && h.Owners.Length!=13))throw new InvalidDataException("Invalid archived war."); }
  if(new HashSet<string>(s.BattleKeys,StringComparer.Ordinal).Count!=s.BattleKeys.Count)throw new InvalidDataException("Duplicate battle keys.");
 }
 static bool Eligible(GkhCampaignState s,int nation,int target) {
  if(nation<0 || nation>2 || target<3 || target>12 || s.Owners[target]==nation)return false;
  bool[] supplied=Supplied(s,nation);
  foreach(int adjacent in GkhWorld.Edges[target])if(supplied[adjacent])return true;return false;
 }
 static bool[] Supplied(GkhCampaignState s,int nation) {
  bool[] result=new bool[13];if(s.Owners[nation]!=nation)return result;
  Queue<int> queue=new Queue<int>();queue.Enqueue(nation);result[nation]=true;
  while(queue.Count>0) { int t=queue.Dequeue();foreach(int next in GkhWorld.Edges[t])if(!result[next] && s.Owners[next]==nation) { result[next]=true;queue.Enqueue(next); } }
  return result;
 }
 static void UpgradePolicy(GkhCampaignState s) {
  if(s.BorderPolicyRevision==11)return;
  if(s.Owners==null || s.Owners.Length!=13 || s.Votes==null || s.Votes.Length!=39)throw new InvalidDataException("Cannot upgrade malformed world state.");
  bool repair=false;for(int f=0;f<3;f++)if(s.War[132+f]!=255 && !Eligible(s,s.War[135+f],s.War[132+f]))repair=true;
  s.Voted=false;Array.Clear(s.Votes,0,39);Vote(s);
  if(repair) {
   GkhFinishedWar h=new GkhFinishedWar();h.War=(byte[])s.War.Clone();h.Owners=(int[])s.Owners.Clone();h.Votes=(int[])s.Votes.Clone();h.NextFronts=Pick(s);h.EndTicks=DateTime.UtcNow.Ticks;h.Reason="Unsupported capital/unsupplied front retired; no ownership award";s.History.Add(h);if(s.History.Count>16)s.History.RemoveAt(0);
   IncrementRound(s);Array.Clear(s.War,48,84);SetFronts(s,h.NextFronts);s.StartTicks=h.EndTicks;s.NextAiTicks=s.StartTicks+s.Settings.AiTickSeconds*Second;
  }
  s.BorderPolicyRevision=11;
 }
 static List<int> Candidates(GkhCampaignState s,int nation) {
  List<int> result=new List<int>();for(int t=0;t<13;t++)if(Eligible(s,nation,t))result.Add(t);return result;
 }
 static void SetFronts(GkhCampaignState s,int[] targets) {
  for(int f=0;f<3;f++) { s.War[132+f]=255;s.War[135+f]=3; }
  int slot=0;for(int n=0;n<3;n++)if(targets[n]>=0) { s.War[132+slot]=(byte)targets[n];s.War[135+slot]=(byte)n;slot++; }
 }
 static void IncrementRound(GkhCampaignState s) {
  uint round=BitConverter.ToUInt32(s.War,4)+1;
  if(round>=BitConverter.ToUInt32(s.War,8)) { uint id=BitConverter.ToUInt32(s.War,0);if(id==UInt32.MaxValue)throw new InvalidDataException("Campaign ID exhausted.");Buffer.BlockCopy(BitConverter.GetBytes(id+1),0,s.War,0,4);round=0; }
  Buffer.BlockCopy(BitConverter.GetBytes(round),0,s.War,4,4);
 }
 static void InitializeWorld(GkhCampaignState s,bool keepOldRoute) {
  s.Owners=(int[])GkhWorld.InitialOwners.Clone();s.Candidates=new int[13];for(int i=0;i<13;i++)s.Candidates[i]=i;
  if(keepOldRoute) {
   GkhFinishedWar h=new GkhFinishedWar();h.War=(byte[])s.War.Clone();h.Votes=(int[])s.Votes.Clone();h.NextFronts=new int[]{-1,-1,-1};h.Owners=(int[])s.Owners.Clone();h.EndTicks=DateTime.UtcNow.Ticks;h.Reason="Legacy front layout retired; no territory awarded";s.History.Add(h);if(s.History.Count>16)s.History.RemoveAt(0);IncrementRound(s);
  }
  s.Version=10;s.Votes=new int[39];s.BallotTargets=new int[]{-1,-1,-1};s.Voted=false;
  Vote(s);SetFronts(s,Pick(s));Array.Clear(s.War,48,84);s.Voted=false;Array.Clear(s.Votes,0,39);
  s.Settings=CopySettings(Desired);s.StartTicks=DateTime.UtcNow.Ticks;s.NextAiTicks=s.StartTicks+s.Settings.AiTickSeconds*Second;s.BorderPolicyRevision=11;Vote(s);
 }
 static void Migrate(GkhCampaignState s) {
  if(s.Version==8) { ValidateWar(s.War);s.Settings.Validate();InitializeWorld(s,true); }
  else if(s.Version!=10)throw new InvalidDataException("Unsupported campaign version; original retained.");
 }
 static byte[] Encode(GkhCampaignState s) {
  using(MemoryStream xml=new MemoryStream()) {
   new XmlSerializer(typeof(GkhCampaignState)).Serialize(xml,s);byte[] payload=xml.ToArray();byte[] result=new byte[payload.Length+12];
   Buffer.BlockCopy(Encoding.ASCII.GetBytes("GKC8"),0,result,0,4);Buffer.BlockCopy(BitConverter.GetBytes(payload.Length),0,result,4,4);Buffer.BlockCopy(payload,0,result,8,payload.Length);
   Buffer.BlockCopy(BitConverter.GetBytes(Crc(result,result.Length-4)),0,result,result.Length-4,4);return result;
  }
 }
 static GkhCampaignState Decode(byte[] data) {
  if(data.Length<12 || data.Length>8*1024*1024 || Encoding.ASCII.GetString(data,0,4)!="GKC8" || BitConverter.ToInt32(data,4)!=data.Length-12 || Crc(data,data.Length-4)!=BitConverter.ToUInt32(data,data.Length-4))throw new InvalidDataException("Campaign state checksum/length mismatch. Preserve campaign_state.bin and .previous for diagnosis.");
  using(MemoryStream xml=new MemoryStream(data,8,data.Length-12))return (GkhCampaignState)new XmlSerializer(typeof(GkhCampaignState)).Deserialize(xml);
 }
 static void WriteAtomic(string path,byte[] data) {
  string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
  try {
   using(FileStream stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { stream.Write(data,0,data.Length);stream.Flush(true); }
   if(File.Exists(path))File.Replace(temp,path,path+".previous");else File.Move(temp,path);
  } finally { if(File.Exists(temp))File.Delete(temp); }
 }
 static void Save() {
  ValidateState(State);WriteAtomic(Path.Combine(Folder,"campaign_state.bin"),Encode(State));
  File.WriteAllText(Path.Combine(Folder,"campaign_status.txt"),StatusText(State,DateTime.UtcNow.Ticks),new UTF8Encoding(true));
  WriteAtomic(Path.Combine(Folder,"contract_status.json"),Encoding.UTF8.GetBytes(ContractStatus(State)));
 }
 static GkhCampaignSettings CopySettings(GkhCampaignSettings x) {
  GkhCampaignSettings y=new GkhCampaignSettings();y.WarSeconds=x.WarSeconds;y.AiTickSeconds=x.AiTickSeconds;y.VoteWindowSeconds=x.VoteWindowSeconds;y.AiVotersPerNation=x.AiVotersPerNation;return y;
 }
 public static void Start(byte[] incoming,string[] importedKeys) {
  lock(Gate) {
   ValidateWar(incoming);if(State!=null)return;
   State=new GkhCampaignState();State.War=(byte[])incoming.Clone();
   if(importedKeys!=null)foreach(string key in importedKeys) {
    if(Convert.FromBase64String(key).Length!=32)throw new InvalidDataException("Invalid imported battle key.");
    State.BattleKeys.Add(BitConverter.ToUInt32(incoming,0)+":"+BitConverter.ToUInt32(incoming,4)+":"+key);
   }
   if(State.BattleKeys.Count>5000)State.BattleKeys.RemoveRange(0,State.BattleKeys.Count-5000);
   // Retire the old layout under a new round ID; never award legacy guesses.
   State.Votes=new int[9];InitializeWorld(State,true);Save();LastNote="Territorial campaign started. "+Describe(State,State.StartTicks);
  }
 }
 static uint Random(GkhCampaignState s) {
  uint x=s.RandomState;x^=x<<13;x^=x>>17;x^=x<<5;s.RandomState=x;return x;
 }
 static int Slot(GkhCampaignState s,int nation) {
  for(int i=0;i<3;i++)if(s.War[135+i]==nation)return i;return -1;
 }
 static void AddStrength(byte[] war,int offset,uint gain) {
  uint value=BitConverter.ToUInt32(war,offset);
  if(value>1000000000-gain)throw new InvalidDataException("Campaign strength limit reached.");
  Buffer.BlockCopy(BitConverter.GetBytes(value+gain),0,war,offset,4);
 }
 static void AiTick(GkhCampaignState s) {
  for(int n=0;n<3;n++) {
   List<int> active=new List<int>();for(int f=0;f<3;f++)if(s.War[132+f]!=255 && (s.War[135+f]==n || s.Owners[s.War[132+f]]==n))active.Add(f);
   s.NationOperations[n]++;if(active.Count==0)continue;
   int front=active[(int)(Random(s)%(uint)active.Count)];uint gain=5+Random(s)%21;
   AddStrength(s.War,(s.War[135+front]==n?72:84)+front*4,gain);s.NationAiPoints[n]+=gain;
  }
 }
 static void Vote(GkhCampaignState s) {
  if(s.Voted)return;Array.Clear(s.Votes,0,39);
  for(int n=0;n<3;n++) {
   List<int> candidates=Candidates(s,n);if(candidates.Count==0)continue;
   int[] weight=new int[candidates.Count];int total=0;
   for(int i=0;i<weight.Length;i++) { weight[i]=10+(int)(Random(s)%31);total+=weight[i]; }
   for(int voter=0;voter<s.Settings.AiVotersPerNation;voter++) {
    int choice=(int)(Random(s)%(uint)total),sum=0;
    for(int i=0;i<weight.Length;i++) { sum+=weight[i];if(choice<sum) { s.Votes[n*13+candidates[i]]++;break; } }
   }
  }
  s.Voted=true;
 }
 static int[] Pick(GkhCampaignState s) {
  int[] selected=new int[]{-1,-1,-1};
  for(int n=0;n<3;n++) {
   List<int> candidates=Candidates(s,n);int best=-1;List<int> tied=new List<int>();
   foreach(int t in candidates) { int count=s.Votes[n*13+t];if(count>best) { best=count;tied.Clear(); }if(count==best)tied.Add(t); }
   if(tied.Count>0)selected[n]=tied[(int)(Random(s)%(uint)tied.Count)];
  }
  s.BallotTargets=(int[])selected.Clone();
  // A native region can appear in only one battle slot. Keep every nation's top
  // ballot target; conflicts wait, rather than silently moving to a runner-up.
  for(int t=0;t<13;t++) {
   int best=-1;List<int> tied=new List<int>();
   for(int n=0;n<3;n++)if(selected[n]==t) { int count=s.Votes[n*13+t];if(count>best) { best=count;tied.Clear(); }if(count==best)tied.Add(n); }
   if(tied.Count>1 || best>=0) { int winner=tied[(int)(Random(s)%(uint)tied.Count)];for(int n=0;n<3;n++)if(selected[n]==t && n!=winner)selected[n]=-1; }
  }
  return selected;
 }
 static void EndWar(GkhCampaignState s,long boundary,GkhCampaignSettings nextSettings) {
  if(s.Contract!=null && s.Contract.Started && s.Contract.Remaining>0)s.Contract.Remaining--;
  byte[] finished=(byte[])s.War.Clone();int[] before=(int[])s.Owners.Clone();bool changed=false;
  for(int f=0;f<3;f++)if(s.War[132+f]!=255) {
   int t=s.War[132+f];uint attack=BitConverter.ToUInt32(s.War,72+4*f),defense=BitConverter.ToUInt32(s.War,84+4*f);
   if(attack>defense) { s.Owners[t]=s.War[135+f];changed=true; }
  }
  // Settlement can change the border. Recast the final next-war ballot on the
  // settled map so no nation attacks land it just acquired or a non-adjacent land.
  if(changed) { s.Voted=false;Array.Clear(s.Votes,0,39); }Vote(s);int[] next=Pick(s);
  GkhFinishedWar archive=new GkhFinishedWar();archive.War=finished;archive.Owners=before;archive.Votes=(int[])s.Votes.Clone();archive.NextFronts=next;archive.EndTicks=boundary;archive.Reason=changed?"War settled; winning attackers acquired territories; border ballot finalized":"War settled; defenders retained territories; ties retain owner";
  s.History.Add(archive);if(s.History.Count>16)s.History.RemoveAt(0);
  IncrementRound(s);Array.Clear(s.War,48,84);SetFronts(s,next);
  s.Settings=CopySettings(nextSettings);s.StartTicks=boundary;s.NextAiTicks=boundary+s.Settings.AiTickSeconds*Second;s.Voted=false;Array.Clear(s.Votes,0,39);Vote(s);
 }
 static int Advance(GkhCampaignState s,long now,GkhCampaignSettings settings,int limit) {
  int events=0;
  while(events<limit) {
   long end=s.StartTicks+s.Settings.WarSeconds*Second,vote=s.StartTicks+(s.Settings.WarSeconds-s.Settings.VoteWindowSeconds)*Second;
   long due=Math.Min(end,s.NextAiTicks);if(!s.Voted)due=Math.Min(due,vote);
   if(due>now)break;
   // At an exact boundary finalize the old round first; there is no extra AI tick.
   if(due==end)EndWar(s,end,settings);
   else if(!s.Voted && due==vote)Vote(s);
   else { AiTick(s);s.NextAiTicks+=s.Settings.AiTickSeconds*Second; }
   events++;
  }
  return events;
 }
 public static string Pump() {
  lock(Gate) {
   if(State==null)return "";
   uint id=BitConverter.ToUInt32(State.War,0),round=BitConverter.ToUInt32(State.War,4);bool voted=State.Voted;
   int count=Advance(State,DateTime.UtcNow.Ticks,Desired,5000);if(count==0)return "";
   Save();LastNote=Describe(State,DateTime.UtcNow.Ticks)+(count==5000?" Catch-up continues on the next tick.":"");
   string eventType=(id!=BitConverter.ToUInt32(State.War,0) || round!=BitConverter.ToUInt32(State.War,4))?"NEXT_WAR":(!voted && State.Voted?"AI_VOTE":"AI_ACTIVITY");
   return eventType+": "+LastNote;
  }
 }
 public static byte[] WireWar() {
  lock(Gate) { if(State==null)throw new InvalidDataException("Campaign not initialized.");byte[] native=new byte[140];Buffer.BlockCopy(State.War,0,native,0,138);return native; }
 }
 static byte[] RouteWar(GkhCampaignState s,byte[] requested) {
  ValidateWar(requested);byte[] source=requested;
  if(SameRound(s.War,requested))source=s.War;
  else foreach(GkhFinishedWar h in s.History)if(SameRound(h.War,requested)) { source=h.War;break; }
  // Never change round/front identity in the middle of a player's marching route.
  // An expired route retains its request; explicit SyncWar delivers the latest round.
  byte[] native=new byte[140];Buffer.BlockCopy(source,0,native,0,138);return native;
 }
 public static byte[] WireWarForRoute(byte[] requested) {
  lock(Gate) { if(State==null)throw new InvalidDataException("Campaign not initialized.");return RouteWar(State,requested); }
 }
 static int[] RouteOwners(GkhCampaignState s,byte[] requested) {
  if(requested==null || SameRound(s.War,requested))return s.Owners;
  foreach(GkhFinishedWar h in s.History)if(SameRound(h.War,requested))return h.Owners??GkhWorld.InitialOwners;
  return GkhWorld.InitialOwners;
 }
 static byte[] RegionRecord(int land,int owner,int aiVotes,bool playerVoted,int attacker=-1) {
  // Match the original world initializer: metadata node+0x1E bit0 identifies
  // defender-side nodes; the other half belongs to the active attacking nation.
  byte[] region=new byte[1164];bool active=attacker>=0 && attacker<=2 && attacker!=owner;
  for(int node=0;node<96;node++)region[node*12+8]=(byte)(node>=GkhWorld.NodeCounts[land]?3:(active && GkhWorld.DefenderMasks[land][node]=='0'?attacker:owner));
  // 0x0896B028/0x0896B138 flag defender nodes bordering attacker nodes. Fill
  // that same bit so the client receives battle frontiers on the first Sync.
  if(active)for(int node=0;node<GkhWorld.NodeCounts[land];node++)if(region[node*12+8]==owner) {
   foreach(int near in GkhWorld.NodeNeighbors[land][node])if(region[near*12+8]!=owner) { region[node*12+9]|=1;break; }
  }
  // Native council UI sums these fields. The player increment uses +0x480.
  Buffer.BlockCopy(BitConverter.GetBytes(playerVoted?1:0),0,region,0x480,4);
  Buffer.BlockCopy(BitConverter.GetBytes(aiVotes),0,region,0x484,4);region[0x488]=(byte)owner;return region;
 }
 public static byte[][] RegionsForRoute(byte[] requested,int nation,int playerChoice) {
  lock(Gate) {
   if(State==null)throw new InvalidDataException("Campaign not initialized.");if(nation<0 || nation>2)throw new InvalidDataException("Invalid council nation.");int[] owners=RouteOwners(State,requested);int[] votes=State.Votes;
   if(!SameRound(State.War,requested))foreach(GkhFinishedWar h in State.History)if(SameRound(h.War,requested) && h.Votes.Length==39) { votes=h.Votes;break; }
   byte[][] regions=new byte[13][];
   for(int t=0;t<13;t++) {
    int attacker=-1;for(int f=0;f<3;f++)if(requested[132+f]==t)attacker=requested[135+f];
    regions[t]=RegionRecord(t,owners[t],votes[nation*13+t],playerChoice==t && Eligible(State,nation,t),attacker);
   }
   return regions;
  }
 }
 public static int HostileNation(int playerNation,int requestedNation) {
  lock(Gate) {
   if(playerNation<0 || playerNation>2 || requestedNation<0 || requestedNation>2)throw new InvalidDataException("Invalid enemy/player nation.");
   if(playerNation!=requestedNation)return requestedNation;
   if(State!=null)for(int f=0;f<3;f++)if(State.War[132+f]!=255) {
    int attacker=State.War[135+f],defender=State.Owners[State.War[132+f]];
    if(attacker==playerNation)return defender;if(defender==playerNation)return attacker;
   }
   throw new InvalidDataException("Friendly enemy query without a hostile active front; return and sync the war map.");
  }
 }
 static bool SameRound(byte[] a,byte[] b) { for(int i=0;i<8;i++)if(a[i]!=b[i])return false;return true; }
 static string ApplyPlayer(GkhCampaignState s,byte[] requestedWar,byte[] player,byte[] battle,byte[] enemy,string digest) {
  if(player.Length!=88 || battle.Length!=24 || enemy.Length!=1)throw new InvalidDataException("Unexpected battle layout.");
  string key=BitConverter.ToUInt32(requestedWar,0)+":"+BitConverter.ToUInt32(requestedWar,4)+":"+digest;
  if(s.BattleKeys.Contains(key))return "Duplicate battle: no second credit";
  byte[] target=null;bool late=false;
  if(SameRound(s.War,requestedWar))target=s.War;
  else foreach(GkhFinishedWar h in s.History)if(SameRound(h.War,requestedWar)) { target=h.War;late=true;break; }
  if(target==null)return "Expired/foreign round: acknowledged without current-round credit";
  int outcome=BitConverter.ToInt32(battle,0),location=BitConverter.ToInt16(battle,8),nation=player[48],opponent=enemy[0],front=-1;
  for(int i=0;i<3;i++)if(target[132+i]==location)front=i;
  if(front<0 || nation>2 || opponent>2 || nation==opponent || (outcome!=2 && outcome!=4) || (target[135+front]!=nation && target[135+front]!=opponent))return "Unmapped battle: acknowledged without credit";
  int[] owners=RouteOwners(s,requestedWar);if(opponent==nation || (target[135+front]==nation?owners[location]!=opponent:owners[location]!=nation))return "Wrong opposing nation: no credit";
  int winner=outcome==4?nation:opponent;AddStrength(target,(target[135+front]==winner?72:84)+front*4,100);s.BattleKeys.Add(key);if(s.BattleKeys.Count>5000)s.BattleKeys.RemoveAt(0);
  return (outcome==4?"WIN":"LOSS")+": +100 to winning side"+(late?" in archived round; new round unchanged":"");
 }
 public static void PlayerResult(byte[] requestedWar,byte[] player,byte[] battle,byte[] enemy,string digest) {
  lock(Gate) { LastNote=ApplyPlayer(State,requestedWar,player,battle,enemy,digest)+". "+Describe(State,DateTime.UtcNow.Ticks);Save(); }
 }
 static string Describe(GkhCampaignState s,long now) {
  StringBuilder text=new StringBuilder();long remaining=Math.Max(0,(s.StartTicks+s.Settings.WarSeconds*Second-now)/Second);
  text.Append("War ").Append(BitConverter.ToUInt32(s.War,0)).Append(" / stage ").Append(BitConverter.ToUInt32(s.War,4)).Append("; ").Append(remaining).Append(" sec; ").Append(s.Voted?"AI votes complete":"AI active");
  for(int f=0;f<3;f++)if(s.War[132+f]!=255) { int t=s.War[132+f];text.Append(" | front ").Append(t).Append(" N").Append(s.War[135+f]).Append(" vs N").Append(s.Owners[t]).Append(" ").Append(BitConverter.ToUInt32(s.War,72+4*f)).Append(":").Append(BitConverter.ToUInt32(s.War,84+4*f)); }
  return text.ToString();
 }
 static string StatusText(GkhCampaignState s,long now) {
  StringBuilder text=new StringBuilder();text.AppendLine("GKH territorial campaign v12.2").AppendLine(Describe(s,now));
  for(int n=0;n<3;n++) {
   List<string> owned=new List<string>(),ballots=new List<string>();for(int t=0;t<13;t++) { if(s.Owners[t]==n)owned.Add(t.ToString());if(Eligible(s,n,t))ballots.Add(t+"="+s.Votes[n*13+t]); }
   text.AppendLine("N"+n+" owns ["+String.Join(",",owned.ToArray())+"]; supplied border AI votes ["+String.Join(",",ballots.ToArray())+"]; operations="+s.NationOperations[n]+" AI points="+s.NationAiPoints[n]);
  }
  text.AppendLine("Latest ballot top targets by nation: "+String.Join(",",Array.ConvertAll(s.BallotTargets,delegate(int x){return x.ToString();}))+" (-1: no candidate)");
  text.AppendLine("Recent completed rounds (newest last):");foreach(GkhFinishedWar h in s.History) {
   text.AppendLine("War "+BitConverter.ToUInt32(h.War,0)+" stage "+BitConverter.ToUInt32(h.War,4)+" ended "+new DateTime(h.EndTicks,DateTimeKind.Utc).ToString("o")+"; "+h.Reason);
   for(int n=0;n<3;n++) { List<string> v=new List<string>();int width=h.Votes.Length/3;for(int t=0;t<width;t++)if(h.Votes[n*width+t]>0)v.Add(t+"="+h.Votes[n*width+t]);text.AppendLine("  N"+n+" votes ["+String.Join(",",v.ToArray())+"] -> scheduled front "+h.NextFronts[n]+" (-1: wait/no candidate)"); }
  }
  text.AppendLine("In-game council counters show the requesting nation's AI votes + local player vote. All three nations are listed above.");
  for(int n=0;n<3;n++)for(int t=3;t<13;t++)if(Eligible(s,n,t)) { bool[] supplied=Supplied(s,n);List<string> sources=new List<string>();foreach(int near in GkhWorld.Edges[t])if(supplied[near])sources.Add(near.ToString());text.AppendLine("Supply proof: N"+n+" target "+t+" from owned adjacent ["+String.Join(",",sources.ToArray())+"] connected to capital "+n); }
  text.AppendLine("Local custom rules; territory ownership settles at the war timer boundary. No remote players.");return text.ToString();
 }

 static int[] ContractRates(GkhCampaignState s) {
  int[] counts=new int[3],rates=new int[3];foreach(int owner in s.Owners)counts[owner]++;
  int min=Math.Min(counts[0],Math.Min(counts[1],counts[2])),max=Math.Max(counts[0],Math.Max(counts[1],counts[2]));
  for(int n=0;n<3;n++)rates[n]=max==min?1000:900+(max-counts[n])*200/(max-min);
  return rates;
 }
 static bool ActiveContract(GkhCampaignState s) { return s.Contract!=null && (!s.Contract.Started || s.Contract.Remaining>0); }
 static void ValidateContract(GkhCampaignState s) {
  if(s.LastPlayerMoney < -1 || s.LastPlayerMoney>9999999 || s.LastPlayerNation < -1 || s.LastPlayerNation>2)throw new InvalidDataException("Invalid contract player snapshot.");
  GkhContract c=s.Contract;if(c==null)return;
  Guid id;if(!Guid.TryParse(c.Id,out id) || c.Nation<0 || c.Nation>2 || c.Duration<1 || c.Duration>60 || c.Remaining<0 || c.Remaining>c.Duration || c.Rate<900 || c.Rate>1100 || c.Amount!=c.Rate*c.Duration || (c.Started && (c.BalanceBefore<0 || c.BalanceTarget!=c.BalanceBefore+c.Amount || c.BalanceTarget>9999999 || c.StartCampaign==0)))throw new InvalidDataException("Invalid contract state.");
 }
 static string ContractStatus(GkhCampaignState s) {
  int[] rates=ContractRates(s),counts=new int[3];foreach(int owner in s.Owners)counts[owner]++;
  GkhContract c=s.Contract;
  return "{\"war_id\":"+BitConverter.ToUInt32(s.War,0)+",\"round\":"+BitConverter.ToUInt32(s.War,4)+",\"territories\":["+String.Join(",",Array.ConvertAll(counts,delegate(int x){return x.ToString();}))+"],\"rates\":["+String.Join(",",Array.ConvertAll(rates,delegate(int x){return x.ToString();}))+"],\"money\":"+s.LastPlayerMoney+",\"player_nation\":"+s.LastPlayerNation+",\"active\":"+(ActiveContract(s)?"true":"false")+",\"nation\":"+(c==null?-1:c.Nation)+",\"duration\":"+(c==null?0:c.Duration)+",\"remaining\":"+(c==null?0:c.Remaining)+",\"amount\":"+(c==null?0:c.Amount)+",\"started\":"+(c!=null && c.Started?"true":"false")+",\"payment_confirmed\":"+(c!=null && c.PaymentConfirmed?"true":"false")+",\"payment_review\":"+(c!=null && c.PaymentReview?"true":"false")+",\"message\":\""+(s.ContractMessage??"").Replace("\\","\\\\").Replace("\"","\\\"")+"\"}";
 }
 static string SelectContract(GkhCampaignState s,string id,int nation,int duration) {
  Guid parsed;if(!Guid.TryParse(id,out parsed) || nation<0 || nation>2 || duration<1 || duration>60)return "Invalid contract request.";
  if(s.LastContractRequest==id)return s.ContractMessage;
  s.LastContractRequest=id;
  if(ActiveContract(s))return s.ContractMessage="Existing contract is still active.";
  if(s.Contract!=null && s.Contract.Started && !s.Contract.PaymentConfirmed)return s.ContractMessage="Previous payment needs confirmation; reconnect and export logs.";
  if(s.LastPlayerMoney<0)return s.ContractMessage="Reconnect once before selecting a contract.";
  int rate=ContractRates(s)[nation],amount=rate*duration;
  if(s.LastPlayerMoney+amount>9999999)return s.ContractMessage="Money limit reached; select a shorter contract.";
  s.Contract=new GkhContract();s.Contract.Id=id;s.Contract.Nation=nation;s.Contract.Duration=duration;s.Contract.Remaining=duration;s.Contract.Rate=rate;s.Contract.Amount=amount;
  return s.ContractMessage="Contract accepted. Reconnect to apply nation and payment. Price: "+amount+" G.";
 }
 public static string PollContractRequest() {
  lock(Gate) {
   if(State==null)return "";string path=Path.Combine(Folder,"contract_request.txt");if(!File.Exists(path))return "";
   string[] parts=File.ReadAllText(path).Trim().Split('|');if(parts.Length!=3)throw new InvalidDataException("Invalid contract command.");
   if(parts[0]==State.LastContractRequest)return "";int nation,duration;if(!Int32.TryParse(parts[1],out nation) || !Int32.TryParse(parts[2],out duration))throw new InvalidDataException("Invalid contract command numbers.");
   string note=SelectContract(State,parts[0],nation,duration);Save();return note;
  }
 }
 // The patched reader changes money only when the native balance still equals
 // the persisted baseline. Replays cannot add a second reward to that balance.
 static byte[] ContractWire(GkhCampaignState s,byte[] player) {
  if(player==null || player.Length!=88 || player[48]>2)throw new InvalidDataException("Invalid contract player.");
  int money=BitConverter.ToInt32(player,40);if(money<0 || money>9999999)throw new InvalidDataException("Unsupported player money.");
  s.LastPlayerMoney=money;s.LastPlayerNation=player[48];byte[] common=new byte[936];GkhContract c=s.Contract;
  if(c==null)return common;
  if(!c.Started) {
   if(money+c.Amount>9999999) { s.ContractMessage="Payment would exceed money limit. Spend money and reconnect.";return common; }
   c.Started=true;c.BalanceBefore=money;c.BalanceTarget=money+c.Amount;c.StartCampaign=BitConverter.ToUInt32(s.War,0);c.StartRound=BitConverter.ToUInt32(s.War,4);
  } else if(!c.PaymentConfirmed && !c.PaymentReview) {
   if(player[48]==c.Nation && money==c.BalanceTarget) { c.PaymentConfirmed=true;s.ContractMessage="Contract payment confirmed."; }
   else if(money!=c.BalanceBefore) { c.PaymentReview=true;s.ContractMessage="Balance changed before payment confirmation. Export logs; reward paused."; }
  }
  Buffer.BlockCopy(Encoding.ASCII.GetBytes("GKC2"),0,common,0,4);
  Buffer.BlockCopy(BitConverter.GetBytes(c.PaymentConfirmed || c.PaymentReview?-1:c.BalanceBefore),0,common,4,4);
  Buffer.BlockCopy(BitConverter.GetBytes(c.BalanceTarget),0,common,8,4);common[12]=(byte)c.Nation;
  return common;
 }
 public static byte[] SyncContract(byte[] player) {
  lock(Gate) {
   if(State==null)throw new InvalidOperationException("Sync war before contract.");
   byte[] wire=ContractWire(State,player);Save();return wire;
  }
 }
 public static byte[] ContractPlayer(byte[] player) {
  lock(Gate) {
   byte[] copy=(byte[])player.Clone();if(State!=null && State.Contract!=null && State.Contract.Started) { if(copy[48]!=State.Contract.Nation)copy[50]=255;copy[48]=(byte)State.Contract.Nation; }return copy;
  }
 }
 public static bool ContractCanFight(int nation) {
  lock(Gate) { return State==null || State.Contract==null || (State.Contract.Started && State.Contract.Remaining>0 && nation==State.Contract.Nation); }
 }
 static void ContractSelfTest(GkhCampaignState s) {
  GkhCampaignState test=Decode(Encode(s));test.Contract=null;test.LastContractRequest="";test.LastPlayerMoney=10000;test.LastPlayerNation=0;
  test.Owners=(int[])GkhWorld.InitialOwners.Clone();int[] rates=ContractRates(test);Assert(rates[0]==900 && rates[1]==1100 && rates[2]==1100,"contract weak-nation premium");
  for(int t=0;t<13;t++)test.Owners[t]=2;rates=ContractRates(test);Assert(rates[2]==900 && rates[0]==1100,"contract strength recalculation");test.Owners=(int[])GkhWorld.InitialOwners.Clone();
  string id=Guid.NewGuid().ToString();SelectContract(test,id,1,3);Assert(test.Contract.Amount==3300 && test.Contract.Remaining==3,"contract proportional payment");
  SelectContract(test,id,2,10);Assert(test.Contract.Nation==1 && test.Contract.Duration==3,"duplicate contract command");SelectContract(test,Guid.NewGuid().ToString(),2,10);Assert(test.Contract.Nation==1,"mid-contract nation switch");
  byte[] player=new byte[88];Buffer.BlockCopy(BitConverter.GetBytes(10000),0,player,40,4);player[48]=0;
  byte[] wire=ContractWire(test,player);Assert(Encoding.ASCII.GetString(wire,0,4)=="GKC2" && BitConverter.ToInt32(wire,4)==10000 && BitConverter.ToInt32(wire,8)==13300 && wire[12]==1,"contract wire");
  GkhCampaignState restart=Decode(Encode(test));Assert(restart.Contract.Started && restart.Contract.Remaining==3 && restart.Contract.Amount==3300,"contract restart");
  Assert(Convert.ToBase64String(ContractWire(restart,player))==Convert.ToBase64String(wire),"lost reply retry changed target");
  player[48]=1;Buffer.BlockCopy(BitConverter.GetBytes(13300),0,player,40,4);wire=ContractWire(restart,player);Assert(restart.Contract.PaymentConfirmed && BitConverter.ToInt32(wire,4)==-1,"payment acknowledgement");
  EndWar(restart,restart.StartTicks+restart.Settings.WarSeconds*Second,restart.Settings);Assert(restart.Contract.Remaining==2,"contract duration decrement");
  EndWar(restart,restart.StartTicks+restart.Settings.WarSeconds*Second,restart.Settings);EndWar(restart,restart.StartTicks+restart.Settings.WarSeconds*Second,restart.Settings);Assert(!ActiveContract(restart),"contract expiry");
  SelectContract(restart,Guid.NewGuid().ToString(),2,1);Assert(restart.Contract.Nation==2 && !restart.Contract.Started,"contract renewal");
  GkhCampaignState review=Decode(Encode(test));Buffer.BlockCopy(BitConverter.GetBytes(10001),0,player,40,4);wire=ContractWire(review,player);Assert(review.Contract.PaymentReview && BitConverter.ToInt32(wire,4)==-1,"ambiguous payment replay");
  test.Contract=null;test.LastPlayerMoney=9999999;SelectContract(test,Guid.NewGuid().ToString(),0,1);Assert(test.Contract==null,"money cap");
  ValidateContract(restart);ValidateContract(review);
 }

 static void Assert(bool condition,string reason) { if(!condition)throw new Exception("SELFTEST "+reason); }
 public static string SelfTest() {
  GkhCampaignSettings settings=new GkhCampaignSettings();settings.WarSeconds=60;settings.AiTickSeconds=10;settings.VoteWindowSeconds=20;
  GkhCampaignState s=new GkhCampaignState();s.War=new byte[138];Buffer.BlockCopy(BitConverter.GetBytes((uint)1),0,s.War,0,4);Buffer.BlockCopy(BitConverter.GetBytes((uint)60),0,s.War,8,4);s.Owners=(int[])GkhWorld.InitialOwners.Clone();s.Votes=new int[39];s.Settings=settings;s.Candidates=new int[13];s.StartTicks=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc).Ticks;s.NextAiTicks=s.StartTicks+10*Second;
  Vote(s);int[] targets=Pick(s);SetFronts(s,targets);s.Voted=false;Array.Clear(s.Votes,0,39);ValidateState(s);
  for(int n=0;n<3;n++) { Vote(s);int total=0;for(int t=0;t<13;t++) { total+=s.Votes[n*13+t];Assert(s.Votes[n*13+t]==0 || Eligible(s,n,t),"non-adjacent vote"); }Assert(total==20,"ballot total"); }
  Pick(s);for(int n=0;n<3;n++) { int top=s.BallotTargets[n];for(int t=0;t<13;t++)Assert(s.Votes[n*13+top]>=s.Votes[n*13+t],"top ballot ignored"); }
  ContractSelfTest(s);
  GkhCampaignState restored=Decode(Encode(s));ValidateState(restored);long begin=s.StartTicks;Advance(s,begin+180*Second,settings,1000);Advance(restored,begin+180*Second,settings,1000);Assert(Convert.ToBase64String(Encode(s))==Convert.ToBase64String(Encode(restored)),"restart trajectory");ValidateState(s);
  GkhCampaignState capture=Decode(Encode(s));int[] before=(int[])capture.Owners.Clone();int f=-1;for(int i=0;i<3;i++)if(capture.War[132+i]!=255) { f=i;break; }Assert(f>=0,"capture front missing");int land=capture.War[132+f],attacker=capture.War[135+f];Buffer.BlockCopy(BitConverter.GetBytes((uint)100000),0,capture.War,72+4*f,4);Buffer.BlockCopy(BitConverter.GetBytes((uint)0),0,capture.War,84+4*f,4);byte[] requested=(byte[])capture.War.Clone();EndWar(capture,capture.StartTicks+60*Second,settings);Assert(capture.Owners[land]==attacker && before[land]!=attacker,"winner did not acquire territory");ValidateState(capture);
  byte[] route=RouteWar(capture,requested);Assert(route.Length==140 && SameRound(route,requested) && !SameRound(route,capture.War),"march round changed");Assert(RouteOwners(capture,requested)[land]==before[land],"march owner changed");
  byte[] player=new byte[88],battle=new byte[24],enemy=new byte[]{(byte)before[land]};player[48]=(byte)attacker;Buffer.BlockCopy(BitConverter.GetBytes(4),0,battle,0,4);Buffer.BlockCopy(BitConverter.GetBytes((short)land),0,battle,8,2);string live=Convert.ToBase64String(capture.War);int owner=capture.Owners[land];string note=ApplyPlayer(capture,requested,player,battle,enemy,"test-key");Assert(note.IndexOf("archived")>=0 && Convert.ToBase64String(capture.War)==live && capture.Owners[land]==owner,"late result changed new war/ownership");Assert(ApplyPlayer(capture,requested,player,battle,enemy,"test-key").IndexOf("Duplicate")>=0,"duplicate credit");
  GkhCampaignState tied=Decode(Encode(s));Array.Clear(tied.War,72,24);int[] tieOwners=(int[])tied.Owners.Clone();EndWar(tied,tied.StartTicks+60*Second,settings);for(int t=0;t<13;t++)Assert(tied.Owners[t]==tieOwners[t],"tie changed territory");ValidateState(tied);
  GkhCampaignState conflict=Decode(Encode(s));conflict.Owners=(int[])GkhWorld.InitialOwners.Clone();conflict.Votes=new int[39];conflict.Votes[10]=20;conflict.Votes[23]=20;conflict.Votes[29]=20;conflict.Voted=true;int[] collision=Pick(conflict);Assert(conflict.BallotTargets[0]==10 && conflict.BallotTargets[1]==10 && conflict.BallotTargets[2]==3,"top targets lost on collision");Assert((collision[0]==10 && collision[1]==-1) || (collision[0]==-1 && collision[1]==10),"duplicate region scheduled");SetFronts(conflict,collision);ValidateState(conflict);
  for(int t=0;t<13;t++) { Assert(GkhWorld.NodeCounts[t]>0 && GkhWorld.NodeCounts[t]<=96,"invalid native node count");foreach(int other in GkhWorld.Edges[t])Assert(Array.IndexOf(GkhWorld.Edges[other],t)>=0 && other!=t,"asymmetric map edge"); }
  GkhCampaignState unified=Decode(Encode(s));for(int t=0;t<13;t++)unified.Owners[t]=0;unified.Voted=false;Vote(unified);SetFronts(unified,Pick(unified));ValidateState(unified);for(int i=0;i<3;i++)Assert(unified.War[132+i]==255 && unified.War[135+i]==3,"no-border front invented");
  GkhCampaignState isolated=Decode(Encode(s));isolated.Owners=(int[])GkhWorld.InitialOwners.Clone();isolated.Owners[3]=0;isolated.Owners[6]=2;isolated.Owners[8]=1;Assert(!Eligible(isolated,0,5),"isolated land enabled invasion");for(int n=0;n<3;n++)for(int t=0;t<3;t++)Assert(!Eligible(isolated,n,t),"capital entered ballot");
  for(int t=3;t<13;t++) {
   int defender=GkhWorld.InitialOwners[t],nodeAttacker=(defender+1)%3;byte[] split=RegionRecord(t,defender,7,true,nodeAttacker);int friendly=0,hostile=0,frontiers=0;
   for(int node=0;node<GkhWorld.NodeCounts[t];node++) { int side=split[node*12+8];if(side==defender)friendly++;else if(side==nodeAttacker)hostile++;else throw new Exception("SELFTEST unrelated nation inside a battlefield");if((split[node*12+9]&1)!=0)frontiers++; }
   Assert(friendly>0 && hostile>0 && frontiers>0,"battlefield missing sides/frontiers");Assert(split[0x488]==defender && BitConverter.ToUInt32(split,0x480)==1 && BitConverter.ToUInt32(split,0x484)==7,"node split changed political ownership/votes");
   for(int node=GkhWorld.NodeCounts[t];node<96;node++)Assert(split[node*12+8]==3,"unused node became battle site");
   byte[] quiet=RegionRecord(t,defender,0,false);for(int node=0;node<GkhWorld.NodeCounts[t];node++)Assert(quiet[node*12+8]==defender && (quiet[node*12+9]&1)==0,"inactive territory became a battlefield");
  }
  byte[] visible=RegionRecord(5,2,7,true);Assert(visible.Length==1164 && BitConverter.ToUInt32(visible,0x480)==1 && BitConverter.ToUInt32(visible,0x484)==7 && visible[0x488]==2,"native AI/player vote counters");Assert(BitConverter.ToUInt32(visible,0x480)+BitConverter.ToUInt32(visible,0x484)==8,"council vote total");
  byte[] corrupt=Encode(s);corrupt[15]^=1;bool rejected=false;try { Decode(corrupt); }catch(InvalidDataException) { rejected=true; }Assert(rejected,"state CRC");
  GkhCampaignState rollover=Decode(Encode(s));Buffer.BlockCopy(BitConverter.GetBytes((uint)59),0,rollover.War,4,4);long boundary=rollover.StartTicks+60*Second;Advance(rollover,boundary,settings,1000);Assert(BitConverter.ToUInt32(rollover.War,0)==2 && BitConverter.ToUInt32(rollover.War,4)==0,"campaign rollover");uint round=BitConverter.ToUInt32(rollover.War,4);Advance(rollover,boundary,settings,1000);Assert(BitConverter.ToUInt32(rollover.War,4)==round,"boundary twice");
  return "PASS: original attack/defense node sides, native battle-frontier flags, political ownership/vote preservation, nation contracts, strength prices, proportional payment, duplicate prevention, payment retries/acknowledgement, expiry/renewal, contract restart, supplied hostile non-capital ballots, native AI vote counters, per-nation top targets, autonomous wars, winner ownership, defender/tie retention, no-border idle, restart, route preservation, late-result isolation/dedup, CRC, rollover.";
 }
}

public static class GkhEncounterPolicy {
 sealed class Recent { public long Ticks;public bool Populated; }
 static int Fights=1, Clear=2;
 static long[] Counts=new long[3];
 static Dictionary<string,Recent> Cache=new Dictionary<string,Recent>();
 public static void Configure(int fights,int clear) {
  if(fights<0 || fights>20 || clear<0 || clear>20 || fights+clear<1)throw new InvalidDataException("Encounter pattern must contain 1..40 slots, 0..20 battle/clear slots each.");
  Fights=fights;Clear=clear;Counts=new long[3];Cache.Clear();
 }
 static bool Select(long count,int fights,int clear) { return count%(fights+clear)<fights; }
 public static bool Populate(byte[] query,int index) {
  int nation=query[0];if(nation>2)throw new InvalidDataException("Invalid encounter nation.");
  string key=Convert.ToBase64String(query)+":"+index;long now=DateTime.UtcNow.Ticks;Recent old;
  // Immediate retries receive the same army; retries do not consume new pattern slots.
  if(Cache.TryGetValue(key,out old) && now>=old.Ticks && now-old.Ticks<5*TimeSpan.TicksPerSecond)return old.Populated;
  bool result=Select(Counts[nation]++,Fights,Clear);
  if(Cache.Count>256)Cache.Clear();Cache[key]=new Recent { Ticks=now,Populated=result };return result;
 }
 public static string SelfTest() {
  for(int i=0;i<30;i++)if(Select(i,1,2)!=(i%3==0))throw new Exception("SELFTEST encounter spacing failed.");
  for(int i=0;i<30;i++)if(Select(i,0,2) || !Select(i,2,0))throw new Exception("SELFTEST encounter configuration failed.");
  return "PASS: encounter pattern 1 battle / 2 empty slots; all-empty/all-battle modes.";
 }
}
public static class GkhWorld {
 public static readonly string[] DefenderMasks=new string[]{"00000000000000000000000000000000000000000000000000000000000000000000000000000000000","000000000000000000000000000000000000000000000000000000000000000000000000000000000","000000000000000000000000000000000000000000000000000000000000000000000000000000000000","1111111111111111111111111100000000000001111100000000000000000000011110011111000000","000000000000000000000000000010010001111101111111111111111111011100010001011011111111","00000000000000000000000111111111111111111111111101101011011100000000000001011111111","000000000000000000000000000001111011000001001000110111111111111111111111111111111","0000000000000000000000000011001011101001001110110111111111111111111111111111111000000","00000000000000000000000000000000000000000000001111111111111111111111111111111111111111111111","00000000000000000000001000011111001111111111111111111111110111111111111001000000000000","00000000000000000000000000111111111111111111111111111111111111110000000100100000010","111111111111111111111111111001010110110000000000001111111111000000000000000000000000000","00000000000001000000001000001111111101111001001011111111111111111100100"};
 public static readonly int[][][] NodeNeighbors=new int[][][]{
new int[][]{new int[]{1},new int[]{2,0},new int[]{3,1},new int[]{6,2},new int[]{7},new int[]{8},new int[]{7,9,3},new int[]{4,6},new int[]{5,10,10},new int[]{6,37},new int[]{8,11,8},new int[]{12,28,10},new int[]{13,11},new int[]{14,12},new int[]{17,17,29,13},new int[]{16},new int[]{15},new int[]{18,14,14},new int[]{19,17},new int[]{20,18},new int[]{21,19},new int[]{20,22},new int[]{21,26},new int[]{24},new int[]{25,23},new int[]{26,27,24},new int[]{22,25},new int[]{25,76},new int[]{11,31},new int[]{14,33,33},new int[]{31,40},new int[]{28,32,30},new int[]{31},new int[]{29,29,36},new int[]{35,43},new int[]{34},new int[]{33,69},new int[]{9,38,47},new int[]{39,37},new int[]{42,42,38},new int[]{30},new int[]{59},new int[]{39,43,39},new int[]{34,60,42},new int[]{45},new int[]{46,44},new int[]{47,48,45},new int[]{37,46},new int[]{46,50},new int[]{50,51,51},new int[]{48,52,49},new int[]{49,49,53},new int[]{50,55,55},new int[]{51,54},new int[]{57,53},new int[]{52,61,52},new int[]{65},new int[]{58,54},new int[]{66,57},new int[]{41,61},new int[]{43,65},new int[]{59,62,55},new int[]{63,61},new int[]{64,62},new int[]{65,63},new int[]{60,70,56,64},new int[]{67,58},new int[]{68,66},new int[]{67},new int[]{36,73},new int[]{71,65},new int[]{72,70},new int[]{73,71},new int[]{69,74,72},new int[]{77,73},new int[]{80},new int[]{27,79},new int[]{78,80,74},new int[]{79,77},new int[]{76,81,81,78},new int[]{77,75},new int[]{79,82,79},new int[]{81}},
new int[][]{new int[]{1},new int[]{3,0},new int[]{8,5},new int[]{4,1},new int[]{5,3},new int[]{2,51,51,4},new int[]{7,9},new int[]{6},new int[]{9,2},new int[]{6,8},new int[]{41,41},new int[]{12},new int[]{13,14,11},new int[]{15,15,12},new int[]{12,17},new int[]{13,16,18,13},new int[]{19,15},new int[]{14},new int[]{15,43,43},new int[]{20,24,16},new int[]{21,19},new int[]{22,25,20},new int[]{23,21},new int[]{22},new int[]{19,26},new int[]{21,28},new int[]{24,27,44},new int[]{26},new int[]{25,29},new int[]{28,31,31},new int[]{31,32,50},new int[]{29,29,30},new int[]{30,33,33},new int[]{32,34,32},new int[]{35,33},new int[]{34},new int[]{47,38},new int[]{38},new int[]{36,39,37},new int[]{40,38},new int[]{39},new int[]{10,42,10},new int[]{43,41},new int[]{18,18,42},new int[]{26,45},new int[]{44},new int[]{47,58},new int[]{48,36,46},new int[]{49,47},new int[]{50,48},new int[]{30,49},new int[]{5,52,5},new int[]{53,54,51},new int[]{55,52},new int[]{52,56},new int[]{57,53},new int[]{54},new int[]{55,58,61},new int[]{46,57},new int[]{60,62},new int[]{61,59},new int[]{57,60},new int[]{59,73},new int[]{64,74},new int[]{65,63},new int[]{66,64},new int[]{67,65},new int[]{66},new int[]{80,71},new int[]{70},new int[]{71,69},new int[]{68,72,70},new int[]{73,71},new int[]{62,74,72},new int[]{73,63},new int[]{76},new int[]{75,77},new int[]{78,80,76},new int[]{79,77},new int[]{78},new int[]{77,68}},
new int[][]{new int[]{1},new int[]{4,0},new int[]{3},new int[]{5,5,2},new int[]{1,7},new int[]{3,6,3},new int[]{7,5},new int[]{4,8,6},new int[]{9,7},new int[]{25,8},new int[]{11,27},new int[]{12,10},new int[]{13,11},new int[]{14,12},new int[]{13},new int[]{16},new int[]{17,19,15},new int[]{18,16},new int[]{17},new int[]{16,39},new int[]{21},new int[]{22,20},new int[]{23,21},new int[]{24,22},new int[]{28,23},new int[]{26,9},new int[]{27,29,25},new int[]{10,26},new int[]{30,24},new int[]{26,31},new int[]{56,28},new int[]{29,58},new int[]{33},new int[]{34,64,32},new int[]{35,33},new int[]{36,34},new int[]{35},new int[]{42,67},new int[]{39,42},new int[]{19,40,38},new int[]{41,39},new int[]{40},new int[]{38,38,43,37},new int[]{42,73},new int[]{77},new int[]{46},new int[]{47,50,45},new int[]{48,46},new int[]{49,47},new int[]{48},new int[]{46,52},new int[]{52},new int[]{50,53,51},new int[]{54,52},new int[]{55,53},new int[]{60,60,54},new int[]{30,57,60,60},new int[]{58,61,56},new int[]{31,59,57},new int[]{64,58},new int[]{56,56,55,55},new int[]{57,62},new int[]{61},new int[]{69},new int[]{33,65,59},new int[]{66,64},new int[]{67,68,65},new int[]{37,66},new int[]{66,71},new int[]{70,63},new int[]{71,69},new int[]{68,72,70},new int[]{71},new int[]{43,78},new int[]{75},new int[]{76,79,74},new int[]{77,75},new int[]{44,80,76},new int[]{73,81},new int[]{75},new int[]{77,83},new int[]{82,78},new int[]{83,81},new int[]{80,82}},
new int[][]{new int[]{1},new int[]{2,0},new int[]{3,3,1},new int[]{2,73,2},new int[]{7},new int[]{8},new int[]{9},new int[]{4,8,10,10},new int[]{5,7},new int[]{6,12},new int[]{7,7,65},new int[]{12,67},new int[]{9,39,11},new int[]{14,14},new int[]{13,13,16},new int[]{20,17,17},new int[]{14,40},new int[]{15,15,42},new int[]{43},new int[]{21},new int[]{23,23,15},new int[]{19,24},new int[]{25},new int[]{20,24,20},new int[]{21,25,23},new int[]{22,24},new int[]{27,28,28},new int[]{30,26},new int[]{26,29,47},new int[]{32,32,28},new int[]{31,27},new int[]{30},new int[]{29,34,29},new int[]{51,37,51},new int[]{32,35,38},new int[]{36,34},new int[]{35},new int[]{33},new int[]{34},new int[]{40,12},new int[]{16,41,39},new int[]{42,40},new int[]{17,43,41},new int[]{18,44,42},new int[]{43,47},new int[]{48},new int[]{47,50,50},new int[]{44,28,51,46},new int[]{45,49,59},new int[]{50,53,48},new int[]{46,46,49},new int[]{47,33,33},new int[]{54},new int[]{49,55},new int[]{52},new int[]{53},new int[]{57,60,79},new int[]{58,56},new int[]{69,57},new int[]{48,61,70},new int[]{56,62},new int[]{59,64,64},new int[]{60},new int[]{64},new int[]{61,61,63},new int[]{10,66,74},new int[]{67,68,65},new int[]{11,66},new int[]{66,69,69},new int[]{68,70,58,68},new int[]{59,69},new int[]{72,75},new int[]{73,71},new int[]{3,74,72},new int[]{65,73},new int[]{71},new int[]{77},new int[]{78,76},new int[]{79,77},new int[]{78,56,81,81},new int[]{81},new int[]{79,79,80}},
new int[][]{new int[]{1},new int[]{29,0},new int[]{3},new int[]{4,2},new int[]{5,3},new int[]{6,4},new int[]{20,5},new int[]{8},new int[]{9,7},new int[]{10,8},new int[]{11,9},new int[]{10,16,13,13},new int[]{13},new int[]{11,11,12},new int[]{15,16},new int[]{26,17,14},new int[]{14,11},new int[]{18,19,15},new int[]{75,17},new int[]{17},new int[]{22,22,6},new int[]{33,24},new int[]{20,23,20},new int[]{24,26,22},new int[]{21,25,23},new int[]{64,24},new int[]{23,15},new int[]{68},new int[]{31},new int[]{30,1},new int[]{32,29},new int[]{28,37,33},new int[]{33,30},new int[]{31,21,32},new int[]{40},new int[]{36,39,39},new int[]{43,35},new int[]{38,31},new int[]{39,37},new int[]{35,35,38},new int[]{60,34},new int[]{42},new int[]{47,41},new int[]{44,45,36},new int[]{46,46,43},new int[]{43,47},new int[]{44,82,49,44},new int[]{45,48,42},new int[]{47},new int[]{46,51},new int[]{52,52,63},new int[]{49,53},new int[]{50,53,50},new int[]{51,54,52},new int[]{55,59,53},new int[]{54},new int[]{57,78,71},new int[]{58,56},new int[]{57},new int[]{54,79,79},new int[]{40,64},new int[]{62,65,65},new int[]{63,61},new int[]{50,67,62},new int[]{60,65,25},new int[]{61,61,64},new int[]{70},new int[]{63,71},new int[]{69,72,27},new int[]{70,68},new int[]{66,69},new int[]{67,56},new int[]{68,75},new int[]{74,76,76},new int[]{77,77,73},new int[]{72,76,18},new int[]{73,73,75},new int[]{74,80,74},new int[]{56},new int[]{59,59,81,81},new int[]{81,77},new int[]{79,79,80},new int[]{83,46},new int[]{82}},
new int[][]{new int[]{1},new int[]{2,0},new int[]{3,1},new int[]{7,2},new int[]{5},new int[]{60,60,4},new int[]{12,61,61},new int[]{8,12,3},new int[]{9,7},new int[]{10,8},new int[]{9},new int[]{17,15,15},new int[]{7,13,6},new int[]{14,53,12},new int[]{15,13},new int[]{11,11,14},new int[]{19},new int[]{18,11},new int[]{19,17},new int[]{16,20,18},new int[]{19,21,24},new int[]{22,20},new int[]{21},new int[]{24,25,25},new int[]{20,27,23},new int[]{23,23,55},new int[]{28},new int[]{29,24},new int[]{26,31},new int[]{27,32},new int[]{31,50},new int[]{28,33,30},new int[]{29,34},new int[]{34,31},new int[]{32,35,36,33},new int[]{34},new int[]{34,38},new int[]{44,39,39,44},new int[]{36,41},new int[]{37,42,37},new int[]{41},new int[]{38,40},new int[]{39},new int[]{51,82,45,45},new int[]{52,37,37,52},new int[]{43,43,46},new int[]{45,81},new int[]{58,49},new int[]{51,51,71},new int[]{50,52,47},new int[]{30,49},new int[]{48,43,48},new int[]{49,44,44,82},new int[]{13,54,54},new int[]{53,58,53},new int[]{25,59},new int[]{57,68,68},new int[]{58,56},new int[]{54,59,47,57},new int[]{55,58},new int[]{5,61,63,5},new int[]{6,6,60},new int[]{63,64,64},new int[]{60,65,65,62},new int[]{62,62,66},new int[]{63,67,67,63},new int[]{64,69},new int[]{65,70,70,65},new int[]{56,56,71},new int[]{66,72},new int[]{67,71,67},new int[]{68,48,70},new int[]{69,74,74},new int[]{76},new int[]{72,77,72},new int[]{76,78},new int[]{73,75},new int[]{74,79},new int[]{75,80},new int[]{80,77},new int[]{78,81,79},new int[]{46,80},new int[]{52,52,43,43}},
new int[][]{new int[]{1},new int[]{2,0},new int[]{3,1},new int[]{4,4,2},new int[]{3,27,7},new int[]{6},new int[]{7,5},new int[]{4,8,6},new int[]{7,9},new int[]{8,10,11},new int[]{36,9},new int[]{9,12},new int[]{11,14},new int[]{14,17,17},new int[]{12,15,18,13},new int[]{42,14},new int[]{17},new int[]{13,13,16},new int[]{14,20,20},new int[]{21},new int[]{18,46,18},new int[]{19,24},new int[]{23},new int[]{24,22},new int[]{21,25,23},new int[]{26,24},new int[]{50,50,25},new int[]{28,4},new int[]{29,27},new int[]{31,31,28},new int[]{31,34},new int[]{29,32,35,30},new int[]{78,31},new int[]{36},new int[]{30,37},new int[]{31,68,31},new int[]{33,10},new int[]{34,38},new int[]{37,41,39,39},new int[]{38,38,42,42},new int[]{43},new int[]{38,44,44,38},new int[]{39,39,15},new int[]{40,45,45},new int[]{41,61,41},new int[]{43,43,47},new int[]{47,50,20},new int[]{45,51,46},new int[]{49,51},new int[]{54,48},new int[]{46,26,26},new int[]{48,52,47},new int[]{51,53,51},new int[]{57,52},new int[]{66,49},new int[]{67,56},new int[]{55,59},new int[]{58,53},new int[]{59,57},new int[]{56,58},new int[]{75,62},new int[]{62,44},new int[]{60,63,66,61},new int[]{64,62},new int[]{65,63},new int[]{64},new int[]{62,54},new int[]{55},new int[]{69,35},new int[]{70,68},new int[]{80,69},new int[]{80,72},new int[]{71,75},new int[]{74},new int[]{75,73},new int[]{72,60,74},new int[]{78},new int[]{79},new int[]{76,32},new int[]{77,80},new int[]{79,71,70}},
new int[][]{new int[]{1},new int[]{2,3,0},new int[]{4,4,1},new int[]{1,6},new int[]{2,5,2},new int[]{21,4},new int[]{3,7,7},new int[]{6,8,10,6},new int[]{9,7},new int[]{8},new int[]{7,11,11},new int[]{10,12,12,10},new int[]{11,37,37,11},new int[]{79,79},new int[]{15,80},new int[]{40,81,14},new int[]{17,21},new int[]{18,22,16},new int[]{19,17},new int[]{20,23,18},new int[]{45,19},new int[]{16,24,5},new int[]{17,25},new int[]{19},new int[]{21,28},new int[]{22,29},new int[]{27,30,30},new int[]{50,26},new int[]{24,31},new int[]{25},new int[]{26,26,32},new int[]{32,28},new int[]{30,33,35,31},new int[]{36,36,32},new int[]{55},new int[]{32,38},new int[]{33,39,33},new int[]{12,40,12},new int[]{35,41},new int[]{36,60,43},new int[]{37,41,15},new int[]{38,40},new int[]{43,73},new int[]{39,64,42},new int[]{47},new int[]{46,48,20},new int[]{47,45},new int[]{44,49,46},new int[]{45},new int[]{47,52},new int[]{51,53,27},new int[]{52,50},new int[]{49,51},new int[]{50,54,50},new int[]{56,53},new int[]{57,34},new int[]{59,54},new int[]{58,55},new int[]{59,57},new int[]{56,61,58},new int[]{62,62,39},new int[]{59,63},new int[]{60,66,66,60},new int[]{61,67},new int[]{43,65,43},new int[]{70,70,64},new int[]{62,62},new int[]{63,71},new int[]{69,78},new int[]{68},new int[]{65,71,65},new int[]{67,72,70},new int[]{71},new int[]{42,75,75},new int[]{75,84},new int[]{73,76,74},new int[]{77,75},new int[]{78,76},new int[]{68,77},new int[]{13,80,82,13},new int[]{14,79},new int[]{15},new int[]{79,83,83,79},new int[]{84,82},new int[]{74,83}},
new int[][]{new int[]{1,3,3},new int[]{2,0},new int[]{38,38,1},new int[]{0,0,4},new int[]{3},new int[]{38,38,9,9},new int[]{7},new int[]{8,10,6},new int[]{9,7},new int[]{5,11,8},new int[]{7,12},new int[]{9,31,9},new int[]{10,13,14},new int[]{12},new int[]{12,16},new int[]{36,21,17,17},new int[]{14,18,18},new int[]{15,15,19},new int[]{16,19,16},new int[]{17,20,18},new int[]{26,19},new int[]{22,15},new int[]{21},new int[]{24,25},new int[]{37,65,65,23},new int[]{23,28,28},new int[]{27,20},new int[]{28,26},new int[]{25,25,27},new int[]{43,44,33},new int[]{45,35},new int[]{32,36,11},new int[]{33,31},new int[]{29,34,32},new int[]{35,33},new int[]{30,55,37,34},new int[]{31,15,15},new int[]{35,24},new int[]{2,39,5,5},new int[]{40,43,38},new int[]{41,39},new int[]{42,40},new int[]{47,41},new int[]{39,29},new int[]{29,29},new int[]{47,47,30},new int[]{47},new int[]{46,48,45,42},new int[]{49,47},new int[]{50,48},new int[]{51,49},new int[]{86,50},new int[]{53},new int[]{54,52},new int[]{90,90,59,53},new int[]{56,35},new int[]{57,60,55},new int[]{58,56},new int[]{59,57},new int[]{54,79,58},new int[]{56,63,63,56},new int[]{62},new int[]{63,61},new int[]{60,64,62},new int[]{71,63},new int[]{66,70,24},new int[]{67,67,65},new int[]{66,68,66},new int[]{69,67},new int[]{73,68},new int[]{65},new int[]{81,73,64},new int[]{74,74},new int[]{71,75,69},new int[]{72,72,76},new int[]{76,73},new int[]{74,77,75},new int[]{76},new int[]{91,80},new int[]{81,59},new int[]{78,83},new int[]{79,82,71,79},new int[]{83,81},new int[]{80,84,82},new int[]{83},new int[]{86},new int[]{85,87,90,51},new int[]{88,86},new int[]{89,91,87},new int[]{88},new int[]{86,54,54},new int[]{88,78}},
new int[][]{new int[]{1},new int[]{0,4},new int[]{3,84},new int[]{81,2},new int[]{1,5},new int[]{4,6},new int[]{5,7,11},new int[]{85,8,6},new int[]{9,7},new int[]{74,8},new int[]{12},new int[]{6,13,13,6},new int[]{10,15},new int[]{11,14,14,11},new int[]{13,23,23,13},new int[]{12,16},new int[]{17,15},new int[]{18,16},new int[]{23,23,17},new int[]{76},new int[]{21,24},new int[]{77,22,25,20},new int[]{27,21},new int[]{14,24,18,14},new int[]{20,23},new int[]{21,26},new int[]{32,25},new int[]{69,28,22},new int[]{30,30,27},new int[]{70},new int[]{28,31,28},new int[]{34,34,30},new int[]{33,26},new int[]{32},new int[]{31,41,31},new int[]{47,37},new int[]{50,50,38,38},new int[]{35,41},new int[]{36,39,38,42},new int[]{40,38},new int[]{39},new int[]{37,42,34},new int[]{38,41},new int[]{44},new int[]{45,43},new int[]{57,46,44},new int[]{45,50,45},new int[]{48,35},new int[]{49,47},new int[]{48},new int[]{46,36,36},new int[]{52},new int[]{51,53,56},new int[]{54,52},new int[]{55,53},new int[]{57,54},new int[]{52,65,65},new int[]{55,45},new int[]{59,83},new int[]{60,58},new int[]{61,59},new int[]{62,62,60},new int[]{61,65,65,61},new int[]{64,73},new int[]{67,63},new int[]{56,68,62},new int[]{69},new int[]{68,64},new int[]{65,65,70,67},new int[]{66,27},new int[]{68,29},new int[]{72,74},new int[]{75,71},new int[]{63,75},new int[]{71,76,9},new int[]{73,77,72},new int[]{74,19},new int[]{75,21},new int[]{79,81},new int[]{80,78},new int[]{82,82,79},new int[]{78,3},new int[]{80,83,80},new int[]{58,82},new int[]{2,2,85,85},new int[]{84,84,7,7}},
new int[][]{new int[]{1},new int[]{2,0},new int[]{3,1},new int[]{5,5,2},new int[]{5,6},new int[]{3,22,4},new int[]{4,4,8},new int[]{9},new int[]{6,12},new int[]{7,67,13},new int[]{11},new int[]{12,10},new int[]{8,14,14,11},new int[]{9,15},new int[]{12,15,12},new int[]{13,75,14},new int[]{19},new int[]{18,22},new int[]{19,17},new int[]{16,20,18},new int[]{21,19},new int[]{25,20},new int[]{17,23,5},new int[]{64,22},new int[]{65},new int[]{26,26,21},new int[]{25,27,25},new int[]{28,26},new int[]{29,53,27},new int[]{34,34,28},new int[]{31},new int[]{30,35},new int[]{33,33,36,36},new int[]{32,32},new int[]{29,35,29},new int[]{31,36,34},new int[]{32,32,38,35},new int[]{39,59},new int[]{36,40,40},new int[]{37,40,41},new int[]{38,38,39},new int[]{39,42,42,46},new int[]{41,43,43,41},new int[]{42,44,42},new int[]{43},new int[]{61},new int[]{41,51,63},new int[]{48,81},new int[]{49,47},new int[]{50,52,48},new int[]{51,49},new int[]{46,50},new int[]{49},new int[]{28,28,56,56},new int[]{59},new int[]{56,71},new int[]{53,53,55},new int[]{58},new int[]{59,57},new int[]{54,37,58},new int[]{61,74},new int[]{62,45,60},new int[]{63,61},new int[]{46,62},new int[]{65,23},new int[]{24,66,64},new int[]{71,65},new int[]{68,9},new int[]{67},new int[]{70,72,72},new int[]{71,73,69},new int[]{66,55,74,70},new int[]{69,69,76},new int[]{70,77},new int[]{71,60},new int[]{76,78,15},new int[]{72,79,75},new int[]{73},new int[]{75,82},new int[]{80,76},new int[]{81,79},new int[]{47,80},new int[]{78}},
new int[][]{new int[]{1,4,4},new int[]{2,0},new int[]{7,1},new int[]{4},new int[]{0,0,3},new int[]{6,80,80},new int[]{13,5},new int[]{10,10,2},new int[]{11},new int[]{17,12,12},new int[]{7,14,7},new int[]{8,15},new int[]{9,9,16},new int[]{14,6},new int[]{10,15,13},new int[]{11,16,14},new int[]{12,59,59,15},new int[]{18,9},new int[]{19,17},new int[]{20,22,18},new int[]{21,19},new int[]{26,20},new int[]{19,23,23},new int[]{22,22,50},new int[]{25,51},new int[]{33,33,24},new int[]{29,29,21},new int[]{28},new int[]{30,30,27},new int[]{26,31,31,26},new int[]{28,32,28},new int[]{29,34,29},new int[]{30,35},new int[]{25,36,25},new int[]{31,37},new int[]{32,38},new int[]{33,58,58},new int[]{34,39,39},new int[]{35,40},new int[]{37,37,41},new int[]{38,43,43},new int[]{39,42,44,44},new int[]{43,41},new int[]{40,40,42},new int[]{41,41,49},new int[]{46,69},new int[]{47,45},new int[]{48,46},new int[]{49,47},new int[]{44,48},new int[]{23,53},new int[]{24,54},new int[]{55},new int[]{50,56,56},new int[]{51,55},new int[]{52,54},new int[]{53,57,53},new int[]{58,56},new int[]{36,36,57},new int[]{16,64,16},new int[]{61,84,84},new int[]{62,60},new int[]{63,61},new int[]{64,62},new int[]{59,65,63},new int[]{64,69},new int[]{67,70,70},new int[]{68,66},new int[]{69,67},new int[]{65,45,71,68},new int[]{66,66,75},new int[]{69,69,72,72},new int[]{71,71},new int[]{86,74,77},new int[]{75,73},new int[]{70,74},new int[]{77},new int[]{73,76},new int[]{79},new int[]{80,85,78},new int[]{5,5,79},new int[]{85,82,86},new int[]{83,81},new int[]{84,82},new int[]{60,60,83},new int[]{79,81},new int[]{81,73}},
new int[][]{new int[]{1},new int[]{0,3},new int[]{4,4},new int[]{1,5},new int[]{2,6,2},new int[]{3,7,7},new int[]{4,7,9},new int[]{5,6},new int[]{36,36,10,10},new int[]{6,11},new int[]{8,8,12},new int[]{9,14},new int[]{10,13,15},new int[]{28,12},new int[]{11,16},new int[]{12,17,17},new int[]{14,18,18},new int[]{15,19,15},new int[]{16,20,20,16},new int[]{17,21},new int[]{21,18},new int[]{19,24,20},new int[]{35,35},new int[]{66,66,27,27},new int[]{25,21},new int[]{26,24},new int[]{27,25},new int[]{23,23,26},new int[]{29,31,13},new int[]{32,32,28},new int[]{40,34,40},new int[]{28,35},new int[]{29,33,29},new int[]{34,32},new int[]{30,63,33},new int[]{31,22,22},new int[]{47,8,8},new int[]{48,48,38},new int[]{37,40,40},new int[]{54,54,40},new int[]{38,39,30,38},new int[]{44},new int[]{45},new int[]{46},new int[]{41,47},new int[]{42,47,47},new int[]{43,48},new int[]{44,45,36},new int[]{46,37,37},new int[]{50,51,51},new int[]{49},new int[]{49,49,52},new int[]{51,53,54,54},new int[]{55,55,52},new int[]{52,52,39,39},new int[]{53,56,53},new int[]{55},new int[]{58,61},new int[]{57},new int[]{60,63},new int[]{61,64,59},new int[]{57,62,62,60},new int[]{61,61},new int[]{59,34},new int[]{60,65,65},new int[]{64,68,64},new int[]{67,23},new int[]{66,69,69,66},new int[]{65},new int[]{67,70,67},new int[]{69}}
 };

 public static readonly int[] InitialOwners=new int[]{0,1,2,0,2,2,0,1,0,0,2,1,1};
 public static readonly int[] NodeCounts=new int[]{83,81,84,82,84,83,81,85,92,86,83,87,71};
 public static readonly int[][] Edges=new int[][]{new int[]{4,6,9},new int[]{7,9,11},new int[]{4,10,11},new int[]{5,7,8,10},new int[]{0,2,5},new int[]{3,4,6,10},new int[]{0,5,8},new int[]{1,3,8,12},new int[]{3,6,7,9},new int[]{0,1,8},new int[]{2,3,5,12},new int[]{1,2,12},new int[]{7,10,11}};
}
