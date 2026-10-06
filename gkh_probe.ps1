param([ValidateSet('patch','capture','collect','backup','contractpatch')][string]$Action = 'contractpatch', [string]$Source)
$ErrorActionPreference = 'Stop'
$TaskRoot = $PSScriptRoot
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Net.Sockets;
public sealed class GkhRequest {
 public string Method, Path, RawHeader;
 public Dictionary<string,string> Headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
 public byte[] Body;
}
public static class GkhProbe {
 const int OverlaySize = 3467392;
 const int HostOffset = 0x293B18, SchemeOffset = 0x293B30;
 const string Hash = "05385f422345e8420b7becc19847a2b99666be33cba958c17b73126783fbfd41";
 public static byte[] Exact(Stream s, int count) {
  byte[] data = new byte[count]; int at = 0;
  while(at < count) { int n = s.Read(data, at, count-at); if(n == 0) throw new EndOfStreamException("Unexpected end of file/request."); at += n; }
  return data;
 }
 static long U32(byte[] b, int p) { return (long)BitConverter.ToUInt32(b,p); }
 static byte[] Find(Stream s, byte[] record, string part) {
  long loc=U32(record,2)*2048; int size=checked((int)U32(record,10));
  if(size > 16*1024*1024) throw new InvalidDataException("Unexpected directory size.");
  s.Position=loc; byte[] dir=Exact(s,size); byte[] found=null;
  for(int pos=0;pos<dir.Length;) {
   int len=dir[pos]; if(len==0) { pos=(pos/2048+1)*2048;continue; }
   if(len<34 || pos+len>dir.Length) throw new InvalidDataException("Invalid ISO directory.");
   int namelen=dir[pos+32];if(33+namelen>len)throw new InvalidDataException("Invalid ISO name.");
   string name=Encoding.ASCII.GetString(dir,pos+33,namelen).Split(';')[0];
   if(String.Equals(name,part,StringComparison.OrdinalIgnoreCase)) {
    if(found!=null)throw new InvalidDataException("Duplicate ISO entry.");
    found=new byte[len];Buffer.BlockCopy(dir,pos,found,0,len);
   }
   pos+=len;
  }
  if(found==null)throw new InvalidDataException("ISO entry missing: "+part);
  return found;
 }
 static long Locate(Stream s) {
  s.Position=16*2048;byte[] pvd=Exact(s,2048);
  if(pvd[0]!=1 || Encoding.ASCII.GetString(pvd,1,5)!="CD001" || pvd[6]!=1 || BitConverter.ToUInt16(pvd,128)!=2048)
   throw new InvalidDataException("Choose an extracted ISO file, not EGG/ZIP/CSO.");
  int n=pvd[156];if(n<34)throw new InvalidDataException("Invalid root directory.");
  byte[] rec=new byte[n];Buffer.BlockCopy(pvd,156,rec,0,n);
  foreach(string part in new string[]{"PSP_GAME","USRDIR","OVERLAY_MO4UMD.BIN"})rec=Find(s,rec,part);
  if(U32(rec,10)!=OverlaySize || (rec[25]&130)!=0 || rec[26]!=0 || rec[27]!=0)
   throw new InvalidDataException("Unexpected overlay layout/size.");
  return U32(rec,2)*2048;
 }
 public static string Sha(byte[] b) {
  using(SHA256 h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(b)).Replace("-","").ToLowerInvariant();
 }
 static void Edit(byte[] data,int off,string original,string replacement) {
  byte[] old=Encoding.ASCII.GetBytes(original+"\0");byte[] value=Encoding.ASCII.GetBytes(replacement);
  if(value.Length>=old.Length)throw new InvalidDataException("Replacement too long.");
  for(int i=0;i<old.Length;i++)if(data[off+i]!=old[i])throw new InvalidDataException("Original string mismatch.");
  Array.Clear(data,off,old.Length);Buffer.BlockCopy(value,0,data,off,value.Length);
 }
 public static string Patch(string source) {
  source=System.IO.Path.GetFullPath(source);string ext=System.IO.Path.GetExtension(source);
  if(!String.Equals(ext,".iso",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Select the extracted .iso file.");
  string target=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source),System.IO.Path.GetFileNameWithoutExtension(source)+"_CONNECT_TEST.iso");
  if(File.Exists(target))throw new IOException("Output already exists. Move the existing CONNECT_TEST.iso first.");
  bool created=false;
  using(FileStream input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read)) {
   long offset=Locate(input);input.Position=offset;byte[] overlay=Exact(input,OverlaySize);
   if(Sha(overlay)!=Hash)throw new InvalidDataException("Game version differs from the uploaded Korean overlay. Nothing changed.");
   Edit(overlay,HostOffset,"ristia.gkh-game-zzz.jp","127.0.0.1");Edit(overlay,SchemeOffset,"https://","http://");
   try {
    using(FileStream output=new FileStream(target,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None)) {
     created=true;input.Position=0;input.CopyTo(output,8*1024*1024);output.Position=offset;output.Write(overlay,0,overlay.Length);output.Flush();
     if(output.Length!=input.Length)throw new IOException("ISO size changed.");
     output.Position=offset;if(Sha(Exact(output,OverlaySize))!=Sha(overlay))throw new IOException("Verification failed.");
    }
   } catch { if(created)File.Delete(target);throw; }
  }
  return target;
 }

 public static string PatchContract(string source,string originalHex,string replacementHex,int patchOffset) {
  source=Path.GetFullPath(source);if(!String.Equals(Path.GetExtension(source),".iso",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Select an ISO.");
  string target=Path.Combine(Path.GetDirectoryName(source),Path.GetFileNameWithoutExtension(source)+"_CONTRACT_v12.iso");if(File.Exists(target))throw new IOException("Contract ISO already exists.");
  bool created=false;
  using(FileStream input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read)) {
   long offset=Locate(input);input.Position=offset;byte[] overlay=Exact(input,OverlaySize),normalized=(byte[])overlay.Clone();
   byte[] host=Encoding.ASCII.GetBytes("ristia.gkh-game-zzz.jp\0"),scheme=Encoding.ASCII.GetBytes("https://\0");
   bool original=Sha(overlay)==Hash;
   if(!original) {
    byte[] expectedHost=new byte[host.Length];Buffer.BlockCopy(Encoding.ASCII.GetBytes("127.0.0.1"),0,expectedHost,0,9);
    byte[] expectedScheme=new byte[scheme.Length];Buffer.BlockCopy(Encoding.ASCII.GetBytes("http://"),0,expectedScheme,0,7);
    for(int i=0;i<host.Length;i++)if(overlay[HostOffset+i]!=expectedHost[i])throw new InvalidDataException("Not the original or existing CONNECT_TEST ISO.");
    for(int i=0;i<scheme.Length;i++)if(overlay[SchemeOffset+i]!=expectedScheme[i])throw new InvalidDataException("Unexpected scheme patch.");
    Buffer.BlockCopy(host,0,normalized,HostOffset,host.Length);Buffer.BlockCopy(scheme,0,normalized,SchemeOffset,scheme.Length);
    if(Sha(normalized)!=Hash)throw new InvalidDataException("Game version or existing patches differ. Original preserved.");
   } else { Edit(overlay,HostOffset,"ristia.gkh-game-zzz.jp","127.0.0.1");Edit(overlay,SchemeOffset,"https://","http://"); }
   if(originalHex.Length!=192 || replacementHex.Length!=192)throw new InvalidDataException("Invalid contract patch size.");
   for(int i=0;i<96;i++) { if(overlay[patchOffset+i]!=Convert.ToByte(originalHex.Substring(i*2,2),16))throw new InvalidDataException("Contract reader mismatch.");overlay[patchOffset+i]=Convert.ToByte(replacementHex.Substring(i*2,2),16); }
   try {
    using(FileStream output=new FileStream(target,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None)) {
     created=true;input.Position=0;input.CopyTo(output,8*1024*1024);output.Position=offset;output.Write(overlay,0,overlay.Length);output.Flush(true);
     output.Position=offset;if(output.Length!=input.Length || Sha(Exact(output,OverlaySize))!=Sha(overlay))throw new IOException("Contract ISO verification failed.");
    }
   } catch { if(created)File.Delete(target);throw; }
  }return target;
 }

 static string Line(Stream s) {
  List<byte> buf=new List<byte>();while(buf.Count<8192) { int ch=s.ReadByte();if(ch<0)throw new EndOfStreamException();if(ch==10)return Encoding.ASCII.GetString(buf.ToArray()).TrimEnd('\r');buf.Add((byte)ch); }
  throw new InvalidDataException("HTTP line too long.");
 }
 public static GkhRequest Request(Stream s) {
  GkhRequest r=new GkhRequest();StringBuilder raw=new StringBuilder();string first=Line(s);raw.Append(first).Append("\r\n");
  string[] fields=first.Split(' ');if(fields.Length<3)throw new InvalidDataException("Invalid HTTP request.");r.Method=fields[0];r.Path=fields[1];
  while(true) {
   string line=Line(s);raw.Append(line).Append("\r\n");if(raw.Length>65536)throw new InvalidDataException("Header too large.");if(line.Length==0)break;
   int colon=line.IndexOf(':');if(colon<1)throw new InvalidDataException("Invalid HTTP header.");r.Headers[line.Substring(0,colon)]=line.Substring(colon+1).Trim();
  }
  r.RawHeader=raw.ToString();string expect;
  if(r.Headers.TryGetValue("Expect",out expect) && String.Equals(expect,"100-continue",StringComparison.OrdinalIgnoreCase)) {
   byte[] interim=Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");s.Write(interim,0,interim.Length);s.Flush();
  }
  string encoding;
  if(r.Headers.TryGetValue("Transfer-Encoding",out encoding)) {
   if(!String.Equals(encoding,"chunked",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsupported transfer encoding.");
   using(MemoryStream body=new MemoryStream()) {
    while(true) {
     string sizeLine=Line(s).Split(';')[0].Trim();int n=Convert.ToInt32(sizeLine,16);
     if(n<0 || body.Length+n>4*1024*1024)throw new InvalidDataException("Request exceeds 4 MiB.");
     if(n==0) { int trailers=0;while(Line(s).Length!=0) { if(++trailers>100)throw new InvalidDataException("Too many trailers."); }break; }
     byte[] chunk=Exact(s,n);body.Write(chunk,0,n);if(Line(s).Length!=0)throw new InvalidDataException("Invalid chunk delimiter.");
    }
    r.Body=body.ToArray();
   }
  } else {
   string length;int n=r.Headers.TryGetValue("Content-Length",out length)?Int32.Parse(length):0;
   if(n<0 || n>4*1024*1024)throw new InvalidDataException("Request exceeds 4 MiB.");r.Body=Exact(s,n);
  }
  return r;
 }
 public static void Reply(Stream s) {
  byte[] body=Encoding.ASCII.GetBytes("GKH diagnostic capture only. Game server is not implemented.\n");
  byte[] header=Encoding.ASCII.GetBytes("HTTP/1.0 503 Service Unavailable\r\nContent-Type: text/plain\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n");
  s.Write(header,0,header.Length);s.Write(body,0,body.Length);s.Flush();
 }
}
'@

Add-Type -ReferencedAssemblies 'System.dll','System.Core.dll','System.Xml.dll' -TypeDefinition ((Get-Content -LiteralPath (Join-Path $TaskRoot 'GkhCampaign.cs') -Raw) + @'
public static class GkhWarExperiment {
 public static uint Crc(byte[] data,int start,int length) {
  uint value=0xffffffff;
  for(int i=start;i<start+length;i++) { value^=data[i];for(int j=0;j<8;j++)value=(value&1)!=0?(value>>1)^0xedb88320:(value>>1); }
  return value^0xffffffff;
 }
 static byte[] Chunk(string tag,byte[] data,ushort children) {
  int pad=(4-data.Length%4)%4;byte[] b=new byte[24+data.Length+pad];
  Buffer.BlockCopy(Encoding.ASCII.GetBytes(tag),0,b,0,4);
  Buffer.BlockCopy(BitConverter.GetBytes(data.Length),0,b,8,4);
  Buffer.BlockCopy(BitConverter.GetBytes(24),0,b,12,4);
  Buffer.BlockCopy(BitConverter.GetBytes(children),0,b,16,2);
  Buffer.BlockCopy(BitConverter.GetBytes(pad),0,b,20,4);
  Buffer.BlockCopy(data,0,b,24,data.Length);
  Buffer.BlockCopy(BitConverter.GetBytes(Crc(b,8,b.Length-8)),0,b,4,4);
  return b;
 }
 public static byte[] Account(byte[] request, string token) {
  int offset=0;byte[] account=null;bool create=false;
  while(offset<request.Length) {
   if(request.Length-offset<24)throw new InvalidDataException("Request chunk header incomplete.");
   string tag=Encoding.ASCII.GetString(request,offset,4);
   int length=BitConverter.ToInt32(request,offset+8),head=BitConverter.ToInt32(request,offset+12),pad=BitConverter.ToInt32(request,offset+20);
   int format=BitConverter.ToUInt16(request,offset+18);
   if(head!=24 || length<0 || pad<0 || pad>3)throw new InvalidDataException("Unsupported request chunk.");
   long total=(long)head+length+pad+format;
   if(total>request.Length-offset)throw new InvalidDataException("Request chunk truncated.");
   if(BitConverter.ToUInt32(request,offset+4)!=Crc(request,offset+8,(int)total-8))throw new InvalidDataException("Request CRC mismatch.");
   if(tag=="CRET")create=true;
   if(tag=="ACNT") { if(account!=null || length!=154)throw new InvalidDataException("Unexpected account chunk.");account=new byte[length];Buffer.BlockCopy(request,offset+head,account,0,length); }
   offset+=(int)total;
  }
  if(!create || account==null)throw new InvalidDataException("Not the captured create-account format.");
  if(BitConverter.ToUInt32(account,0)!=0)throw new InvalidDataException("Existing account: creation response disabled.");
  for(int i=4;i<132;i++)if(account[i]!=0)throw new InvalidDataException("Existing account token: response disabled.");
  byte[] value=new byte[148];Buffer.BlockCopy(account,0,value,0,140);
  Buffer.BlockCopy(BitConverter.GetBytes(1),0,value,0,4);
  byte[] text=Encoding.ASCII.GetBytes(token);if(text.Length>=128)throw new InvalidDataException("Token too long.");
  Buffer.BlockCopy(text,0,value,4,text.Length);
  // Remaining account fields and 936-byte common server data are placeholders.
  byte[] init=Chunk("INTL",new byte[936],0),acnt=Chunk("ACNT",value,0);
  byte[] payload=new byte[init.Length+acnt.Length];Buffer.BlockCopy(init,0,payload,0,init.Length);Buffer.BlockCopy(acnt,0,payload,init.Length,acnt.Length);
  return Chunk("GKHS",payload,2);
 }

 public sealed class Block { public string Tag, Format; public byte[] Payload; public ushort Count; }
 static List<Block> Parse(byte[] bytes,bool requireCrc) {
  List<Block> blocks=new List<Block>();int offset=0;
  while(offset<bytes.Length) {
   if(bytes.Length-offset<24)throw new InvalidDataException("Incomplete block header.");
   int length=BitConverter.ToInt32(bytes,offset+8),head=BitConverter.ToInt32(bytes,offset+12),pad=BitConverter.ToInt32(bytes,offset+20);
   int formatLength=BitConverter.ToUInt16(bytes,offset+18);
   long total=(long)head+length+pad+formatLength;
   if(head!=24 || length<0 || pad<0 || pad>3 || total>bytes.Length-offset)throw new InvalidDataException("Invalid block lengths.");
   uint checksum=BitConverter.ToUInt32(bytes,offset+4);
   if((requireCrc || checksum!=0) && checksum!=Crc(bytes,offset+8,(int)total-8))throw new InvalidDataException("Block CRC mismatch.");
   Block b=new Block();b.Tag=Encoding.ASCII.GetString(bytes,offset,4);b.Payload=new byte[length];Buffer.BlockCopy(bytes,offset+head,b.Payload,0,length);
   b.Count=BitConverter.ToUInt16(bytes,offset+16);b.Format=Encoding.ASCII.GetString(bytes,offset+head+length,formatLength).TrimEnd('\0');
   blocks.Add(b);offset+=(int)total;
  }
  return blocks;
 }
 static Block Get(List<Block> blocks,string tag) {
  Block found=null;foreach(Block b in blocks)if(b.Tag==tag) { if(found!=null)throw new InvalidDataException("Duplicate block "+tag);found=b; }
  if(found==null)throw new InvalidDataException("Missing block "+tag);return found;
 }
 static void TestAccount(List<Block> blocks) {
  Block b=Get(blocks,"ACNT");
  if(b.Payload.Length!=154 || BitConverter.ToUInt32(b.Payload,0)!=1 || !Encoding.ASCII.GetString(b.Payload,4,128).StartsWith("LOCAL_TEST_"))throw new InvalidDataException("Only the temporary local test account is supported.");
 }
 static byte[] Root(params byte[][] blocks) {
  using(MemoryStream data=new MemoryStream()) { foreach(byte[] b in blocks)data.Write(b,0,b.Length);return Chunk("GKHS",data.ToArray(),checked((ushort)blocks.Length)); }
 }
 public static byte[] Login(byte[] request) {
  List<Block> blocks=Parse(request,true);TestAccount(blocks);Block original=Get(blocks,"ACNT");byte[] account=new byte[148];Buffer.BlockCopy(original.Payload,0,account,0,140);
  return Root(Chunk("INTL",new byte[936],0),Chunk("ACNT",account,0));
 }
 static readonly Dictionary<string,int> UnitOffsets=new Dictionary<string,int> {
 // Generated from the uploaded version's serialization field pointers.
 {"CI",0},
{"CJ",4},
{"CK",8},
{"CL",9},
{"CM",10},
{"CN",11},
{"CO",12},
{"CP",28},
{"CQ",30},
{"CR",31},
{"CS",32},
{"CT",33},
{"CU",34},
{"CV",35},
{"CW",36},
{"CX",37},
{"CY",38},
{"CZ",39},
{"DA",40},
{"DB",41},
{"DC",44},
{"DD",48},
{"DE",52},
{"DF",56},
{"DG",58},
{"DH",59},
{"DI",60},
{"DJ",61},
{"DK",62},
{"DL",63},
{"DM",64},
{"DN",65},
{"DO",66},
{"DP",67},
{"DQ",68},
{"DR",69},
{"DS",70},
{"DT",71},
{"DU",72},
{"DV",73},
{"DW",76},
{"DX",78},
{"DY",80},
{"DZ",82},
{"EA",84},
{"EB",86},
{"EC",88},
{"ED",90},
{"EE",92},
{"EF",94},
{"EG",96},
{"EH",98},
{"EI",100},
{"EJ",101},
{"EK",102},
{"EL",103},
{"EM",104},
{"EN",106},
{"EO",108},
{"EP",110},
{"EQ",112},
{"ER",114},
{"ES",116},
{"ET",118},
{"EU",120},
{"EV",122},
{"EW",123}
 };
 static byte[] NativePrimary(Block b) {
  byte[] primary=new byte[124];int at=0;
  foreach(string item in b.Format.Split('/')) {
   System.Text.RegularExpressions.Match match=System.Text.RegularExpressions.Regex.Match(item,@"^([LlvsCc])(\d+)([A-Z]+)$");
   if(!match.Success)throw new InvalidDataException("Unknown unit field format.");
   char type=match.Groups[1].Value[0];int count=Int32.Parse(match.Groups[2].Value);string name=match.Groups[3].Value;
   int width=(type=='L'||type=='l')?4:(type=='v'||type=='s')?2:1;int size=checked(count*width),dest;
   if(!UnitOffsets.TryGetValue(name,out dest) || at+size>b.Payload.Length || dest+size>primary.Length)throw new InvalidDataException("Unit field layout mismatch.");
   Buffer.BlockCopy(b.Payload,at,primary,dest,size);at+=size;
  }
  if(at!=b.Payload.Length || at!=120)throw new InvalidDataException("Unexpected unit payload size.");return primary;
 }
 static void SaveTemplates(Block units,string directory) {
  List<Block> records=Parse(units.Payload,false);if(records.Count!=units.Count)throw new InvalidDataException("Unit child count mismatch.");
  List<byte[]> templates=new List<byte[]>();foreach(Block b in records) {
   byte[] primary=NativePrimary(b);
   if(BitConverter.ToUInt32(primary,0)!=0 && primary[12]!=0) { templates.Add(primary);if(templates.Count==4)break; }
  }
  if(templates.Count==0)throw new InvalidDataException("No named unit templates available.");
  using(MemoryStream data=new MemoryStream()) { for(int i=0;i<4;i++) { byte[] primary=templates[i%templates.Count];data.Write(primary,0,primary.Length); }File.WriteAllBytes(System.IO.Path.Combine(directory,"unit-template-cache.bin"),data.ToArray()); }
 }
 public static string LastWarNote="";
 sealed class WarState {
  public byte[] War;
  public HashSet<string> Keys=new HashSet<string>(StringComparer.Ordinal);
 }
 static string StatePath(byte[] war,string directory) {
  string folder=System.IO.Path.Combine(directory,"local-war-state");Directory.CreateDirectory(folder);
  return System.IO.Path.Combine(folder,"war_"+BitConverter.ToUInt32(war,0).ToString()+"_"+BitConverter.ToUInt32(war,4).ToString()+".bin");
 }
 static WarState LoadWar(byte[] incoming,string path) {
  WarState state=new WarState();state.War=(byte[])incoming.Clone();
  if(!File.Exists(path))return state;
  byte[] data=File.ReadAllBytes(path);
  if(data.Length<154 || data.Length>3200154 || Encoding.ASCII.GetString(data,0,4)!="GKW6" || BitConverter.ToUInt32(data,4)!=1)throw new InvalidDataException("Invalid local war state. Preserve the file for diagnosis.");
  int count=BitConverter.ToInt32(data,146);
  if(count<0 || count>100000 || data.Length!=154+count*32 || Crc(data,0,data.Length-4)!=BitConverter.ToUInt32(data,data.Length-4))throw new InvalidDataException("Local war state checksum/length mismatch. No reset was performed.");
  byte[] saved=new byte[138];Buffer.BlockCopy(data,8,saved,0,138);
  for(int i=0;i<8;i++)if(saved[i]!=incoming[i])throw new InvalidDataException("Local war ID mismatch.");
  for(int i=132;i<138;i++)if(saved[i]!=incoming[i])throw new InvalidDataException("Battlefield IDs/owners changed in the same war. No score update was performed.");
  // Only the two per-front strength arrays are authoritative in this experiment.
  Buffer.BlockCopy(saved,72,state.War,72,24);
  for(int i=0;i<count;i++) {
   string key=Convert.ToBase64String(data,150+i*32,32);
   if(!state.Keys.Add(key))throw new InvalidDataException("Duplicate keys in local war state.");
  }
  return state;
 }
 static void SaveWar(WarState state,string path) {
  if(state.Keys.Count>100000)throw new InvalidDataException("Local battle journal is full.");
  byte[] data=new byte[154+state.Keys.Count*32];Buffer.BlockCopy(Encoding.ASCII.GetBytes("GKW6"),0,data,0,4);
  Buffer.BlockCopy(BitConverter.GetBytes((uint)1),0,data,4,4);Buffer.BlockCopy(state.War,0,data,8,138);
  Buffer.BlockCopy(BitConverter.GetBytes(state.Keys.Count),0,data,146,4);int at=150;
  List<string> keys=new List<string>(state.Keys);keys.Sort(StringComparer.Ordinal);
  foreach(string key in keys) { byte[] hash=Convert.FromBase64String(key);Buffer.BlockCopy(hash,0,data,at,32);at+=32; }
  Buffer.BlockCopy(BitConverter.GetBytes(Crc(data,0,data.Length-4)),0,data,data.Length-4,4);
  string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
  try {
   using(FileStream file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { file.Write(data,0,data.Length);file.Flush(true); }
   if(File.Exists(path))File.Replace(temp,path,path+".previous");else File.Move(temp,path);
  } finally { if(File.Exists(temp))File.Delete(temp); }
 }
 static string BattleKey(List<Block> blocks) {
  // Exclude WARI scores and account timestamps: retrying after a sync must not
  // earn a second credit. Include client counters to distinguish repeat battles.
  using(MemoryStream data=new MemoryStream()) {
   Block account=Get(blocks,"ACNT"),player=Get(blocks,"PLYR"),battle=Get(blocks,"BTRT"),war=Get(blocks,"WARI"),nation=Get(blocks,"ENMN");
   data.Write(account.Payload,0,132);data.Write(player.Payload,0,88);data.Write(battle.Payload,0,24);data.Write(war.Payload,0,8);data.Write(nation.Payload,0,1);
   using(System.Security.Cryptography.SHA256 hash=System.Security.Cryptography.SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(data.ToArray()));
  }
 }
 static string Scores(byte[] war) {
  StringBuilder note=new StringBuilder();
  for(int i=0;i<3;i++) { if(i!=0)note.Append("; ");note.Append("front ").Append(war[132+i]).Append(" owner ").Append(war[135+i]).Append(" strength ").Append(BitConverter.ToUInt32(war,72+i*4)).Append(" opposing ").Append(BitConverter.ToUInt32(war,84+i*4)); }
  return note.ToString();
 }
 static byte[] PersistentWar(List<Block> blocks,string directory,bool credit) {
  Block incoming=Get(blocks,"WARI");if(incoming.Payload.Length!=138 || BitConverter.ToUInt32(incoming.Payload,0)==0)throw new InvalidDataException("Unexpected war layout or zero war ID.");
  byte[] seed=incoming.Payload;string[] importedKeys=null;
  // Import an existing v06/v07 checkpoint only when starting a new campaign.
  string path=StatePath(seed,directory);
  if(File.Exists(path) && !File.Exists(System.IO.Path.Combine(directory,"campaign_state.bin"))) { WarState legacy=LoadWar(seed,path);seed=legacy.War;importedKeys=new List<string>(legacy.Keys).ToArray(); }
  GkhCampaign.Start(seed,importedKeys);GkhCampaign.Pump();
  if(credit)GkhCampaign.PlayerResult(incoming.Payload,Get(blocks,"PLYR").Payload,Get(blocks,"BTRT").Payload,Get(blocks,"ENMN").Payload,BattleKey(blocks));
  LastWarNote=GkhCampaign.LastNote;return credit?GkhCampaign.WireWarForRoute(incoming.Payload):GkhCampaign.WireWar();
 }

 static byte[] TerritoryBlock(byte[] war,byte[] player) {
  byte[] requested=new byte[138];Buffer.BlockCopy(war,0,requested,0,138);byte[][] records=GkhCampaign.RegionsForRoute(requested,player[48],player[50]);
  byte[][] nodes=new byte[records.Length][];for(int i=0;i<nodes.Length;i++)nodes[i]=Chunk("AREA",records[i],0);
  using(MemoryStream data=new MemoryStream()) { foreach(byte[] node in nodes)data.Write(node,0,node.Length);return Chunk("FOLW",data.ToArray(),checked((ushort)nodes.Length)); }
 }
 public static byte[] Sync(byte[] request,string directory) {
  List<Block> blocks=Parse(request,true);TestAccount(blocks);Block war=Get(blocks,"WARI");
  if(war.Payload.Length!=138)throw new InvalidDataException("Unexpected WARI payload size.");
  SaveTemplates(Get(blocks,"UNIT"),directory);byte[] value=PersistentWar(blocks,directory,false);
  byte[] common=GkhCampaign.SyncContract(Get(blocks,"PLYR").Payload),effective=GkhCampaign.ContractPlayer(Get(blocks,"PLYR").Payload);
  // Empty optional blocks cause the observed readers to leave the existing client data alone.
  return Root(Chunk("INTL",common,0),Chunk("WARI",value,0),Chunk("WMAP",new byte[0],0),Chunk("HIST",new byte[0],0),TerritoryBlock(value,effective),Chunk("AUTO",new byte[0],0),Chunk("UNIT",new byte[0],0),Chunk("BTRT",new byte[]{0},0));
 }
 static byte[] MirrorParty(byte[] cache,int index,int nation) {
  // 0x728-byte transport record: 0x5c metadata, 0x80 counters, 10*12 history,
  // three sets of four 0x7c-byte primary character snapshots, then trailing flags.
  byte[] record=new byte[0x728];
  Buffer.BlockCopy(BitConverter.GetBytes(1000+index),0,record,0,4);
  record[0x30]=(byte)nation;record[0x32]=255;Buffer.BlockCopy(BitConverter.GetBytes(-1),0,record,0x54,4);
  byte[] name=Encoding.ASCII.GetBytes("TEST NPC "+(index+1).ToString("00"));Buffer.BlockCopy(name,0,record,0x34,name.Length);
  for(int set=0;set<3;set++)for(int unit=0;unit<4;unit++)Buffer.BlockCopy(cache,unit*124,record,0x154+(set*4+unit)*124,124);
  // 0x08969824..0x08969878 rejects an unspecified (-1) formation.
  // 0x724 selects character set 0..2; 0x725 selects formation 0..36. Formation 13 places four units at distinct valid cells.
  record[0x724]=0;record[0x725]=13;return record;
 }
 public static string NpcSelfTest() {
  byte[] cache=new byte[496];for(int unit=0;unit<4;unit++)cache[unit*124]=1;
  byte[] record=MirrorParty(cache,0,2);
  if(record.Length!=1832 || (sbyte)record[0x724]<0 || record[0x724]>=3 || (sbyte)record[0x725]<0 || record[0x725]>=37)throw new Exception("SELFTEST NPC set/formation would be rejected by native spawn reader.");
  int active=0;for(int unit=0;unit<4;unit++)if((record[0x154+(record[0x724]*4+unit)*124]&1)!=0)active++;
  if(active!=4 || record[0x30]!=2)throw new Exception("SELFTEST invalid mirror NPC characters/nation.");
  return "PASS: NPC native spawn set/formation ranges and four active template characters.";
 }
 public static byte[] Enemies(byte[] request,string directory) {
  List<Block> blocks=Parse(request,true);Block enemies=Get(blocks,"ENMY");Block player=Get(blocks,"PLYR");if(player.Payload.Length!=88 || player.Payload[48]>2)throw new InvalidDataException("Invalid enemy lookup player.");int number=enemies.Count;
  if(number<1 || number>20)throw new InvalidDataException("Unsupported enemy request count.");
  List<Block> queries=Parse(enemies.Payload,false);if(queries.Count!=number)throw new InvalidDataException("Enemy count mismatch.");
  string path=System.IO.Path.Combine(directory,"unit-template-cache.bin");if(!File.Exists(path))throw new InvalidDataException("Run SyncWar first to capture unit templates.");
  byte[] cache=File.ReadAllBytes(path);if(cache.Length!=496)throw new InvalidDataException("Invalid local unit template cache.");
  byte[][] root=new byte[number+1][];root[0]=Chunk("INTL",new byte[936],0);
  for(int i=0;i<number;i++) {
   if(queries[i].Payload.Length!=7 || queries[i].Payload[0]>2)throw new InvalidDataException("Unsupported enemy nation request.");
   bool populated=GkhCampaign.ContractCanFight(player.Payload[48]) && GkhEncounterPolicy.Populate(queries[i].Payload,i);
   // Decoder 0x089a9c60 clears the old army before reading the UNIT child count.
   // A zero-count UNIT list therefore clears this requested army cache slot.
   byte[] record=populated?Chunk("UNIT",MirrorParty(cache,i,GkhCampaign.HostileNation(player.Payload[48],queries[i].Payload[0])),0):new byte[0];
   byte[] units=Chunk("UNIT",record,(ushort)(populated?1:0));
   root[i+1]=Chunk("PRTY",units,1);
  }
  return Root(root);
 }

 public static byte[] BattleResult(byte[] request,string directory) {
  List<Block> blocks=Parse(request,true);TestAccount(blocks);
  Block battle=Get(blocks,"BTRT"),war=Get(blocks,"WARI"),nation=Get(blocks,"ENMN");
  if(blocks.Count!=5 || Get(blocks,"PLYR").Payload.Length!=88 || battle.Payload.Length!=24 || war.Payload.Length!=138 || nation.Payload.Length!=1 || nation.Payload[0]>2)throw new InvalidDataException("Unexpected battle result layout.");
  byte[] value=PersistentWar(blocks,directory,true);
  // Consumer 0x089b5338 reads ten children by index. Child 7 is mandatory
  // even when empty: 0x089aa0d8 dereferences its child-count unconditionally.
  // Unknown reward/list data stays absent; no guessed rewards are injected.
  return Root(Chunk("INTL",new byte[936],0),Chunk("WARI",value,0),
   Chunk("WMAP",new byte[0],0),Chunk("HIST",new byte[0],0),Chunk("FOLW",new byte[0],0),
   Chunk("LST1",new byte[0],0),Chunk("LST2",new byte[0],0),Chunk("PLYR",new byte[0],0),
   Chunk("BTRT",new byte[0],0),Chunk("STAT",new byte[]{0},0));
 }

 public static byte[] Council(byte[] request) {
  List<Block> blocks=Parse(request,true);TestAccount(blocks);
  if(blocks.Count!=3 || Get(blocks,"PLYR").Payload.Length!=88 || Get(blocks,"WARI").Payload.Length!=138)throw new InvalidDataException("Unexpected council request layout.");
  // Observed response callback 0x089b54f0 is a return-only function.
  // Provide a valid status-zero envelope. No vote, reward or war mutation.
  return Root();
 }

 public static void Send(Stream stream,byte[] body,int status) {
  string reason=status==200?"OK":"Service Unavailable";
  byte[] header=Encoding.ASCII.GetBytes("HTTP/1.0 "+status+" "+reason+"\r\nContent-Type: application/octet-stream\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n");
  stream.Write(header,0,header.Length);stream.Write(body,0,body.Length);stream.Flush();
 }
}
'@)

try {
 if ($Action -eq 'backup') {
  Add-Type -AssemblyName System.Windows.Forms
  $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
  $dialog.Description = 'Select PPSSPP PSP/SAVEDATA folder (contains ULJS00394... game save folders)'
  $dialog.SelectedPath = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PPSSPP\PSP\SAVEDATA'
  if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }
  $savePath = $dialog.SelectedPath
  $dialog.Dispose()
  $gameSaves = @(Get-ChildItem -LiteralPath $savePath -Directory | Where-Object { $_.Name -like 'ULJS00394*' })
  if ($gameSaves.Count -eq 0) { throw 'No ULJS00394 save found. Select the PSP/SAVEDATA folder used by PPSSPP.' }
  $backupPath = Join-Path $TaskRoot ('save_backup_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
  [void][IO.Directory]::CreateDirectory($backupPath)
  foreach ($save in $gameSaves) { Copy-Item -LiteralPath $save.FullName -Destination $backupPath -Recurse }
  foreach ($save in $gameSaves) {
   foreach ($file in @(Get-ChildItem -LiteralPath $save.FullName -Recurse -File)) {
    $relative = $file.FullName.Substring($savePath.Length).TrimStart('\')
    $copy = Join-Path $backupPath $relative
    if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash) { throw 'Save backup verification failed.' }
   }
  }
  @{source=$savePath;backup=$backupPath;verified=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $TaskRoot 'backup-info.json')
  Write-Host ('Save backup verified: ' + $backupPath)
 } elseif ($Action -eq 'contractpatch') {
  if (-not $Source) {
   Add-Type -AssemblyName System.Windows.Forms
   $dialog = New-Object System.Windows.Forms.OpenFileDialog
   $dialog.Title = 'Choose original or CONNECT_TEST ISO for contract patch'
   $dialog.Filter = 'Game ISO (*.iso)|*.iso'
   if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }
   $Source = $dialog.FileName
   $dialog.Dispose()
  }
  $patch = Get-Content -LiteralPath (Join-Path $TaskRoot 'contract_patch.json') -Raw | ConvertFrom-Json
  $outputPath = [GkhProbe]::PatchContract($Source,$patch.original,$patch.replacement,[int]$patch.offset)
  Write-Host ('Created and verified: ' + $outputPath)
 } elseif ($Action -eq 'patch') {
  if (-not $Source) {
   Add-Type -AssemblyName System.Windows.Forms
   $dialog = New-Object System.Windows.Forms.OpenFileDialog
   $dialog.Title = 'Choose original Grand Knights History ISO'
   $dialog.Filter = 'Game ISO (*.iso)|*.iso'
   if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }
   $Source = $dialog.FileName
   $dialog.Dispose()
  }
  $outputPath = [GkhProbe]::Patch($Source)
  @{tool='GKH connection probe v0.2';host='127.0.0.1';scheme='http://';original_preserved=$true;game_server_implemented=$false} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $TaskRoot 'patch-info.json')
  Write-Host "Created: $outputPath"
  Write-Host 'Original unchanged. Connection test only; login/play are not implemented.'
 } elseif ($Action -eq 'capture') {
  $backupInfoPath = Join-Path $TaskRoot 'backup-info.json'
  if (-not (Test-Path -LiteralPath $backupInfoPath)) { throw 'Run 1_BACKUP_SAVE.bat first to create a verified backup of your game save.' }
  $backupInfo = Get-Content -LiteralPath $backupInfoPath -Raw | ConvertFrom-Json
  if (-not $backupInfo.verified -or -not (Test-Path -LiteralPath $backupInfo.backup)) { throw 'Verified save backup missing.' }
  $tokenPath = Join-Path $TaskRoot 'local-test-token.txt'
  if (-not (Test-Path -LiteralPath $tokenPath)) { ('LOCAL_TEST_' + [Guid]::NewGuid().ToString('N')) | Set-Content -Encoding ASCII -LiteralPath $tokenPath }
  $localToken = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
  $logs = Join-Path $TaskRoot 'logs'
  [void][System.IO.Directory]::CreateDirectory($logs)
  $settingsPath = Join-Path $TaskRoot 'campaign_config.json'
  $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
  $selfTest = [GkhCampaign]::SelfTest() + ' ' + [GkhEncounterPolicy]::SelfTest() + ' ' + [GkhWarExperiment]::NpcSelfTest()
  Write-Host ('Campaign self-test: ' + $selfTest)
  $selfTest | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $TaskRoot 'campaign_selftest.txt')
  [GkhCampaign]::Configure($TaskRoot, [int]$settings.war_seconds, [int]$settings.ai_tick_seconds, [int]$settings.vote_window_seconds, [int]$settings.ai_voters_per_nation)
  [GkhEncounterPolicy]::Configure([int]$settings.encounter_battle_slots, [int]$settings.encounter_empty_slots)
  $listener = New-Object System.Net.Sockets.TcpListener -ArgumentList ([System.Net.IPAddress]::Loopback),80
  $listener.Start()
  Write-Host 'Listening at http://127.0.0.1:80'
  Write-Host 'Keep this window open. Open the network menu in your CONTRACT_v12.iso.'
  Write-Host 'GKH LOCAL WAR 1.0.0: use CONTRACT_v12.iso; choose nation and duration with 6_CONTRACT_MENU.bat.'
  Write-Host 'New rounds reach the game on SyncWar. BattleResult preserves the marching round. Keep this server window open.'
  try {
   while ($true) {
    $campaignEvent = [GkhCampaign]::Pump()
    $contractEvent = [GkhCampaign]::PollContractRequest()
    if ($contractEvent) { Write-Host $contractEvent }
    if ($campaignEvent) {
     Write-Host $campaignEvent
     @{utc=[DateTime]::UtcNow.ToString('o');event=$campaignEvent} | ConvertTo-Json -Compress | Add-Content -Encoding UTF8 -LiteralPath (Join-Path $TaskRoot 'campaign_events.jsonl')
    }
    if (-not $listener.Pending()) { Start-Sleep -Milliseconds 250; continue }
    $client = $listener.AcceptTcpClient()
    $client.ReceiveTimeout = 15000
    $client.SendTimeout = 15000
    try {
     $stream = $client.GetStream()
     $request = [GkhProbe]::Request($stream)
     $name = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '_' + [Guid]::NewGuid().ToString('N').Substring(0,8) + '.json'
     $status = 503
     $replyBody = [Text.Encoding]::ASCII.GetBytes('Endpoint not implemented. Request captured.')
     $replyNote = 'Endpoint not implemented.'
     if ($request.Method -eq 'POST' -and $request.Path -eq '/gkh/server/psp/GkCreateAccount.php') {
      try {
       $replyBody = [GkhWarExperiment]::Account($request.Body, $localToken)
       $status = 200
       $replyNote = 'Experimental local account reply; server data fields are placeholders. Gameplay not restored.'
      } catch { $replyNote = $_.Exception.Message }
     } elseif ($request.Method -eq 'POST' -and $request.Path -eq '/gkh/server/psp/GkLogin.php') {
      try { $replyBody = [GkhWarExperiment]::Login($request.Body); $status = 200; $replyNote = 'Experimental local account login response.' } catch { $replyNote = $_.Exception.Message }
     } elseif ($request.Method -eq 'POST' -and $request.Path -eq '/gkh/server/psp/GkSyncWar.php') {
      try { $replyBody = [GkhWarExperiment]::Sync($request.Body, $TaskRoot); $status = 200; $replyNote = [GkhWarExperiment]::LastWarNote } catch { $replyNote = $_.Exception.Message }
     } elseif ($request.Method -eq 'POST' -and $request.Path -eq '/gkh/server/psp/GkBattleResult.php') {
      try { $replyBody = [GkhWarExperiment]::BattleResult($request.Body, $TaskRoot); $status = 200; $replyNote = [GkhWarExperiment]::LastWarNote } catch { $replyNote = $_.Exception.Message }
     } elseif ($request.Method -eq 'POST' -and $request.Path -eq '/gkh/server/psp/GkCouncil.php') {
      try { $replyBody = [GkhWarExperiment]::Council($request.Body); $status = 200; $replyNote = 'Council compatibility acknowledgment only. Council gameplay and voting are not implemented; local war state unchanged.' } catch { $replyNote = $_.Exception.Message }
     } elseif ($request.Method -eq 'POST' -and $request.Path -eq '/gkh/server/psp/GkEnemyArmy.php') {
      try { $replyBody = [GkhWarExperiment]::Enemies($request.Body, $TaskRoot); $status = 200; $replyNote = 'Sparse hostile mirror-NPC response: configured battle/empty army slots. Empty slots clear cached enemies; map recovery behavior requires gameplay verification.' } catch { $replyNote = $_.Exception.Message }
     }
     $record = @{utc=[DateTime]::UtcNow.ToString('o');method=$request.Method;path=$request.Path;raw_header=$request.RawHeader;body_base64=[Convert]::ToBase64String($request.Body);body_size=$request.Body.Length;response_status=$status;response_base64=[Convert]::ToBase64String($replyBody);response_size=$replyBody.Length;note=$replyNote}
     $record | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $logs $name)
     [GkhWarExperiment]::Send($stream, $replyBody, $status)
     if ($status -eq 200 -and ($request.Path -eq '/gkh/server/psp/GkBattleResult.php' -or $request.Path -eq '/gkh/server/psp/GkSyncWar.php')) { Write-Host $replyNote }
     Write-Host ('Captured: ' + $request.Method + ' ' + $request.Path + ' bytes: ' + $request.Body.Length)
    } catch {
     Write-Host ('Request error: ' + $_.Exception.Message)
     $errorLog = Join-Path $logs ('error_' + [Guid]::NewGuid().ToString('N') + '.json')
     @{utc=[DateTime]::UtcNow.ToString('o');error=$_.Exception.Message} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath $errorLog
    } finally { $client.Close() }
   }
  } finally { $listener.Stop() }
 } else {
  $logs = Join-Path $TaskRoot 'logs'
  $files = @(Get-ChildItem -LiteralPath $logs -Filter '*.json' -File -ErrorAction SilentlyContinue)
  if ($files.Count -eq 0) { throw 'No request captured. Report NO LOG and send the game error screenshot.' }
  $zipPath = Join-Path $TaskRoot ('GKH_REQUESTS_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.zip')
  $items = @($files | ForEach-Object { $_.FullName })
  $stateFolder = Join-Path $TaskRoot 'local-war-state'
  if (Test-Path -LiteralPath $stateFolder) { $items += @(Get-ChildItem -LiteralPath $stateFolder -Filter '*.bin' -File | ForEach-Object { $_.FullName }) }
  foreach ($extra in @('campaign_state.bin','campaign_status.txt','campaign_events.jsonl','campaign_selftest.txt','campaign_config.json','territory_map.json','contract_status.json','contract_patch.json','node_side_map.json')) {
   $extraPath = Join-Path $TaskRoot $extra
   if (Test-Path -LiteralPath $extraPath) { $items += $extraPath }
  }
  $info = Join-Path $TaskRoot 'patch-info.json'
  if (Test-Path -LiteralPath $info) { $items += $info }
  Compress-Archive -LiteralPath $items -DestinationPath $zipPath
  Write-Host "Upload this: $zipPath"
 }
} catch {
 Write-Host ('ERROR: ' + $_.Exception.Message)
 exit 1
}
