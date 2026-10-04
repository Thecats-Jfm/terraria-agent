using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Terraria;
using Terraria.IO;
using Terraria.Social;

namespace TerrariaAgent.Bridge
{
    /// <summary>
    /// Save-only isolation for the inspected vanilla Terraria 1.4.5.8 assembly.
    /// Install before entering the game. The host must set Program.SavePath before
    /// anything can initialize Main's static paths. No content/file-system-wide hooks.
    /// </summary>
    public static class SaveIsolation
    {
        private static string _saveRoot;
        private static string _playerRoot;
        private static string _worldRoot;
        private static Action<string, string> _diagnostic;
        private static bool _installed;
        private static readonly object DiagnosticLock = new object();
        private static readonly HashSet<string> ReportedCloudBlocks = new HashSet<string>();

        public static void Install(Harmony harmony, string saveRoot, Action<string, string> diagnostic)
        {
            if (harmony == null) throw new ArgumentNullException(nameof(harmony));
            if (_installed) throw new InvalidOperationException("Save isolation is already installed.");
            if (!new Version(1, 4, 5, 8).Equals(typeof(Main).Assembly.GetName().Version))
                throw new NotSupportedException("Save isolation requires the inspected Terraria 1.4.5.8 assembly.");
            _diagnostic = diagnostic;
            _saveRoot = NormalizeAbsolute(saveRoot);
            _playerRoot = NormalizeAbsolute(Path.Combine(_saveRoot, "Players"));
            _worldRoot = NormalizeAbsolute(Path.Combine(_saveRoot, "Worlds"));
            AssertIsolation();

            Type cloudType = typeof(Terraria.Social.Steam.CloudSocialModule);
            Patch(harmony, cloudType, "GetFiles", Type.EmptyTypes, typeof(IEnumerable<string>), false, "EmptyCloudFilesPrefix");
            Patch(harmony, cloudType, "HasFile", new[] { typeof(string) }, typeof(bool), false, "FalseCloudResultPrefix");
            Patch(harmony, cloudType, "Write", new[] { typeof(string), typeof(byte[]), typeof(int) }, typeof(bool), false, "FalseCloudResultPrefix");
            Patch(harmony, cloudType, "Delete", new[] { typeof(string) }, typeof(bool), false, "FalseCloudResultPrefix");
            Patch(harmony, cloudType, "Forget", new[] { typeof(string) }, typeof(bool), false, "FalseCloudResultPrefix");
            Patch(harmony, cloudType, "GetFileSize", new[] { typeof(string) }, typeof(int), false, "ZeroCloudSizePrefix");
            Patch(harmony, cloudType, "Read", new[] { typeof(string), typeof(byte[]), typeof(int) }, typeof(void), false, "RejectCloudReadPrefix");
            Patch(harmony, cloudType, "OpenRead", new[] { typeof(string) }, typeof(Stream), false, "RejectCloudReadPrefix");
            Patch(harmony, typeof(SocialAPI), "Initialize", new[] { typeof(SocialMode?) }, typeof(void), true, null, "CloudInitializedPostfix");
            Patch(harmony, typeof(Terraria.Social.Base.CloudSocialModule), "Configuration_OnLoad", new[] { typeof(Preferences) }, typeof(void), false, null, "CloudConfigurationPostfix");

            Patch(harmony, typeof(Main), "LoadPlayers", Type.EmptyTypes, typeof(void), true, "SaveRootPrefix", "PlayerListPostfix");
            Patch(harmony, typeof(Main), "LoadWorlds", Type.EmptyTypes, typeof(void), true, "SaveRootPrefix", "WorldListPostfix");
            Patch(harmony, typeof(Player), "LoadPlayer", new[] { typeof(string), typeof(bool) }, typeof(PlayerFileData), true, "PlayerPathPrefix");
            Patch(harmony, typeof(Player), "GetFileData", new[] { typeof(string), typeof(bool) }, typeof(PlayerFileData), true, "PlayerPathPrefix");
            Patch(harmony, typeof(Player), "SavePlayer", new[] { typeof(PlayerFileData), typeof(bool), typeof(bool) }, typeof(void), true, "PlayerSavePrefix");
            Patch(harmony, typeof(Player), "InternalSavePlayerFile", new[] { typeof(PlayerFileData) }, typeof(void), true, "PlayerSavePrefix");
            Patch(harmony, typeof(PlayerFileData), "CreateAndSave", new[] { typeof(Player) }, typeof(PlayerFileData), true, "SaveRootPrefix");
            Patch(harmony, typeof(PlayerFileData), "SetAsActive", Type.EmptyTypes, typeof(void), false, "ActivePlayerPrefix");
            Patch(harmony, typeof(WorldFileData), "SetAsActive", Type.EmptyTypes, typeof(void), false, "ActiveWorldPrefix");
            Patch(harmony, typeof(WorldFile), "GetAllMetadata", new[] { typeof(string), typeof(bool) }, typeof(WorldFileData), true, "WorldPathPrefix");
            Patch(harmony, typeof(WorldFile), "CreateMetadata", new[] { typeof(string), typeof(bool), typeof(int) }, typeof(WorldFileData), true, "WorldCreationPrefix");
            Patch(harmony, typeof(Main), "OnWorldNamed", new[] { typeof(string) }, typeof(void), false, "SaveRootPrefix");
            Patch(harmony, typeof(WorldFile), "LoadWorld", Type.EmptyTypes, typeof(void), true, "WorldLoadSavePrefix");
            Patch(harmony, typeof(WorldFile), "SaveWorld", new[] { typeof(bool), typeof(bool), typeof(bool) }, typeof(void), true, "WorldLoadSavePrefix");
            Patch(harmony, typeof(WorldFile), "_SaveWorld", new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool) }, typeof(void), true, "WorldSavePrefix");
            Patch(harmony, typeof(WorldFile), "InternalSaveWorld", new[] { typeof(bool), typeof(bool), typeof(bool) }, typeof(void), true, "WorldSavePrefix");
            foreach (Type type in new[] { typeof(PlayerFileData), typeof(WorldFileData) })
            {
                Patch(harmony, type, "MoveToCloud", Type.EmptyTypes, typeof(void), false, "SkipCloudMovePrefix");
                Patch(harmony, type, "MoveToLocal", Type.EmptyTypes, typeof(void), false, "SkipCloudMovePrefix");
            }
            ForceLocalDefault();
            _installed = true;
            Report("save_isolation_installed", "Verified signatures and installed save-only guards for Terraria 1.4.5.8.");
        }

        public static void AssertIsolation()
        {
            if (string.IsNullOrEmpty(_saveRoot)) throw new InvalidOperationException("Save isolation is not configured.");
            AssertDirectory(Program.SavePath, _saveRoot, "Program.SavePath");
            AssertDirectory(Main.SavePath, _saveRoot, "Main.SavePath");
            AssertDirectory(Main.PlayerPath, _playerRoot, "Main.PlayerPath");
            AssertDirectory(Main.WorldPath, _worldRoot, "Main.WorldPath");
            RejectReparsePoints(_saveRoot);
            RejectReparsePoints(_playerRoot);
            RejectReparsePoints(_worldRoot);
            ForceLocalDefault();
        }

        private static void Patch(Harmony harmony, Type type, string methodName, Type[] arguments,
            Type returnType, bool isStatic, string prefix, string postfix = null)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            MethodInfo target = type.GetMethod(methodName, flags, null, arguments, null);
            if (target == null || target.ReturnType != returnType || target.IsStatic != isStatic)
                throw new MissingMethodException("Inspected Terraria API signature is missing: " + type.FullName + "." + methodName);
            HarmonyMethod before = prefix == null ? null : new HarmonyMethod(typeof(SaveIsolation).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static));
            HarmonyMethod after = postfix == null ? null : new HarmonyMethod(typeof(SaveIsolation).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static));
            harmony.Patch(target, prefix: before, postfix: after);
        }

        private static void SaveRootPrefix() { AssertIsolation(); }
        private static void CloudInitializedPostfix() { ForceLocalDefault(); }
        private static void CloudConfigurationPostfix(Terraria.Social.Base.CloudSocialModule __instance) { __instance.EnabledByDefault = false; }
        private static void ForceLocalDefault()
        {
            if (SocialAPI.Cloud != null) SocialAPI.Cloud.EnabledByDefault = false;
        }

        private static bool EmptyCloudFilesPrefix(ref IEnumerable<string> __result, MethodBase __originalMethod)
        {
            CloudBlocked(__originalMethod);
            __result = new string[0];
            return false;
        }

        private static bool FalseCloudResultPrefix(ref bool __result, MethodBase __originalMethod)
        {
            CloudBlocked(__originalMethod);
            __result = false;
            return false;
        }

        private static bool ZeroCloudSizePrefix(ref int __result, MethodBase __originalMethod)
        {
            CloudBlocked(__originalMethod);
            __result = 0;
            return false;
        }

        private static bool RejectCloudReadPrefix(MethodBase __originalMethod)
        {
            CloudBlocked(__originalMethod);
            throw new InvalidOperationException("Cloud reads are disabled for the isolated Agent run.");
        }

        private static bool SkipCloudMovePrefix(MethodBase __originalMethod)
        {
            CloudBlocked(__originalMethod);
            return false;
        }

        private static void PlayerPathPrefix(string __0, bool __1)
        {
            AssertIsolation();
            if (__1) Reject("Cloud player load was requested.");
            AssertFilePath(__0, _playerRoot, "player file");
        }

        private static void WorldPathPrefix(string __0, bool __1)
        {
            AssertIsolation();
            if (__1) Reject("Cloud world metadata was requested.");
            AssertFilePath(__0, _worldRoot, "world metadata");
        }

        private static void PlayerSavePrefix(PlayerFileData __0)
        {
            AssertIsolation();
            AssertFileData(__0, _playerRoot, "player save");
        }

        private static void ActivePlayerPrefix(PlayerFileData __instance)
        {
            AssertIsolation();
            AssertFileData(__instance, _playerRoot, "active player");
        }

        private static void ActiveWorldPrefix(WorldFileData __instance)
        {
            AssertIsolation();
            AssertFileData(__instance, _worldRoot, "active world");
        }

        private static void WorldCreationPrefix(bool __1)
        {
            AssertIsolation();
            if (__1) Reject("Cloud world creation was requested.");
        }

        private static void WorldLoadSavePrefix()
        {
            AssertIsolation();
            AssertFileData(Main.ActiveWorldFileData, _worldRoot, "world load/save");
        }

        private static void WorldSavePrefix(bool __0)
        {
            if (__0) Reject("Cloud world save was requested.");
            WorldLoadSavePrefix();
        }

        private static void PlayerListPostfix()
        {
            AssertIsolation();
            foreach (PlayerFileData data in Main.PlayerList) AssertFileData(data, _playerRoot, "player list");
        }

        private static void WorldListPostfix()
        {
            AssertIsolation();
            foreach (WorldFileData data in Main.WorldList) AssertFileData(data, _worldRoot, "world list");
        }

        private static void AssertFileData(FileData data, string root, string label)
        {
            if (data == null) Reject(label + " has no FileData.");
            if (data.IsCloudSave) Reject(label + " is a cloud file.");
            AssertFilePath(data.Path, root, label);
        }

        private static void AssertFilePath(string path, string root, string label)
        {
            string full = NormalizeAbsolute(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Reject(label + " is outside the dedicated save directory.");
            RejectReparsePoints(full);
        }

        private static void AssertDirectory(string actual, string expected, string label)
        {
            if (!string.Equals(NormalizeAbsolute(actual), expected, StringComparison.OrdinalIgnoreCase))
                Reject(label + " does not match the dedicated save directory.");
        }

        private static string NormalizeAbsolute(string path)
        {
            // IsPathRooted alone also accepts drive-relative C:foo and \foo.
            // This Windows host uses an explicit local drive, never either form
            // or a network share / alternate data stream.
            if (string.IsNullOrWhiteSpace(path) || path.Length < 3 ||
                !char.IsLetter(path[0]) || path[1] != Path.VolumeSeparatorChar ||
                (path[2] != Path.DirectorySeparatorChar && path[2] != Path.AltDirectorySeparatorChar) ||
                path.IndexOf(Path.VolumeSeparatorChar, 2) >= 0)
                throw new InvalidOperationException("Save paths must be fully qualified local drive paths.");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        // Metadata only: prevent a save-folder junction or a file symlink from
        // escaping the path checks. This does not read any save contents.
        private static void RejectReparsePoints(string path)
        {
            string current = path;
            while (current != null && current.Length >= _saveRoot.Length)
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        Reject("Reparse points are not allowed in the dedicated save directory.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (string.Equals(current, _saveRoot, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(current);
            }
        }

        private static void CloudBlocked(MethodBase method)
        {
            string name = method.DeclaringType.FullName + "." + method.Name;
            lock (DiagnosticLock)
            {
                if (!ReportedCloudBlocks.Add(name)) return;
            }
            Report("cloud_operation_blocked", name);
        }

        private static void Reject(string reason)
        {
            Report("save_isolation_rejected", reason);
            throw new InvalidOperationException("Save isolation rejected: " + reason);
        }

        private static void Report(string eventName, string detail)
        {
            try { if (_diagnostic != null) _diagnostic(eventName, detail); }
            catch { /* Diagnostics cannot remove a protection or enable I/O. */ }
        }
    }
}
