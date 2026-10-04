using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace TerrariaAgent.Host
{
    // Own, fixed-purpose loader. No mod discovery, upstream injector, executable
    // replacement, installation writes, downloaded update or hidden background GUI.
    internal static class Program
    {
        private const string GameSha256 = "960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3";
        private static readonly Dictionary<string, Assembly> Embedded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static Assembly _game;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);

        [STAThread]
        private static int Main(string[] args)
        {
            string runDirectory = null;
            try
            {
                if (args.Length < 2 || args.Length > 4 || args[0] != "--runtime-root")
                    throw new ArgumentException("Usage: TerrariaAgent.Host.exe --runtime-root <workspace runtime> [--allow-initial-controller-start] [--enable-stage-b]");
                var flags = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 2; i < args.Length; ++i)
                    if ((args[i] != "--allow-initial-controller-start" && args[i] != "--enable-stage-b") || !flags.Add(args[i]))
                        throw new ArgumentException("Unknown or repeated Host option.");
                bool allowInitialControllerStart = flags.Contains("--allow-initial-controller-start");
                bool enableStageB = flags.Contains("--enable-stage-b");
                string repository = FindRepository();
                string expectedRoot = Path.GetFullPath(Path.Combine(repository, "..", "..", "work", "terraria-runtime"));
                string runtimeRoot = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
                if (!string.Equals(runtimeRoot, expectedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Runtime must be the project's isolated work/terraria-runtime directory.");
                RejectReparseAncestors(runtimeRoot);
                if (allowInitialControllerStart && File.Exists(Path.Combine(runtimeRoot, "STOP")))
                    throw new InvalidOperationException("An existing emergency STOP blocks initial controller start. It is never cleared by operator_arm.");
                string gameDirectory = Path.Combine(runtimeRoot, "game");
                string gameFile = Path.Combine(gameDirectory, "Terraria.exe");
                string saveRoot = Path.Combine(runtimeRoot, "saves", "main");
                RejectReparseAncestors(gameFile);
                RejectReparseAncestors(saveRoot);
                if (!string.Equals(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                    gameDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Deploy the Host into the isolated game directory before launch; XNA resolves Content from the entry assembly directory.");
                PreparationState prepared;
                using (var stream = File.OpenRead(Path.Combine(runtimeRoot, "preparation-status.json")))
                    prepared = (PreparationState)new DataContractJsonSerializer(typeof(PreparationState)).ReadObject(stream);
                if (prepared == null || prepared.Status != "prepared_not_launched" || !prepared.BackupStable || !prepared.GameCopyVerified || !prepared.ProcessVisibilityVerified)
                    throw new InvalidOperationException("Stable save backup and verified game copy are required.");
                if (!string.Equals(FileVersionInfo.GetVersionInfo(gameFile).FileVersion, "1.4.5.8", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unsupported Terraria version; inspect its API before use.");
                using (var stream = File.OpenRead(gameFile))
                using (var sha = SHA256.Create())
                {
                    string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                    if (!string.Equals(actual, GameSha256, StringComparison.Ordinal))
                        throw new InvalidOperationException("Terraria executable differs from the statically inspected build.");
                }
                string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                runDirectory = Path.Combine(runtimeRoot, "logs", runId);
                Directory.CreateDirectory(runDirectory);
                Directory.CreateDirectory(saveRoot);
                File.WriteAllText(Path.Combine(runDirectory, "host-info.txt"),
                    "runId=" + runId + "\r\nmode=rules\r\ngameVersion=1.4.5.8\r\ngameSHA256=" + GameSha256 +
                    "\r\ngameDirectory=" + gameDirectory + "\r\nsaveRoot=" + saveRoot + "\r\nloader=own-fixed-purpose-host\r\n" +
                    "initialControllerStartAllowed=" + allowInitialControllerStart + "\r\nstageBEnabled=" + enableStageB + "\r\n");
                Directory.SetCurrentDirectory(gameDirectory);
                // Steam's documented development launch path prevents
                // RestartAppIfNecessary from opening the original Steam installation.
                // Keep the game's unmodified Steam initialization and ownership check.
                string appIdFile = Path.Combine(gameDirectory, "steam_appid.txt");
                RejectReparseAncestors(appIdFile);
                if (!File.Exists(appIdFile) || new FileInfo(appIdFile).Length > 64 || File.ReadAllText(appIdFile).Trim() != "105600")
                    throw new InvalidOperationException("The copied game's steam_appid.txt must contain 105600 to prevent a Steam relaunch into the original installation.");
                string harmonyFile = Path.Combine(gameDirectory, "0Harmony.dll");
                RejectReparseAncestors(harmonyFile);
                using (var stream = File.OpenRead(harmonyFile))
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") != "498EDE1D20A87AFAA6C7B1ED5B3BEB505B01DC418B952C1BE5C322204404B033")
                        throw new InvalidOperationException("The deployed Harmony DLL differs from the pinned official package.");
                if (!SetDllDirectory(gameDirectory)) throw new InvalidOperationException("Could not restrict native DLL search to isolated game directory.");
                AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
                _game = Assembly.LoadFrom(gameFile);
                foreach (string resource in _game.GetManifestResourceNames())
                {
                    if (!resource.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    using (var input = _game.GetManifestResourceStream(resource))
                    using (var bytes = new MemoryStream())
                    {
                        if (input == null || input.Length > 32 * 1024 * 1024) throw new InvalidDataException("Unexpected embedded dependency.");
                        input.CopyTo(bytes);
                        var assembly = Assembly.Load(bytes.ToArray());
                        Embedded[assembly.GetName().Name] = assembly;
                    }
                }

                // Main's static constructor captures this path. Set it BEFORE loading
                // bridge types, inspecting Main fields, or preparing Harmony patches.
                var saveField = _game.GetType("Terraria.Program", true).GetField("SavePath", BindingFlags.Public | BindingFlags.Static);
                if (saveField == null || saveField.FieldType != typeof(string)) throw new MissingFieldException("Program.SavePath");
                saveField.SetValue(null, saveRoot);
                byte[] secret = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
                string token = Convert.ToBase64String(secret);
                var bridge = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TerrariaAgent.Bridge.dll"));
                var startup = bridge.GetType("TerrariaAgent.Bridge.Startup", true);
                startup.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static).Invoke(null,
                    new object[] { runtimeRoot, saveRoot, runDirectory, runId, token, allowInitialControllerStart, enableStageB });
                File.WriteAllText(Path.Combine(runtimeRoot, "current-run.txt"), runDirectory);
                Console.WriteLine("Isolated Terraria startup. Controls default to manual. Runtime planner: rules.");
                Console.WriteLine("Arm: Ctrl+Shift+Insert. Stop: Ctrl+Shift+Backspace. Physical movement/jump takes over.");
                if (allowInitialControllerStart) Console.WriteLine("Initial explicit controller start is enabled once for this run. Reconnect and safety stops cannot renew it.");
                if (enableStageB) Console.WriteLine("Stage B code preview enabled. Gameplay integration remains unverified until tested in the game.");
                // This is the first game entry invocation. All launch arguments are
                // chosen here; external world/cloudworld/savepath arguments are refused.
                try
                {
                    _game.EntryPoint.Invoke(null, new object[] { new[] { "-savedirectory", saveRoot } });
                }
                finally
                {
                    startup.GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                }
                return 0;
            }
            catch (Exception error)
            {
                var actual = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
                if (runDirectory != null) File.WriteAllText(Path.Combine(runDirectory, "host-error.txt"), actual.ToString());
                Console.Error.WriteLine("Startup stopped: " + actual.Message);
                return 1;
            }
        }

        private static string FindRepository()
        {
            for (var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                    File.Exists(Path.Combine(directory.FullName, "src", "AgentHost", "AgentHost.csproj"))) return directory.FullName;
            // Build outputs live under workspace work/terraria-runtime/build/AgentHost.
            for (var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "outputs", "terraria-agent");
                if (File.Exists(Path.Combine(candidate, "AGENTS.md")) &&
                    File.Exists(Path.Combine(candidate, "src", "AgentHost", "AgentHost.csproj"))) return candidate;
            }
            throw new InvalidOperationException("Project repository could not be resolved from the executable location.");
        }

        private static void RejectReparseAncestors(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Reparse paths are not allowed in runtime preparation.");
                current = Path.GetDirectoryName(current);
            }
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs arguments)
        {
            string name = new AssemblyName(arguments.Name).Name;
            if (name == "Terraria") return _game;
            Assembly embedded;
            if (Embedded.TryGetValue(name, out embedded)) return embedded;
            // The only external managed libraries we deliberately load are our bridge
            // and the pinned Harmony package. XNA resolves from the installed GAC.
            if (name == "0Harmony" || name == "TerrariaAgent.Bridge")
                return Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name + ".dll"));
            return null;
        }

        [DataContract]
        private sealed class PreparationState
        {
            [DataMember] public string Status { get; set; }
            [DataMember] public bool BackupStable { get; set; }
            [DataMember] public bool GameCopyVerified { get; set; }
            [DataMember] public bool ProcessVisibilityVerified { get; set; }
        }
    }
}
