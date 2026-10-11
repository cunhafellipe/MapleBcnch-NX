using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using MapleBench.Services.Nx;

if (args.Length == 2 && args[0] == "verify")
{
    using var stream = File.OpenRead(Path.Combine(args[1], "candidate.img"));
    using var candidate = new WzImage("candidate.img", stream, WzMapleVersion.BMS);
    if (!candidate.ParseImage()) throw new Exception("Candidate IMG parse failed.");
    if (candidate["value"] is not WzIntProperty value || value.Value != 42) throw new Exception("Scalar mismatch.");
    if (candidate["untouched"] is not WzStringProperty text || text.Value != "preserve-me") throw new Exception("Non-target scalar changed.");
    var sprite = (WzCanvasProperty)candidate["sprite"];
    var origin = (WzVectorProperty)sprite["origin"];
    if (origin.X.Value != 1 || origin.Y.Value != 2) throw new Exception("Origin changed.");
    using var pixels = sprite.PngProperty.GetImage(false);
    using var exported = new Bitmap(Path.Combine(args[1], "candidate.png"));
    foreach (var bitmap in new[] { pixels, exported })
    {
        if (bitmap.Width != 4 || bitmap.Height != 4) throw new Exception("Dimensions mismatch.");
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
            if (bitmap.GetPixel(x, y).ToArgb() != Color.Blue.ToArgb()) throw new Exception("Pixel mismatch.");
    }
    Console.WriteLine("PASS: independent IMG parse, scalar, non-target string, origin, every canvas/PNG pixel.");
    return;
}
if (args.Length != 1) throw new ArgumentException("Provide a NEW fixture directory.");
if (Directory.Exists(args[0])) throw new IOException("Fixture directory already exists.");
Directory.CreateDirectory(args[0]);
using var image = new WzImage("Fixture.img");
image.AddProperty(new WzIntProperty("value", 7));
image.AddProperty(new WzStringProperty("untouched", "preserve-me"));
using var original = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
using (var graphics = Graphics.FromImage(original)) graphics.Clear(Color.Red);
var canvas = new WzCanvasProperty("sprite") { PngProperty = new WzPngProperty() };
canvas.PngProperty.PNG = original;
canvas.AddProperty(new WzVectorProperty("origin", 1, 2));
image.AddProperty(canvas);
File.WriteAllBytes(Path.Combine(args[0], "Fixture.img"), NxWzExport.SerializeImageAsWz(image));
using var replacement = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
using (var graphics = Graphics.FromImage(replacement)) graphics.Clear(Color.Blue);
replacement.Save(Path.Combine(args[0], "replacement.png"), ImageFormat.Png);
Console.WriteLine("Generated scalar, untouched string, origin and red canvas; replacement is blue.");
