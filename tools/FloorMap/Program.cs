using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using System.Security.Cryptography;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using MapleBench.Services.Nx;

if (args.Length == 3 && args[0] == "inventory")
{
    if (Directory.Exists(args[2])) throw new IOException("Refusing existing inventory output.");
    Directory.CreateDirectory(args[2]);
    foreach (var version in new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS })
    {
        using var asset = new WzImage(Path.GetFileName(args[1]), File.OpenRead(args[1]), version);
        try { if (!asset.ParseImage()) continue; } catch { continue; }
        var rows = new List<object>(); int index = 0;
        void Walk(IEnumerable<WzImageProperty> properties, string prefix)
        {
            foreach (var p in properties)
            {
                string path = prefix + "/" + p.Name;
                if (p is WzCanvasProperty canvas)
                {
                    using var bitmap = canvas.PngProperty.GetImage(false);
                    string filename = (++index).ToString("D4") + ".png";
                    bitmap.Save(Path.Combine(args[2], filename), System.Drawing.Imaging.ImageFormat.Png);
                    var origin = canvas["origin"] as WzVectorProperty;
                    rows.Add(new { path, file = filename, width = bitmap.Width, height = bitmap.Height, originX = origin?.X.Value ?? 0, originY = origin?.Y.Value ?? 0 });
                }
                else if (p is WzSubProperty sub) Walk(sub.WzProperties, path);
            }
        }
        Walk(asset.WzProperties, "");
        File.WriteAllText(Path.Combine(args[2], "index.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Decoded " + index + " canvases with " + version); return;
    }
    throw new Exception("No readable asset profile.");
}
if (args.Length is not (3 or 4)) throw new ArgumentException("Usage: FloorMap <source.img> <new-output-dir> <unused-nine-digit-id> [town]");
string source = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
if (!int.TryParse(args[2], out int id) || id < 100000000 || id > 999999998) throw new ArgumentException("Invalid map id.");
if (Directory.Exists(output)) throw new IOException("Refusing existing output directory.");
byte[] original = File.ReadAllBytes(source);
WzImage? image = null;
WzMapleVersion used = WzMapleVersion.UNKNOWN;
foreach (var version in new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS })
{
    var candidate = new WzImage(Path.GetFileName(source), new MemoryStream(original), version);
    try { if (candidate.ParseImage() && candidate["info"] is WzSubProperty && candidate["foothold"] is WzSubProperty) { image = candidate; used = version; break; } }
    catch (Exception) { /* Wrong encryption candidates are rejected, never written. */ }
    candidate.Dispose();
}
if (image is null) throw new Exception("No verified source profile.");
using (image)
{
    var oldPortals = (WzSubProperty)image["portal"];
    var spawn = oldPortals.WzProperties.OfType<WzSubProperty>().FirstOrDefault(p => p["pt"] is WzIntProperty pt && pt.Value == 0)
        ?? throw new Exception("Source has no measured spawn portal.");
    int x = ((WzIntProperty)spawn["x"]).Value, y = ((WzIntProperty)spawn["y"]).Value;
    image.Name = id.ToString() + ".img";
    var info = (WzSubProperty)image["info"];
    void SetInt(string name, int value) { info.RemoveProperty(name); info.AddProperty(new WzIntProperty(name, value)); }
    SetInt("returnMap", id); SetInt("forcedReturn", 999999999); SetInt("town", 1);
    info.RemoveProperty("mobRate"); info.AddProperty(new WzFloatProperty("mobRate", 0));
    foreach (string name in new[] { "onUserEnter", "onFirstUserEnter", "link" }) info.RemoveProperty(name);
    foreach (string name in new[] { "life", "portal", "reactor" }) { image.RemoveProperty(name); image.AddProperty(new WzSubProperty(name)); }
    var portal = new WzSubProperty("0");
    portal.AddProperty(new WzStringProperty("pn", "sp")); portal.AddProperty(new WzStringProperty("tn", ""));
    portal.AddProperty(new WzIntProperty("pt", 0)); portal.AddProperty(new WzIntProperty("tm", 999999999));
    portal.AddProperty(new WzIntProperty("x", x)); portal.AddProperty(new WzIntProperty("y", y));
    ((WzSubProperty)image["portal"]).AddProperty(portal);
    bool designedTown = args.Length == 4 && args[3] == "town";
    if (designedTown)
    {
        var sky = ((WzSubProperty)image["back"])["0"].DeepClone();
        foreach (string name in new[] { "foothold", "ladderRope", "back", "miniMap" }) image.RemoveProperty(name);
        image.AddProperty(new WzSubProperty("ladderRope"));
        var backgrounds = new WzSubProperty("back"); backgrounds.AddProperty(sky); image.AddProperty(backgrounds);
        for (int layerNo = 0; layerNo < 8; layerNo++)
        {
            image.RemoveProperty(layerNo.ToString());
            var layer = new WzSubProperty(layerNo.ToString());
            var layerInfo = new WzSubProperty("info"); layerInfo.AddProperty(new WzStringProperty("tS", "brownBrick"));
            layer.AddProperty(layerInfo); layer.AddProperty(new WzSubProperty("tile")); layer.AddProperty(new WzSubProperty("obj")); image.AddProperty(layer);
        }
        var layer0 = (WzSubProperty)image["0"];
        var tiles = (WzSubProperty)layer0["tile"]; int tileId = 0;
        void Tile(int tx, int ty, string u)
        {
            var t = new WzSubProperty((tileId++).ToString());
            t.AddProperty(new WzIntProperty("x", tx)); t.AddProperty(new WzIntProperty("y", ty));
            t.AddProperty(new WzStringProperty("u", u)); t.AddProperty(new WzIntProperty("no", 0)); t.AddProperty(new WzIntProperty("zM", 0)); tiles.AddProperty(t);
        }
        for (int tx = 0; tx < 2700; tx += 90)
        {
            Tile(tx, 27, "enH0");
            for (int ty = 27; ty < 300; ty += 60) Tile(tx, ty, "bsc");
        }
        var objects = (WzSubProperty)layer0["obj"]; int objectId = 0;
        void Object(int ox, int oy, string group, string asset, int z)
        {
            var o = new WzSubProperty((objectId++).ToString());
            foreach (var pair in new[] { ("oS", "guild"), ("l0", "syarenian"), ("l1", group), ("l2", asset) }) o.AddProperty(new WzStringProperty(pair.Item1, pair.Item2));
            foreach (var pair in new[] { ("x", ox), ("y", oy), ("z", z), ("f", 0), ("zM", 0) }) o.AddProperty(new WzIntProperty(pair.Item1, pair.Item2));
            objects.AddProperty(o);
        }
        Object(310, -202, "acc1", "39", 0); Object(2390, -202, "acc1", "39", 0);
        Object(1350, -157, "acc3", "0", 2);
        Object(890, -127, "acc2", "15", 1); Object(1810, -127, "acc2", "15", 1);
        Object(660, -50, "acc2", "14", 3); Object(2040, -50, "acc2", "14", 3);
        var footholds = new WzSubProperty("foothold"); var fl = new WzSubProperty("0"); var group = new WzSubProperty("0");
        for (int n = 1; n <= 30; n++)
        {
            var fh = new WzSubProperty(n.ToString());
            foreach (var pair in new[] { ("x1", (n - 1) * 90), ("y1", 0), ("x2", n * 90), ("y2", 0), ("prev", n == 1 ? 0 : n - 1), ("next", n == 30 ? 0 : n + 1) }) fh.AddProperty(new WzIntProperty(pair.Item1, pair.Item2));
            group.AddProperty(fh);
        }
        fl.AddProperty(group); footholds.AddProperty(fl); image.AddProperty(footholds);
        x = 1350; y = -10; ((WzIntProperty)portal["x"]).Value = x; ((WzIntProperty)portal["y"]).Value = y;
        SetInt("VRLeft", 0); SetInt("VRRight", 2700); SetInt("VRTop", -800); SetInt("VRBottom", 300);
        // Static composition from the SAME palette frames referenced by this map, not a game screenshot.
        string paletteRoot = Path.Combine(Path.GetDirectoryName(output)!, "palette");
        using var rendered = new System.Drawing.Bitmap(2700, 1100);
        using (var g = System.Drawing.Graphics.FromImage(rendered))
        {
            using var skyPixel = new System.Drawing.Bitmap(Path.Combine(paletteRoot, "skyStation", "0001.png"));
            g.Clear(skyPixel.GetPixel(0, 0));
            void Draw(string set, string path, int dx, int dy)
            {
                using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(paletteRoot, set, "index.json")));
                var match = index.RootElement.EnumerateArray().Single(e => e.GetProperty("path").GetString() == path);
                using var sprite = System.Drawing.Image.FromFile(Path.Combine(paletteRoot, set, match.GetProperty("file").GetString()!));
                g.DrawImageUnscaled(sprite, dx - match.GetProperty("originX").GetInt32(), dy + 800 - match.GetProperty("originY").GetInt32());
            }
            foreach (var t in tiles.WzProperties.Cast<WzSubProperty>()) Draw("brownBrick", "/" + ((WzStringProperty)t["u"]).Value + "/0", ((WzIntProperty)t["x"]).Value, ((WzIntProperty)t["y"]).Value);
            foreach (var o in objects.WzProperties.Cast<WzSubProperty>().OrderBy(o => ((WzIntProperty)o["z"]).Value))
                Draw("guild", "/syarenian/" + ((WzStringProperty)o["l1"]).Value + "/" + ((WzStringProperty)o["l2"]).Value + "/0", ((WzIntProperty)o["x"]).Value, ((WzIntProperty)o["y"]).Value);
        }
        Directory.CreateDirectory(output);
        using var preview = new System.Drawing.Bitmap(rendered, 1350, 550);
        preview.Save(Path.Combine(output, "town-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
        using var small = new System.Drawing.Bitmap(rendered, 169, 69);
        var townMinimap = new WzSubProperty("miniMap");
        foreach (var pair in new[] { ("width", 2700), ("height", 1100), ("centerX", 0), ("centerY", 800), ("mag", 4) }) townMinimap.AddProperty(new WzIntProperty(pair.Item1, pair.Item2));
        var minimapArt = new WzCanvasProperty("canvas"); minimapArt.PngProperty = new WzPngProperty(); minimapArt.PngProperty.PNG = small;
        townMinimap.AddProperty(minimapArt); image.AddProperty(townMinimap);
    }
    Directory.CreateDirectory(output);
    string imgPath = Path.Combine(output, image.Name);
    File.WriteAllBytes(imgPath, NxWzExport.SerializeImageAsWz(image));
    XElement? Xml(WzImageProperty p) => p switch
    {
        WzSubProperty sub => new XElement("imgdir", new XAttribute("name", sub.Name), sub.WzProperties.Select(Xml).Where(e => e != null)),
        WzIntProperty n => new XElement("int", new XAttribute("name", n.Name), new XAttribute("value", n.Value)),
        WzShortProperty n => new XElement("short", new XAttribute("name", n.Name), new XAttribute("value", n.Value)),
        WzLongProperty n => new XElement("long", new XAttribute("name", n.Name), new XAttribute("value", n.Value)),
        WzFloatProperty n => new XElement("float", new XAttribute("name", n.Name), new XAttribute("value", n.Value)),
        WzDoubleProperty n => new XElement("double", new XAttribute("name", n.Name), new XAttribute("value", n.Value)),
        WzStringProperty s => new XElement("string", new XAttribute("name", s.Name), new XAttribute("value", s.Value)),
        WzVectorProperty v => new XElement("vector", new XAttribute("name", v.Name), new XAttribute("x", v.X.Value), new XAttribute("y", v.Y.Value)),
        WzUOLProperty u => new XElement("uol", new XAttribute("name", u.Name), new XAttribute("value", u.Value)),
        WzCanvasProperty => null, // Server mechanics export omits image pixels, not a general WZ export.
        _ => throw new Exception("Unsupported map XML property: " + p.PropertyType)
    };
    new XDocument(new XElement("imgdir", new XAttribute("name", image.Name), image.WzProperties.Select(Xml).Where(e => e != null)))
        .Save(Path.Combine(output, image.Name + ".xml"));
    using var saved = new WzImage(image.Name, File.OpenRead(imgPath), used);
    if (!saved.ParseImage()) throw new Exception("Candidate reparse failed.");
    if (((WzSubProperty)saved["life"]).WzProperties.Count != 0 || ((WzSubProperty)saved["portal"]).WzProperties.Count != 1) throw new Exception("Unexpected life/portal.");
    foreach (string unchanged in new[] { "foothold", "back", "0", "1", "2", "3", "4", "5", "6", "7" })
    {
        var a = image[unchanged]; var b = saved[unchanged];
        if (a == null && b == null) continue;
        if (a == null || b == null || Xml(a)?.ToString() != Xml(b)?.ToString()) throw new Exception("Non-target geometry changed: " + unchanged);
    }
    if (saved["miniMap"] is WzSubProperty minimap && minimap["canvas"] is WzCanvasProperty minimapCanvas)
    {
        using var bitmap = minimapCanvas.PngProperty.GetImage(false);
        bitmap.Save(Path.Combine(output, "minimap-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
    }
    if (!File.ReadAllBytes(source).SequenceEqual(original)) throw new Exception("Source changed.");
    File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new { id, name = "起始之城・中央廣場（骨架原型）", sourceTemplate = Path.GetFileName(source), sourceProfile = used.ToString(), sourceSha256 = Convert.ToHexString(SHA256.HashData(original)), candidateSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imgPath))), spawn = new { x, y }, inheritedArtAndGeometry = !designedTown, designedTown, tileSet = designedTown ? "brownBrick" : null, exportedServerXml = "Mechanics only; canvas omitted", installed = false, gameTested = false, next = "Replace Orbis-derived visuals, add gate/NPC, verify asset references and playable movement before publication" }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS: candidate IMG reparse; intended geometry roundtrip; life cleared; single spawn; source unchanged. Output: " + output);
}
