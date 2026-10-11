using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using MapleBench.Services.Nx;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

// A composed scene is world-anchored as an object. Unlike a parallax background,
// its painted road follows exactly the same camera translation as its footholds.
if (args.Length != 6)
    throw new ArgumentException("GeneratedTown <scene.png> <new-output-dir> <map-id> <ground-row> <client-wz-dir> <server-root>");
string source = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
int id = int.Parse(args[2], CultureInfo.InvariantCulture);
int groundRow = int.Parse(args[3], CultureInfo.InvariantCulture);
string client = Path.GetFullPath(args[4]), server = Path.GetFullPath(args[5]);
if (id is < 100000000 or > 999999998) throw new ArgumentException("Map id must have nine digits.");
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output already exists; refusing overwrite.");
if (!Directory.Exists(server)) throw new DirectoryNotFoundException(server);
string mapName = id + ".img", artSet = "saoGeneratedTown" + id;
string mapNx = Path.Combine(client, "Map.nx");
string baseHash = Hash(mapNx), inputHash = Hash(source);
using (var nx = NxArchive.Open(mapNx))
{
    foreach (var folder in nx.Root["Map"])
        if (folder[mapName].IsValid) throw new IOException("Map id already exists in client: " + id);
    if (nx.Root["Obj"][artSet + ".img"].IsValid) throw new IOException("Art set already exists.");
}
foreach (string path in Directory.EnumerateFiles(server, mapName + "*", SearchOption.AllDirectories))
    throw new IOException("Map id already exists on server: " + path);
const string bgm = "Bgm00/FloralLife";
using (var sound = NxArchive.Open(Path.Combine(client, "Sound.nx")))
    if (!sound.Root.Resolve("Bgm00.img/FloralLife").IsValid) throw new IOException("BGM does not exist.");

using var pixels = new Bitmap(source);
int width = pixels.Width, height = pixels.Height;
if (groundRow <= 0 || groundRow >= height || width > short.MaxValue || height > short.MaxValue)
    throw new ArgumentException("Invalid ground row or scene dimensions.");
using var map = new WzImage(mapName);
var info = Sub("info");
foreach (var pair in new[] { ("version", 10), ("cloud", 0), ("town", 1), ("returnMap", id),
    ("forcedReturn", 999999999), ("fieldLimit", 0), ("swim", 0), ("fly", 0),
    ("noMapCmd", 0), ("hideMinimap", 0), ("VRLeft", 0), ("VRRight", width),
    ("VRTop", -groundRow), ("VRBottom", height - groundRow) }) Int(info, pair.Item1, pair.Item2);
Text(info, "bgm", bgm); Text(info, "mapMark", "None");
info.AddProperty(new WzFloatProperty("mobRate", 0));
map.AddProperty(info);
foreach (string name in new[] { "back", "life", "ladderRope", "reactor", "seat" }) map.AddProperty(Sub(name));
for (int n = 0; n < 8; n++)
{
    var layer = Sub(n.ToString());
    layer.AddProperty(Sub("info")); layer.AddProperty(Sub("tile")); layer.AddProperty(Sub("obj"));
    map.AddProperty(layer);
}
var scene = Sub("0");
foreach (var pair in new[] { ("oS", artSet), ("l0", "scene"), ("l1", "town"), ("l2", "0") }) Text(scene, pair.Item1, pair.Item2);
foreach (string name in new[] { "x", "y", "z", "zM", "f", "r", "flow" }) Int(scene, name, 0);
((WzSubProperty)((WzSubProperty)map["0"])["obj"]).AddProperty(scene);

var footholds = Sub("foothold"); var fhLayer = Sub("0"); var group = Sub("0");
int count = (width + 89) / 90;
for (int n = 1; n <= count; n++)
{
    var fh = Sub(n.ToString());
    foreach (var pair in new[] { ("x1", (n - 1) * 90), ("y1", 0), ("x2", Math.Min(n * 90, width)),
        ("y2", 0), ("prev", n == 1 ? 0 : n - 1), ("next", n == count ? 0 : n + 1) }) Int(fh, pair.Item1, pair.Item2);
    group.AddProperty(fh);
}
fhLayer.AddProperty(group); footholds.AddProperty(fhLayer); map.AddProperty(footholds);
var portals = Sub("portal"); var spawn = Sub("0");
Text(spawn, "pn", "sp"); Text(spawn, "tn", "");
Int(spawn, "pt", 0); Int(spawn, "tm", 999999999); Int(spawn, "x", width / 2); Int(spawn, "y", -10);
portals.AddProperty(spawn); map.AddProperty(portals);

using var art = new WzImage(artSet + ".img");
var root = Sub("scene"); var town = Sub("town"); var animation = Sub("0");
var frame = Canvas("0", pixels, 0, groundRow); frame.AddProperty(new WzIntProperty("delay", 100));
animation.AddProperty(frame); town.AddProperty(animation); root.AddProperty(town); art.AddProperty(root);
using var small = new Bitmap(pixels, Math.Max(1, (width + 15) / 16), Math.Max(1, (height + 15) / 16));
var minimap = Sub("miniMap");
foreach (var pair in new[] { ("width", width), ("height", height), ("centerX", 0), ("centerY", groundRow), ("mag", 4) }) Int(minimap, pair.Item1, pair.Item2);
minimap.AddProperty(Canvas("canvas", small, 0, 0)); map.AddProperty(minimap);

string mapDir = Path.Combine(output, "img", "Map", "Map" + args[2][0]);
string artDir = Path.Combine(output, "img", "Obj"), xmlDir = Path.Combine(output, "server-xml");
Directory.CreateDirectory(mapDir); Directory.CreateDirectory(artDir); Directory.CreateDirectory(xmlDir);
string mapPath = Path.Combine(mapDir, mapName), artPath = Path.Combine(artDir, art.Name);
File.Copy(source, Path.Combine(output, "town-preview.png"));
File.WriteAllBytes(mapPath, NxWzExport.SerializeImageAsWz(map));
File.WriteAllBytes(artPath, NxWzExport.SerializeImageAsWz(art));
new XDocument(new XElement("imgdir", new XAttribute("name", map.Name), map.WzProperties.Select(Xml)))
    .Save(Path.Combine(xmlDir, mapName + ".xml"));
// Only a merge fragment: writing a fresh String/Map.img would erase other names.
new XDocument(new XElement("imgdir", new XAttribute("name", "Map.img"),
    new XElement("imgdir", new XAttribute("name", "etc"),
        new XElement("imgdir", new XAttribute("name", id),
            new XElement("string", new XAttribute("name", "mapName"), new XAttribute("value", "起始之城・中央廣場")),
            new XElement("string", new XAttribute("name", "streetName"), new XAttribute("value", "艾恩葛朗特第一層"))))))
    .Save(Path.Combine(output, "string-map-merge-fragment.xml"));

using var readMap = Reopen(mapPath); using var readArt = Reopen(artPath);
if (!XNode.DeepEquals(XmlImage(map), XmlImage(readMap)) || !XNode.DeepEquals(XmlImage(art), XmlImage(readArt)))
    throw new InvalidDataException("IMG semantic roundtrip mismatch.");
var savedFrame = (WzCanvasProperty)((WzSubProperty)((WzSubProperty)((WzSubProperty)readArt["scene"])["town"])["0"])["0"];
CheckPixels(pixels, savedFrame);
CheckPixels(small, (WzCanvasProperty)((WzSubProperty)readMap["miniMap"])["canvas"]);
var savedChain = (WzSubProperty)((WzSubProperty)((WzSubProperty)readMap["foothold"])["0"])["0"];
for (int n = 1; n <= count; n++)
{
    var fh = (WzSubProperty)savedChain[n.ToString()];
    if (Number(fh, "prev") != (n == 1 ? 0 : n - 1) || Number(fh, "next") != (n == count ? 0 : n + 1)
        || Number(fh, "x1") != (n - 1) * 90 || Number(fh, "x2") != Math.Min(n * 90, width)
        || Number(fh, "y1") != 0 || Number(fh, "y2") != 0)
        throw new InvalidDataException("Broken or misaligned foothold: " + n);
}
if (Hash(mapNx) != baseHash || Hash(source) != inputHash) throw new IOException("Input changed during generation.");
WriteJson("validation.json", new { imgRoundtrip = "passed", fullScenePixelComparison = "passed", minimapPixelComparison = "passed",
    footholds = count, brokenChains = 0, continuousGround = new[] { 0, width }, spawnSupported = true,
    customObjectReference = "resolved against candidate Obj IMG", clientAndSourceUnchanged = true,
    bgmReference = "exists in client Sound.nx", installed = false, gameTested = false });
WriteJson("manifest.json", new { id, name = "起始之城・中央廣場", width, height, groundRow,
    generation = "from scratch; no previous map template or existing Maple artwork", artMode = "single world-anchored scene object",
    profile = "BMS", sceneSha256 = inputHash, baseMapNxSha256 = baseHash, mapSha256 = Hash(mapPath), artSha256 = Hash(artPath),
    mapTarget = "Map/Map" + args[2][0] + "/" + mapName, artTarget = "Obj/" + art.Name,
    artOrigin = new[] { 0, groundRow }, spawn = new[] { width / 2, -10 }, installed = false, gameTested = false,
    next = "Merge both candidate IMG subtrees into a COPY of Map.nx, merge names, then test isolated client/server movement." });
Console.WriteLine($"PASS: new map {id}, {width}x{height}, {count} connected footholds, exact scene/minimap pixels, IMG reparse. Not installed or game-tested.");

void WriteJson(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
static WzSubProperty Sub(string name) => new(name);
static void Int(WzSubProperty parent, string name, int value) => parent.AddProperty(new WzIntProperty(name, value));
static void Text(WzSubProperty parent, string name, string value) => parent.AddProperty(new WzStringProperty(name, value));
static int Number(WzSubProperty parent, string name) => ((WzIntProperty)parent[name]).Value;
static WzCanvasProperty Canvas(string name, Bitmap bitmap, int ox, int oy)
{
    var c = new WzCanvasProperty(name) { PngProperty = new WzPngProperty() };
    // MapleLib's automatic texture choice may quantize this painted scene.
    // Format2 stores BGRA losslessly and is supported by the v83 asset pipeline.
    var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    byte[] raw = new byte[checked(bitmap.Width * bitmap.Height * 4)];
    try
    {
        for (int row = 0; row < bitmap.Height; row++)
            Marshal.Copy(data.Scan0 + row * data.Stride, raw, row * bitmap.Width * 4, bitmap.Width * 4);
    }
    finally { bitmap.UnlockBits(data); }
    using var buffer = new MemoryStream();
    using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
    c.PngProperty.SetCompressedBytes(buffer.ToArray(), bitmap.Width, bitmap.Height, WzPngFormat.Format2);
    c.AddProperty(new WzVectorProperty("origin", ox, oy)); return c;
}
static WzImage Reopen(string path)
{
    var image = new WzImage(Path.GetFileName(path), File.OpenRead(path), WzMapleVersion.BMS);
    if (!image.ParseImage()) { image.Dispose(); throw new InvalidDataException("IMG failed to parse: " + path); }
    return image;
}
static XElement XmlImage(WzImage image) => new("imgdir", new XAttribute("name", image.Name), image.WzProperties.Select(Xml));
static XElement Xml(WzImageProperty p) => p switch
{
    WzSubProperty sub => new XElement("imgdir", new XAttribute("name", p.Name), sub.WzProperties.Select(Xml)),
    WzIntProperty n => new XElement("int", new XAttribute("name", p.Name), new XAttribute("value", n.Value)),
    WzFloatProperty n => new XElement("float", new XAttribute("name", p.Name), new XAttribute("value", n.Value.ToString("R", CultureInfo.InvariantCulture))),
    WzStringProperty s => new XElement("string", new XAttribute("name", p.Name), new XAttribute("value", s.Value)),
    WzVectorProperty v => new XElement("vector", new XAttribute("name", p.Name), new XAttribute("x", v.X.Value), new XAttribute("y", v.Y.Value)),
    WzCanvasProperty c => new XElement("canvas", new XAttribute("name", p.Name), c.WzProperties.Select(Xml)),
    _ => throw new InvalidDataException("Unsupported property " + p.PropertyType)
};
static void CheckPixels(Bitmap expected, WzCanvasProperty canvas)
{
    using var actual = canvas.PngProperty.GetImage(false);
    if (actual.Width != expected.Width || actual.Height != expected.Height) throw new InvalidDataException("Canvas size changed.");
    for (int y = 0; y < actual.Height; y++) for (int x = 0; x < actual.Width; x++)
        if (actual.GetPixel(x, y).ToArgb() != expected.GetPixel(x, y).ToArgb()) throw new InvalidDataException($"Pixel mismatch at {x},{y}");
}
