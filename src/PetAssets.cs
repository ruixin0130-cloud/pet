using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Tamago {
    public sealed class FrameDefinition { public string image; public double[] rect; public double time; }
    public sealed class ClipDefinition {
        public FrameDefinition[] frames;
        public bool loop;
        public string facing;
        public bool mirror;
        public double? touchedFrame;
    }
    public sealed class ManifestDefinition {
        public double version;
        public Dictionary<string,string> images;
        public Dictionary<string,ClipDefinition> actions,interactions;
    }
    public sealed class PetClip {
        readonly BitmapSource[] frames;
        readonly double[] ends;
        readonly bool loop,mirror,left;
        readonly int? touchedFrame;
        public int Count { get { return frames.Length; } }
        public double Length { get { return ends[ends.Length-1]; } }
        internal PetClip(ClipDefinition definition,Dictionary<string,BitmapSource> images,bool interaction) {
            if(definition==null||definition.frames==null||definition.frames.Length<1||definition.frames.Length>128)
                throw new InvalidDataException("动作帧数无效");
            if(definition.facing!="left"&&definition.facing!="right")throw new InvalidDataException("动作朝向无效");
            if(interaction&&definition.loop)throw new InvalidDataException("互动必须单次播放");
            frames=new BitmapSource[definition.frames.Length];ends=new double[frames.Length];
            loop=definition.loop;mirror=definition.mirror;left=definition.facing=="left";
            if(definition.touchedFrame.HasValue) {
                double index=definition.touchedFrame.Value;
                if(Double.IsNaN(index)||index<0||index>=frames.Length||index!=Math.Floor(index))throw new InvalidDataException("触摸帧无效");
                touchedFrame=(int)index;
            }
            double total=0;
            for(int i=0;i<frames.Length;i++) {
                FrameDefinition f=definition.frames[i];BitmapSource image;
                if(f==null||f.image==null||!images.TryGetValue(f.image,out image)||f.rect==null||f.rect.Length!=4)
                    throw new InvalidDataException("帧引用无效");
                if(Double.IsNaN(f.time)||Double.IsInfinity(f.time)||f.time<=0||f.time>60)throw new InvalidDataException("播放时序无效");
                int[] r=new int[4];
                for(int n=0;n<4;n++) {
                    double value=f.rect[n];
                    if(Double.IsNaN(value)||value<0||value>Int32.MaxValue||value!=Math.Floor(value))throw new InvalidDataException("裁切坐标必须为非负整数");
                    r[n]=(int)value;
                }
                if(r[0]<0||r[1]<0||r[2]<=0||r[3]<=0||(long)r[0]+r[2]>image.PixelWidth||(long)r[1]+r[3]>image.PixelHeight)
                    throw new InvalidDataException("裁切矩形越界");
                CroppedBitmap crop=new CroppedBitmap(image,new Int32Rect(r[0],r[1],r[2],r[3]));crop.Freeze();frames[i]=crop;
                total+=f.time;ends[i]=total;
            }
        }
        public int Index(double elapsed,double duration) {
            if(Double.IsNaN(elapsed)||Double.IsInfinity(elapsed)||elapsed<0)elapsed=0;
            double t=duration>0?Math.Min(1,elapsed/duration)*Length:(loop?elapsed%Length:elapsed);
            for(int i=0;i<ends.Length-1;i++)if(t<ends[i])return i;
            return ends.Length-1;
        }
        public BitmapSource At(double elapsed,double duration) { return frames[Index(elapsed,duration)]; }
        public BitmapSource Touched(double elapsed) { return touchedFrame.HasValue?frames[touchedFrame.Value]:At(elapsed,0); }
        public double ScaleX(bool facingLeft) { return mirror&&left!=facingLeft?-1:1; }
    }
    /// <summary>Validated, frozen images and clips. No filesystem access occurs during frame selection.</summary>
    public sealed class PetAssets {
        public PetProfile Profile { get; private set; }
        public string Error { get; private set; }
        public string Id { get; private set; }
        readonly Dictionary<PetAction,PetClip> actions=new Dictionary<PetAction,PetClip>();
        readonly Dictionary<PetInteraction,PetClip> interactions=new Dictionary<PetInteraction,PetClip>();
        public PetClip Action(PetAction action) { return actions[action]; }
        public PetClip Interaction(PetInteraction kind) { return interactions[kind]; }
        static string ReadJson(string path) {
            if(new FileInfo(path).Length>1024*1024)throw new InvalidDataException("配置文件过大");
            return File.ReadAllText(path);
        }
        // Reject traversal, ADS, rooted paths and junction/symlink escapes, including parent components.
        public static string LocalPath(string root,string relative) {
            if(String.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.Contains(":"))throw new InvalidDataException("素材路径无效");
            string fullRoot=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            string current=fullRoot;
            foreach(string part in relative.Replace('\\','/').Split('/')) {
                if(part==".."||part=="."||part.Length==0)throw new InvalidDataException("素材路径越界");
                current=Path.Combine(current,part);
            }
            string full=Path.GetFullPath(current);
            if(!full.StartsWith(fullRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("素材路径越界");
            for(string p=full;p!=null;p=Path.GetDirectoryName(p)) {
                if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("素材路径包含链接");
            }
            return full;
        }
        static string EmbeddedText(string name) {
            using(Stream s=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))using(StreamReader reader=new StreamReader(s))return reader.ReadToEnd();
        }
        static long CheckPngHeader(Stream stream) {
            byte[] header=new byte[24];int read=0;
            while(read<header.Length) { int n=stream.Read(header,read,header.Length-read);if(n==0)throw new InvalidDataException("PNG 文件不完整");read+=n; }
            byte[] signature={137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82};
            for(int i=0;i<signature.Length;i++)if(header[i]!=signature[i])throw new InvalidDataException("PNG 格式无效");
            long w=0,h=0;for(int i=0;i<4;i++){w=w*256+header[16+i];h=h*256+header[20+i];}
            if(w<1||h<1||w>8192||h>8192)throw new InvalidDataException("图片尺寸过大或无效");
            stream.Position=0;return w*h;
        }
        static PetAssets Parse(string manifest,string profile,Func<string,Stream> open,string id) {
            ManifestDefinition m=new JavaScriptSerializer().Deserialize<ManifestDefinition>(manifest);
            if(m==null||m.version!=1)throw new InvalidDataException("不支持的素材包版本");
            if(m.images==null||m.images.Count<1||m.images.Count>16||m.actions==null||m.actions.Count!=8||m.interactions==null||m.interactions.Count!=5)
                throw new InvalidDataException("素材包动作不完整");
            Dictionary<string,BitmapSource> images=new Dictionary<string,BitmapSource>();long pixels=0;
            foreach(var item in m.images) {
                if(String.IsNullOrWhiteSpace(item.Value)||!item.Value.EndsWith(".png",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("仅支持 PNG 素材");
                using(Stream stream=open(item.Value)) {
                    if(stream==null||stream.Length>32*1024*1024)throw new InvalidDataException("图片缺失或过大");
                    // Bound dimensions before WPF allocates a decoded bitmap.
                    pixels+=CheckPngHeader(stream);
                    if(pixels>32000000)throw new InvalidDataException("图片总像素过大");
                    PngBitmapDecoder decoder=new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                    BitmapSource image=decoder.Frames[0];
                    image.Freeze();images.Add(item.Key,image);
                }
            }
            PetAssets result=new PetAssets {Profile=PetProfile.FromJson(profile,m.actions.Keys),Id=id};
            foreach(PetAction a in Enum.GetValues(typeof(PetAction))) {
                ClipDefinition clip;if(!m.actions.TryGetValue(a.ToString(),out clip))throw new InvalidDataException("基础动作缺失");
                result.actions.Add(a,new PetClip(clip,images,false));
            }
            foreach(PetInteraction i in Enum.GetValues(typeof(PetInteraction)))if(i!=PetInteraction.None) {
                ClipDefinition clip;if(!m.interactions.TryGetValue(i.ToString(),out clip))throw new InvalidDataException("互动动作缺失");
                result.interactions.Add(i,new PetClip(clip,images,true));
            }
            return result;
        }
        public static PetAssets BuiltIn() {
            return Parse(EmbeddedText("default-manifest.json"),EmbeddedText("default-profile.json"),
                delegate(string name){return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);},"builtin");
        }
        public static PetAssets Load(string content) {
            try {
                string selection=Path.Combine(content,"active-pet.json");
                if(!File.Exists(selection)) {
                    PetAssets legacy=BuiltIn();string error;
                    string file=Path.Combine(content,"tamago-profile.json");
                    if(File.Exists(file)){legacy.Profile=PetProfile.Load(file,out error);legacy.Error=error;}
                    return legacy;
                }
                var selected=new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(ReadJson(selection));
                string id;
                if(selected==null||!selected.TryGetValue("pet",out id)||String.IsNullOrWhiteSpace(id)||
                    !System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[a-zA-Z0-9_-]{1,64}\z"))throw new InvalidDataException("角色选择无效");
                string root=LocalPath(content,"pets/"+id);
                return Parse(ReadJson(LocalPath(root,"manifest.json")),ReadJson(LocalPath(root,"profile.json")),
                    delegate(string name){return File.OpenRead(LocalPath(root,name));},id);
            } catch(Exception ex) {
                PetAssets fallback=BuiltIn();
                string reason=ex is InvalidDataException?ex.Message:ex is FileNotFoundException||ex is DirectoryNotFoundException?"文件缺失":"配置或图片无法读取";
                fallback.Error="角色包未加载："+reason+"，已恢复内置玉子。";return fallback;
            }
        }
    }
}
