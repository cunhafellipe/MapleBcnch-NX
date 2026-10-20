using System;
using System.IO;
using MapleBench.Services.Nx;
using MapleLib.WzLib;

// Usage: NxMergeMap <target-Map.nx> <map.img> <obj.img>
if (args.Length != 3)
{
    Console.WriteLine("Usage: NxMergeMap <target-Map.nx> <map.img> <obj.img>");
    return 1;
}

string targetNxPath = Path.GetFullPath(args[0]);
string mapImgPath = Path.GetFullPath(args[1]);
string objImgPath = Path.GetFullPath(args[2]);

if (!File.Exists(targetNxPath)) throw new FileNotFoundException("Target Map.nx not found: " + targetNxPath);
if (!File.Exists(mapImgPath)) throw new FileNotFoundException("Map IMG not found: " + mapImgPath);
if (!File.Exists(objImgPath)) throw new FileNotFoundException("Obj IMG not found: " + objImgPath);

Console.WriteLine($"Loading base Map.nx ({new FileInfo(targetNxPath).Length / 1024 / 1024} MB)...");
using var nxArchive = NxArchive.Open(targetNxPath);
var conversion = NxToWzFile.Convert(nxArchive, NxLoadMode.MetadataOnly);
WzFile baseMapWz = conversion.File;

Console.WriteLine($"Parsing map IMG: {Path.GetFileName(mapImgPath)}...");
using var mapImg = new WzImage(Path.GetFileName(mapImgPath), File.OpenRead(mapImgPath), WzMapleVersion.BMS);
if (!mapImg.ParseImage()) throw new InvalidDataException("Failed to parse map IMG: " + mapImgPath);

Console.WriteLine($"Parsing obj IMG: {Path.GetFileName(objImgPath)}...");
using var objImg = new WzImage(Path.GetFileName(objImgPath), File.OpenRead(objImgPath), WzMapleVersion.BMS);
if (!objImg.ParseImage()) throw new InvalidDataException("Failed to parse obj IMG: " + objImgPath);

// Locate Map/Map9 under baseMapWz
var mapDir = (WzDirectory)baseMapWz.WzDirectory["Map"];
string mapGroupName = "Map" + Path.GetFileNameWithoutExtension(mapImgPath)[0];
var mapSubDir = (WzDirectory)mapDir[mapGroupName];

var existingMap = mapSubDir[mapImg.Name];
if (existingMap != null)
{
    Console.WriteLine($"Replacing existing map: {mapImg.Name}");
    mapSubDir.WzImages.Remove((WzImage)existingMap);
}
else
{
    Console.WriteLine($"Adding new map: {mapImg.Name}");
}
mapSubDir.AddImage(mapImg.DeepClone() as WzImage ?? throw new Exception("Clone failed"));

// Locate Obj directory
var objDir = (WzDirectory)baseMapWz.WzDirectory["Obj"];
var existingObj = objDir[objImg.Name];
if (existingObj != null)
{
    Console.WriteLine($"Replacing existing obj: {objImg.Name}");
    objDir.WzImages.Remove((WzImage)existingObj);
}
else
{
    Console.WriteLine($"Adding new obj: {objImg.Name}");
}
objDir.AddImage(objImg.DeepClone() as WzImage ?? throw new Exception("Clone failed"));

// Backup original target NX
string backupPath = targetNxPath + ".bak." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
File.Copy(targetNxPath, backupPath, true);
Console.WriteLine($"Backup created at: {backupPath}");

// Write back using NxWriter
Console.WriteLine("Rebuilding Map.nx using project's NxWriter...");
byte[] newNxBytes = NxWriter.Write(baseMapWz, out NxWriteReport report);

string tempOut = targetNxPath + ".tmp";
File.WriteAllBytes(tempOut, newNxBytes);

// Atomic replace
File.Move(tempOut, targetNxPath, true);
Console.WriteLine($"SUCCESS: Successfully merged and updated {targetNxPath} ({newNxBytes.Length / 1024 / 1024} MB)!");
return 0;
