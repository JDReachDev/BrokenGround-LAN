using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Broken Ground LAN patcher: injects calls to BGLan.dll into the game's Assembly-CSharp.dll.
// The untouched original is kept as Assembly-CSharp.orig.dll and is always the input, so patching twice is safe.

Console.WriteLine("Broken Ground LAN patcher");
string gameDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? FindGameDir();
while (gameDir == null || !File.Exists(Path.Combine(gameDir, "BrokenGround.exe")))
{
    if (Console.IsInputRedirected)
    {
        Console.WriteLine("Game folder not found. Usage: BGLanPatcher \"C:\\...\\steamapps\\common\\Broken Ground\"");
        return Exit(1);
    }
    Console.WriteLine("Could not find Broken Ground automatically.");
    Console.WriteLine("In Steam: right click Broken Ground > Manage > Browse local files, copy that folder path and paste it here:");
    gameDir = (Console.ReadLine() ?? "").Trim().Trim('"');
}
Console.WriteLine("Game folder: " + gameDir);
while (System.Diagnostics.Process.GetProcessesByName("BrokenGround").Length > 0)
{
    Console.WriteLine("Broken Ground is running: close the game, then press Enter.");
    if (Console.IsInputRedirected) return Exit(1);
    Console.ReadLine();
}
string managed = Path.Combine(gameDir, "BrokenGround_Data", "Managed");
string target = Path.Combine(managed, "Assembly-CSharp.dll");
string original = Path.Combine(managed, "Assembly-CSharp.orig.dll");
string modSource = Path.Combine(AppContext.BaseDirectory, "BGLan.dll");
string modTarget = Path.Combine(managed, "BGLan.dll");

if (args.Contains("--uninstall"))
{
    if (File.Exists(original)) File.Copy(original, target, true);
    if (File.Exists(modTarget)) File.Delete(modTarget);
    foreach (string f in new[] { ServerScript, ServerBat })
    {
        string p = Path.Combine(gameDir, f);
        if (File.Exists(p)) File.Delete(p);
    }
    Console.WriteLine("Mod removed, the game has been restored.");
    return Exit(0);
}

if (!File.Exists(original))
{
    if (IsPatched(target))
    {
        Console.WriteLine("Assembly-CSharp.dll is already modified but the original backup is missing. Verify the game files in Steam and try again.");
        return Exit(1);
    }
    File.Copy(target, original);
    Console.WriteLine("Backup created: Assembly-CSharp.orig.dll");
}
if (!File.Exists(modSource))
{
    Console.WriteLine("BGLan.dll was not found next to the patcher.");
    return Exit(1);
}
File.Copy(modSource, modTarget, true);
InstallDedicatedServer(gameDir);

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
var readParams = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };

using (var mod = AssemblyDefinition.ReadAssembly(modTarget, readParams))
using (var game = AssemblyDefinition.ReadAssembly(original, readParams))
{
    ModuleDefinition module = game.MainModule;
    TypeDefinition hooks = mod.MainModule.GetType("BGLan.Hooks");
    MethodReference Hook(string name) => module.ImportReference(hooks.Methods.Single(m => m.Name == name));

    // 1. Start the mod as soon as the game's GameManager wakes up.
    MethodDefinition awake = Method(module, "GameManager", "Awake");
    awake.Body.GetILProcessor().InsertBefore(awake.Body.Instructions[0], Instruction.Create(OpCodes.Call, Hook("Init")));

    // 2. Unlock everything when the mod says so.
    MethodDefinition isUnlocked = Method(module, "Unlockable", "IsUnlocked");
    {
        ILProcessor il = isUnlocked.Body.GetILProcessor();
        Instruction first = isUnlocked.Body.Instructions[0];
        il.InsertBefore(first, Instruction.Create(OpCodes.Call, Hook("ForceUnlocked")));
        il.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(first, Instruction.Create(OpCodes.Ret));
    }

    // 3. Report deaths after damage: HandleTakeDamage(int damage, int dealtBySoldierId, bool headshot, string weaponID, int projectileID, bool electricDmg)
    MethodDefinition handleDamage = Method(module, "HealthManager", "HandleTakeDamage");
    BeforeEveryReturn(handleDamage, il => new[]
    {
        il.Create(OpCodes.Ldarg_0),
        il.Create(OpCodes.Ldarg_2),
        il.Create(OpCodes.Ldarg_3),
        il.Create(OpCodes.Ldarg_S, handleDamage.Parameters[3]),
        il.Create(OpCodes.Call, Hook("AfterHandleTakeDamage")),
    });

    // 4. Save stats on the host after each game.
    MethodDefinition endGame = Method(module, "GameManager", "EndGame");
    BeforeEveryReturn(endGame, il => new[] { il.Create(OpCodes.Call, Hook("AfterEndGame")) });

    // 5. Parse the ammo packet correctly: OnClientReceivedAmmo(Packet response, BinaryReader reader, IPEndPoint source)
    MethodDefinition onAmmo = Method(module, "TNetConnectionHandler", "OnClientReceivedAmmo");
    {
        ILProcessor il = onAmmo.Body.GetILProcessor();
        Instruction first = onAmmo.Body.Instructions[0];
        il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_2));
        il.InsertBefore(first, Instruction.Create(OpCodes.Call, Hook("OnAmmo")));
        il.InsertBefore(first, Instruction.Create(OpCodes.Ret));
    }

    game.Write(target);
}

Console.WriteLine("Done! Broken Ground is ready for LAN play.");
return Exit(0);

// The dedicated server console script goes in the game folder, with a .bat to start it there and one next to the
// patcher (pointing to the game folder) so players find it easily.
static void InstallDedicatedServer(string gameDir)
{
    string script = Path.Combine(AppContext.BaseDirectory, ServerScript);
    if (!File.Exists(script)) return;
    File.Copy(script, Path.Combine(gameDir, ServerScript), true);
    File.WriteAllText(Path.Combine(gameDir, ServerBat),
        "@echo off\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0" + ServerScript + "\"\r\n");
    try
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, ServerBat),
            "@echo off\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(gameDir, ServerScript) + "\"\r\n");
    }
    catch { }
    Console.WriteLine("Dedicated server installed: run \"" + ServerBat + "\"");
}

static MethodDefinition Method(ModuleDefinition module, string type, string name)
{
    TypeDefinition t = module.GetType(type) ?? throw new Exception("Type not found: " + type);
    return t.Methods.Single(m => m.Name == name);
}

// Inserts code before each 'ret'. The old ret becomes the first new instruction so branches that jumped to it still run the hook.
static void BeforeEveryReturn(MethodDefinition method, Func<ILProcessor, Instruction[]> make)
{
    method.Body.SimplifyMacros();
    ILProcessor il = method.Body.GetILProcessor();
    foreach (Instruction ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
    {
        Instruction[] code = make(il);
        ret.OpCode = code[0].OpCode;
        ret.Operand = code[0].Operand;
        Instruction last = ret;
        for (int i = 1; i < code.Length; i++)
        {
            il.InsertAfter(last, code[i]);
            last = code[i];
        }
        il.InsertAfter(last, il.Create(OpCodes.Ret));
    }
    method.Body.OptimizeMacros();
}

static bool IsPatched(string path)
{
    using var asm = AssemblyDefinition.ReadAssembly(path);
    return asm.MainModule.AssemblyReferences.Any(r => r.Name == "BGLan");
}

static string FindGameDir()
{
    string here = AppContext.BaseDirectory;
    for (DirectoryInfo d = new DirectoryInfo(here); d != null; d = d.Parent)
    {
        if (File.Exists(Path.Combine(d.FullName, "BrokenGround.exe"))) return d.FullName;
    }
    foreach (string library in SteamLibraries())
    {
        string p = Path.Combine(library, "steamapps", "common", "Broken Ground");
        if (File.Exists(Path.Combine(p, "BrokenGround.exe"))) return p;
    }
    foreach (string drive in Directory.GetLogicalDrives())
    {
        foreach (string lib in new[] { @"SteamLibrary\steamapps\common\Broken Ground", @"Program Files (x86)\Steam\steamapps\common\Broken Ground", @"Steam\steamapps\common\Broken Ground" })
        {
            string p = Path.Combine(drive, lib);
            if (File.Exists(Path.Combine(p, "BrokenGround.exe"))) return p;
        }
    }
    return null;
}

// Steam lists every library folder in steamapps\libraryfolders.vdf as lines like: "path"  "D:\\SteamLibrary"
static List<string> SteamLibraries()
{
    var result = new List<string>();
    try
    {
        string steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam == null) return result;
        result.Add(steam);
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return result;
        foreach (string line in File.ReadAllLines(vdf))
        {
            string[] parts = line.Trim().Split('"');
            if (parts.Length >= 4 && parts[1] == "path") result.Add(parts[3].Replace(@"\\", @"\"));
        }
    }
    catch { }
    return result;
}

static int Exit(int code)
{
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine("Press Enter to close...");
        Console.ReadLine();
    }
    return code;
}

partial class Program
{
    const string ServerScript = "BGLan_Server.ps1";
    const string ServerBat = "Start Dedicated Server.bat";
}
