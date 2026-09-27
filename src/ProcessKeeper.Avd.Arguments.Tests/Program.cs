using ProcessKeeper.Core;

ProcessKeeper.Core.L.Language = "zh-Hans";

var count = 0;
const string launcher = @"C:\Android SDK\emulator\emulator.exe";
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    count++; Console.WriteLine("PASS: " + message);
}
AvdLaunchArguments Prepare(params string[] parameters) => AvdArgumentPolicy.Prepare([launcher, .. parameters], launcher);
void Reject(string message, params string[] parameters)
{
    try { Prepare(parameters); }
    catch (InvalidDataException) { Check(true, message); return; }
    throw new Exception("FAIL: accepted " + message);
}
void RejectInput(string message, IReadOnlyList<string> arguments, string expected)
{
    try { AvdArgumentPolicy.Prepare(arguments, expected); }
    catch (InvalidDataException) { Check(true, message); return; }
    throw new Exception("FAIL: accepted " + message);
}

var observed = Prepare("-avd", "Auradio_API_36_1", "-port", "5562", "-no-snapshot-load", "-no-snapshot-save", "-no-window", "-no-boot-anim", "-gpu", "host", "-feature", "-Vulkan");
Check(observed.AvdName == "Auradio_API_36_1" && observed.ConsolePort == 5562, "observed launch keeps exact AVD and console port");
Check(observed.Arguments.SequenceEqual(["-avd", "Auradio_API_36_1", "-port", "5562", "-no-snapshot-load", "-no-snapshot-save", "-no-boot-anim", "-gpu", "host", "-feature", "-Vulkan"]), "observed launch removes only no-window and preserves safe token order");
Check(observed.RemovedOptions.Count == 1 && observed.RemovedOptions[0].Contains("-no-window"), "every observed change has a removal explanation");
Check(observed.Arguments[0] != launcher, "returned arguments exclude executable argv zero");
var cold = Prepare("@Phone_API_36", "-snapshot", "saved_quickboot", "-no-snapshot-save");
Check(cold.AvdName == "Phone_API_36" && cold.ConsolePort is null, "at-name form canonicalized without inventing a port");
Check(cold.Arguments.SequenceEqual(["-avd", "Phone_API_36", "-no-snapshot-save", "-no-snapshot-load"]), "snapshot name removed and cold boot added without changing save policy");
Check(cold.RemovedOptions.Count == 3 && !string.Join('|', cold.RemovedOptions).Contains("saved_quickboot"), "at-name, snapshot and cold boot changes explained without original snapshot value");
var noSnapshot = Prepare("-avd", "Phone", "-no-snapshot");
Check(noSnapshot.Arguments.Contains("-no-snapshot") && noSnapshot.Arguments.Count(v => v == "-no-snapshot-load") == 1, "no-snapshot policy retained with explicit cold-boot guarantee");
var embedding = Prepare("-avd", "Phone", "-qt-hide-window", "-grpc-use-token", "-grpc-use-jwt", "-idle-grpc-timeout", "300", "-grpc", "8554", "-studio-params", @"C:\Private\params secret.json", "-grpc-tls-key", @"C:\Private\private-key.pem", "-grpc-tls-cer", @"C:\Private\cert.pem", "-grpc-tls-ca", @"C:\Private\ca.pem");
Check(embedding.Arguments.SequenceEqual(["-avd", "Phone", "-no-snapshot-load"]), "IDE window, gRPC, lifecycle and TLS parameters removed completely");
Check(!string.Join('|', embedding.RemovedOptions).Contains("Private") && !string.Join('|', embedding.RemovedOptions).Contains("8554"), "removed lifecycle diagnostics never include values or secret paths");
Check(Prepare("-avd", "Phone", "-grpc", "0", "-idle-grpc-timeout", "0").Arguments.Count == 3, "zero lifecycle values are parsed and removed");

var paths = Prepare("-avd", "Phone", "-datadir", @"D:\AVD Data\..\AVD Data\Phone.avd", "-sdcard", @"E:\Disk Images\sd card.img");
Check(paths.Arguments[3] == @"D:\AVD Data\Phone.avd" && paths.Arguments[5] == @"E:\Disk Images\sd card.img", "already split quoted paths remain individual absolute tokens with spaces");
Check(paths.RemovedOptions.Any(change => change.Contains("-datadir")), "normalization of existing path is disclosed");
var casePath = AvdArgumentPolicy.Prepare([@"c:/android sdk/emulator/emulator.exe", "-avd", "Phone"], launcher);
Check(casePath.AvdName == "Phone", "argv zero normalized case insensitively against verified launcher");
Check(Prepare("-avd", "Phone", "-ports", "5556,5559").ConsolePort == 5556, "explicit nonadjacent console and adb pair preserved");
Check(Prepare("-avd", "Phone", "-port", "5682").ConsolePort == 5682, "highest supported even console port preserved");
Check(Prepare("-avd", "Phone", "-ports", "5682,5683").Arguments.Contains("5682,5683"), "highest adjacent pair preserved");
Check(Prepare("-avd", "Phone", "-feature", "-Vulkan,GLDirectMem", "-feature", "GuestAngle").Arguments.Count(v => v == "-feature") == 2, "separate disjoint feature groups preserved including leading minus value");
Check(Prepare("-avd", "Phone", "-camera-front", @"imagefile:C:\Images\test frame.png").Arguments.Contains(@"imagefile:C:\Images\test frame.png"), "file-backed camera validates absolute media path");

foreach (var flag in new[] { "-no-snapshot-load", "-no-snapshot-save", "-no-snapshot", "-no-snapstorage", "-no-boot-anim", "-no-audio", "-no-skin", "-no-jni", "-no-accel", "-verbose", "-show-kernel", "-netfast", "-no-metrics" })
    Check(Prepare("-avd", "Phone", flag).Arguments.Contains(flag), "allowlisted flag preserved: " + flag);
foreach (var pair in new[]
{
    ("-gpu", "software"), ("-gpu", "swiftshader_indirect"), ("-accel", "auto"), ("-engine", "qemu2"),
    ("-memory", "4096"), ("-cores", "4"), ("-dpi-device", "420"), ("-scale", "0.75"),
    ("-netdelay", "none"), ("-netdelay", "10:20"), ("-netspeed", "full"), ("-netspeed", "1000:2000"),
    ("-camera-back", "virtualscene"), ("-camera-front", "webcam0"), ("-skin", "1080x1920"),
    ("-timezone", "Asia/Shanghai"), ("-dns-server", "1.1.1.1,8.8.8.8"), ("-http-proxy", "127.0.0.1:7890")
}) Check(Prepare("-avd", "Phone", pair.Item1, pair.Item2).Arguments.Contains(pair.Item2), "allowlisted value preserved: " + pair.Item1 + " " + pair.Item2);
foreach (var option in new[] { "-sysdir", "-datadir", "-data", "-cache", "-sdcard", "-system", "-vendor", "-kernel", "-ramdisk", "-snapstorage", "-skindir" })
    Check(Prepare("-avd", "Phone", option, @"D:\absolute path\resource").Arguments.Contains(@"D:\absolute path\resource"), "absolute resource path retained: " + option);

foreach (var option in new[] { "-wipe-data", "-read-only", "-writable-system", "-qemu", "-shell", "-shell-serial", "-script", "-script-file", "-loadvm", "-initdata", "-init-data", "-partition-size", "-append", "-prop", "-boot-property", "-netsim-args", "-fuchsia", "-snapshot-list", "-unknown-flag", "--wipe-data", "-avd=Other" })
    Reject("dangerous or unknown option rejected: " + option, "-avd", "Phone", option);
foreach (var option in new[] { "-avd", "-port", "-ports", "-grpc", "-idle-grpc-timeout", "-datadir", "-snapshot", "-gpu", "-feature" })
{
    var before = option == "-avd" ? Array.Empty<string>() : new[] { "-avd", "Phone" };
    Reject("missing value rejected: " + option, [.. before, option]);
    Reject("next option cannot be swallowed: " + option, [.. before, option, "-no-window"]);
}
Reject("feature cannot swallow destructive QEMU marker", "-avd", "Phone", "-feature", "-qemu");
Reject("feature cannot swallow GPU option", "-avd", "Phone", "-feature", "-gpu", "host");
Reject("unrecognized feature rejected", "-avd", "Phone", "-feature", "UnreviewedFeature");
Reject("conflicting feature signs rejected", "-avd", "Phone", "-feature", "Vulkan", "-feature", "-Vulkan");
Reject("duplicate feature rejected", "-avd", "Phone", "-feature", "-Vulkan,-Vulkan");
Reject("stray value rejected instead of becoming second command", "-avd", "Phone", "extra.exe");
Reject("flag never consumes stray token", "-avd", "Phone", "-grpc-use-token", "private-token-value");
Reject("missing AVD rejected", "-no-window");
Reject("duplicate AVD option rejected", "-avd", "Phone", "-avd", "Phone");
Reject("mixed AVD name forms rejected", "-avd", "Phone", "@Other");
Reject("duplicate at-name rejected", "@Phone", "@Phone");
Reject("AVD path traversal rejected", "-avd", "../Phone");
Reject("AVD shell text rejected", "-avd", "Phone & cmd");
Reject("AVD surrounding shell quotes are not reinterpreted", "-avd", "\"Phone\"");
foreach (var value in new[] { "5553", "5555", "5684", "65536", "-1", " 5562", "5562 ", "5562x", "5562,5563", "999999999999999999999" })
    Reject("invalid console port rejected: " + value, "-avd", "Phone", "-port", value);
foreach (var value in new[] { "5562", "5562,5562", "5563,5564", "5554,5684", "5554,0", "5554,5555,5557" })
    Reject("invalid console/adb pair rejected: " + value, "-avd", "Phone", "-ports", value);
Reject("duplicate explicit port rejected", "-avd", "Phone", "-port", "5562", "-port", "5562");
Reject("port and ports conflict rejected", "-avd", "Phone", "-ports", "5562,5563", "-port", "5562");
Reject("duplicate no-window rejected", "-avd", "Phone", "-no-window", "-no-window");
Reject("conflicting acceleration flags rejected", "-avd", "Phone", "-no-accel", "-accel", "on");
foreach (var path in new[] { "relative", @"C:relative", @"\relative", @"%USERPROFILE%\Phone.avd", @"\\?\C:\Phone.avd", @"\\.\pipe\foo", @"C:\Phone\*.img", @"C:\Phone\file:stream", "\"C:\\Phone Data\"" })
    Reject("unsafe or relative path rejected: " + path, "-avd", "Phone", "-datadir", path);
Reject("removed IDE path must still be absolute", "-avd", "Phone", "-studio-params", "relative.json");
Reject("camera media cannot inherit unknown cwd", "-avd", "Phone", "-camera-front", "imagefile:relative.png");
Reject("newlines rejected in every argument", "-avd", "Phone", "-snapshot", "name\nsecond");
Reject("NUL rejected in every argument", "-avd", "Phone\0Other");
Reject("invalid GPU value rejected", "-avd", "Phone", "-gpu", "host -wipe-data");
Reject("reverse delay range rejected", "-avd", "Phone", "-netdelay", "20:10");
Reject("negative memory rejected", "-avd", "Phone", "-memory", "-1");
Reject("invalid proxy path rejected", "-avd", "Phone", "-http-proxy", "http://localhost:7890/run");
Reject("invalid DNS token rejected", "-avd", "Phone", "-dns-server", "one two");
RejectInput("different launcher cannot be replayed", [@"C:\Other\emulator.exe", "-avd", "Phone"], launcher);
RejectInput("QEMU binary cannot impersonate emulator launcher", [@"C:\Android SDK\emulator\qemu.exe", "-avd", "Phone"], @"C:\Android SDK\emulator\qemu.exe");
RejectInput("argv zero cannot be a relative executable", ["emulator.exe", "-avd", "Phone"], launcher);
RejectInput("argv zero must be pre-tokenized without quotes", ["\"" + launcher + "\"", "-avd", "Phone"], launcher);
RejectInput("null token rejected safely", [launcher, "-avd", null!], launcher);
RejectInput("oversized command rejected", [launcher, "-avd", new string('a', 32760)], launcher);
try { Prepare("-avd", "Phone", "-token=SUPER_PRIVATE_VALUE"); throw new Exception("FAIL: unknown attached credential option accepted"); }
catch (InvalidDataException ex) { Check(!ex.Message.Contains("SUPER_PRIVATE_VALUE"), "unknown option error does not leak attached credentials"); }

Console.WriteLine($"PASS: {count} AVD argument assertions; no emulator or external process was started.");
