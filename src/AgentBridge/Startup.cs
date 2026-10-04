using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;
using TerrariaAgent.Protocol;
using WinForms = System.Windows.Forms;

namespace TerrariaAgent.Bridge
{
    public static class Startup
    {
        private static LeaseGate Gate;
        private static LocalBridgeServer _server;
        private static EventLog _log;
        private static string _runtimeRoot;
        private static string _saveRoot;
        private static string _runDirectory;
        private static string _worldId;
        private static object _worldReference;
        private static Player _lastInjectedPlayer;
        private static bool _injected;
        private static long _gameTick;
        private static long _observationSequence;
        private static long _lastPublishAt = -1000;
        private static long _lastLogAt = -1000;
        private static long _lastPathCheckAt = -1000;
        private static string _lastControlSummary;
        private static long _lastPermitContextAt = -1000;
        private static string _lastPermitContextSummary;
        private static bool _previousInsert;
        private static bool _previousStop;
        private static bool _previousDead;
        private static int _deaths;
        private static int _takeovers;
        private static Timer _watchdog;
        private static bool _bridgeFaulted;
        private static string _connectionPath;
        private static bool _pendingArmChord;
        private static bool _armChordDiagnosticPending;
        private static long _pendingArmAt;
        private static string _pendingArmWorldId;
        private static string _pendingArmSessionId;
        private static long _pendingArmControlEpoch;
        private static long _pendingArmStopFileStamp;
        private static int _pendingArmHumanActivityRevision;
        private static bool _pendingArmFromWindow;
        private static string _lastPendingReleaseSummary;
        private static bool _permitContextUnsafe = true;
        private static WinForms.Form _gameForm;
        private static GameWindowMessageFilter _gameMessageFilter;
        private static WindowArmSignal _windowArmSignal;
        // Kept separately from ordinary pending arms: our own options pause is
        // expected to make RefreshContext clear ordinary input permissions.
        private static WindowArmSignal _windowResumeArmSignal;
        private static WindowArmSignal _resumingArmSignal;
        private static WindowArmSignal _resumePauseRecoverySignal;
        private static int _windowInsertHeld;
        private static int _windowBackHeld;
        private static int _windowHomeHeld;
        private static int _windowEndHeld;
        private static WindowPauseSignal _windowPauseEvents;
        private static string _operatorPauseWorldId;
        private static int _windowCancelArm;
        private static int _windowEmergencyEvents;
        private static int _windowManualEvents;
        private static int _windowEventFault;
        private static long _stopFileStamp;
        private static long _lastAppliedLeaseExpiry;
        private static long _lastAppliedSequence;
        private static Harmony _harmony;
        private static bool _stageBEnabled;
        private static int _physicalMouseX;
        private static int _physicalMouseY;
        private static bool _mouseInjected;
        private static AutoPausePolicy _autoPause;
        private static int _humanActivityRevision;

        public static void Initialize(string runtimeRoot, string saveRoot, string runDirectory, string runId, string token,
            bool allowInitialControllerStart, bool enableStageB)
        {
            _stageBEnabled = enableStageB;
            _autoPause = new AutoPausePolicy(enableStageB);
            Gate = new LeaseGate(null, allowInitialControllerStart, enableStageB);
            _runtimeRoot = runtimeRoot;
            _saveRoot = saveRoot;
            _runDirectory = runDirectory;
            string challenge = Path.GetFileName(saveRoot);
            if (challenge != "main" && challenge != "combat_test") throw new InvalidOperationException("Unknown challenge save profile.");
            _log = new EventLog(runDirectory, runId, challenge);
            _harmony = new Harmony("terraria-agent.stage-a");
            SaveIsolation.Install(_harmony, saveRoot, Diagnostic);
            SaveIsolation.AssertIsolation();
            // Preparing DoUpdate before graphics initialization can initialize its
            // BeforeFieldInit dependencies while Main.instance is still null.
            // This verified event runs after CreateDevice/Initialize, before the
            // first DoUpdate. Save guards are already installed above.
            Main.OnEnginePreload += InstallInputHooks;
            _server = new LocalBridgeServer(Gate, token, runId, runDirectory, Diagnostic, TryInitialOperatorArm);
            int port = _server.Start();
            var info = new ConnectionInfo { Port = port, Token = token, RunId = runId, LogDirectory = runDirectory, Challenge = challenge };
            string ipcDirectory = Path.Combine(runtimeRoot, "ipc");
            Directory.CreateDirectory(ipcDirectory);
            _connectionPath = Path.Combine(ipcDirectory, runId + ".local.json");
            var security = new FileSecurity();
            var user = WindowsIdentity.GetCurrent().User;
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            byte[] connectionBytes = JsonCodec.Serialize(info);
            using (var file = new FileStream(_connectionPath, FileMode.CreateNew, FileSystemRights.Write,
                FileShare.None, 4096, FileOptions.None, security)) file.Write(connectionBytes, 0, connectionBytes.Length);
            _watchdog = new Timer(Watchdog, null, 0, 50);
            Diagnostic("bridge_loaded", "rules; filtered-own-state; controlHooks=pending_graphics_ready; control=Manual; initialOperatorStart=" + allowInitialControllerStart + ";stageB=" + enableStageB + "; loopback=" + port);
            File.WriteAllText(Path.Combine(runDirectory, "save-isolation.txt"),
                "expectedSaveRoot=" + saveRoot + "\r\nactualMainSavePath=" + Main.SavePath +
                "\r\nactualProgramSavePath=" + Terraria.Program.SavePath + "\r\ncloud=blocked-in-this-process\r\n" +
                "This startup check precedes menus. In-game character/world creation still needs validation.\r\n");
        }

        private static void InstallInputHooks()
        {
            Main.OnEnginePreload -= InstallInputHooks;
            try
            {
                try { SaveIsolation.AssertIsolation(); }
                catch (Exception error) { FatalIsolation(error); return; }
                if (Main.instance == null || Main.instance.GraphicsDevice == null)
                    throw new InvalidOperationException("Graphics device is not ready at the verified engine preload hook.");
                var doUpdate = typeof(Main).GetMethod("DoUpdate", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(GameTime).MakeByRefType() }, null);
                var copyInto = typeof(TriggersSet).GetMethod("CopyInto", BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(Player) }, null);
                if (doUpdate == null || copyInto == null) throw new MissingMethodException("Inspected update/input hook signature changed.");
                _harmony.Patch(doUpdate, prefix: Hook("GameUpdatePrefix"), postfix: Hook("GameUpdatePostfix"), finalizer: Hook("GameUpdateFinalizer"));
                _harmony.Patch(copyInto, postfix: Hook("CopyIntoPostfix"));
                AttachWindowHotkeys();
                Diagnostic("control_hooks_installed", "graphics_ready=true; verified signatures; control=Manual");
            }
            catch (Exception error)
            {
                DetachWindowHotkeys();
                FailClosed(error);
            }
        }

        private sealed class WindowArmSignal
        {
            internal readonly long ObservedAtMs;
            internal readonly string WorldId;
            internal readonly string SessionId;
            internal readonly long ControlEpoch;
            internal readonly long StopFileStamp;
            internal readonly int HumanActivityRevision;
            internal readonly bool ResumeStarted;
            internal WindowArmSignal(long observedAtMs, LeaseSnapshot lease, long stopFileStamp, int humanActivityRevision)
            {
                ObservedAtMs = observedAtMs;
                WorldId = lease.WorldId;
                SessionId = lease.SessionId;
                ControlEpoch = lease.ControlEpoch;
                StopFileStamp = stopFileStamp;
                HumanActivityRevision = humanActivityRevision;
            }
            internal WindowArmSignal(WindowArmSignal signal)
            {
                ObservedAtMs = signal.ObservedAtMs;
                WorldId = signal.WorldId;
                SessionId = signal.SessionId;
                ControlEpoch = signal.ControlEpoch;
                StopFileStamp = signal.StopFileStamp;
                HumanActivityRevision = signal.HumanActivityRevision;
                ResumeStarted = true;
            }
        }

        private static void AttachWindowHotkeys()
        {
            _gameForm = WinForms.Control.FromHandle(Main.instance.Window.Handle) as WinForms.Form;
            if (_gameForm == null) throw new InvalidOperationException("The real XNA game window is not a managed Form.");
            // XNA WindowsGameForm.ProcessDialogKey consumes ordinary dialog keys
            // before Form.KeyDown. Observe the real window's queued key messages
            // before WinForms preprocessing instead. Do not consume any message.
            _gameMessageFilter = new GameWindowMessageFilter(_gameForm.Handle);
            WinForms.Application.AddMessageFilter(_gameMessageFilter);
            Diagnostic("window_hotkeys_installed", "localForm=True;messageFilter=True;ownHwndOnly=True;consume=False");
        }

        private sealed class WindowPauseSignal
        {
            internal readonly long ObservedAtMs;
            internal readonly string WorldId;
            internal readonly bool Resume;
            internal WindowPauseSignal(long observedAtMs, string worldId, bool resume)
            { ObservedAtMs = observedAtMs; WorldId = worldId; Resume = resume; }
        }

        private static void DetachWindowHotkeys()
        {
            _gameForm = null;
            GameWindowMessageFilter filter = _gameMessageFilter;
            _gameMessageFilter = null;
            if (filter != null) WinForms.Application.RemoveMessageFilter(filter);
        }

        private sealed class GameWindowMessageFilter : WinForms.IMessageFilter
        {
            private readonly IntPtr _handle;
            internal GameWindowMessageFilter(IntPtr handle) { _handle = handle; }
            public bool PreFilterMessage(ref WinForms.Message message)
            {
                if (message.HWnd != _handle) return false;
                // Own-window physical clicks cancel an unattended pause, too.
                // Internal Main.mouseX/Y aiming never creates Windows messages.
                if (message.Msg == 0x0201 || message.Msg == 0x0204 || message.Msg == 0x0207 ||
                    message.Msg == 0x020B || message.Msg == 0x020A || message.Msg == 0x020E)
                {
                    Interlocked.Increment(ref _humanActivityRevision);
                    Interlocked.Exchange(ref _windowResumeArmSignal, null);
                    LeaseSnapshot before = Gate.Snapshot();
                    Gate.ManualTakeover("physical_window_pointer");
                    if (before.State == ControlState.Agent || before.ArmPermitted)
                        Interlocked.Increment(ref _windowManualEvents);
                    return false;
                }
                bool down = message.Msg == 0x0100 || message.Msg == 0x0104; // WM_KEYDOWN / WM_SYSKEYDOWN
                bool up = message.Msg == 0x0101 || message.Msg == 0x0105; // WM_KEYUP / WM_SYSKEYUP
                if (!down && !up) return false;
                try
                {
                    var args = new WinForms.KeyEventArgs((WinForms.Keys)message.WParam.ToInt32() | WinForms.Control.ModifierKeys);
                    if (down) GameWindowKeyDown(null, args);
                    else GameWindowKeyUp(null, args);
                }
                catch
                {
                    Interlocked.Exchange(ref _windowEventFault, 1);
                    Gate.EmergencyStop("window_hotkey_error");
                }
                return false;
            }
        }

        // Event callbacks only touch local immutable signals and the thread-safe
        // protocol gate. They never read/write Terraria objects or consume keys.
        private static void GameWindowKeyDown(object sender, WinForms.KeyEventArgs args)
        {
            try
            {
                if (args.KeyCode == WinForms.Keys.Home || args.KeyCode == WinForms.Keys.End)
                {
                    bool resume = args.KeyCode == WinForms.Keys.End;
                    bool firstDown = resume ? Interlocked.Exchange(ref _windowEndHeld, 1) == 0 :
                        Interlocked.Exchange(ref _windowHomeHeld, 1) == 0;
                    if (args.Control && args.Shift && !args.Alt)
                    {
                        if (firstDown)
                        {
                            Interlocked.Increment(ref _humanActivityRevision);
                            Interlocked.Exchange(ref _windowResumeArmSignal, null);
                            LeaseSnapshot lease = Gate.Snapshot();
                            Interlocked.Exchange(ref _windowArmSignal, null);
                            Interlocked.Exchange(ref _windowCancelArm, 1);
                            Gate.EmergencyStop(resume ? "operator_resume_hotkey" : "operator_pause_hotkey");
                            Interlocked.Exchange(ref _windowPauseEvents,
                                new WindowPauseSignal(MonotonicClock.NowMs, lease.WorldId, resume));
                        }
                        return; // OS autorepeat cannot pause/resume a second time.
                    }
                }
                if (args.KeyCode == WinForms.Keys.Insert)
                {
                    bool firstDown = Interlocked.Exchange(ref _windowInsertHeld, 1) == 0;
                    if (args.Control && args.Shift && !args.Alt)
                    {
                        if (firstDown)
                        {
                            int revision = Interlocked.Increment(ref _humanActivityRevision);
                            LeaseSnapshot lease = Gate.Snapshot();
                            Interlocked.Exchange(ref _windowArmSignal, null);
                            Interlocked.Exchange(ref _windowCancelArm, 1);
                            if (lease.Connected)
                                Interlocked.Exchange(ref _windowResumeArmSignal, new WindowArmSignal(MonotonicClock.NowMs, lease,
                                    Interlocked.Read(ref _stopFileStamp), revision));
                        }
                        return; // Autorepeat cannot create another permission.
                    }
                }
                if (args.KeyCode == WinForms.Keys.Back)
                {
                    bool firstDown = Interlocked.Exchange(ref _windowBackHeld, 1) == 0;
                    if (args.Control && args.Shift)
                    {
                        if (firstDown)
                        {
                            Interlocked.Increment(ref _humanActivityRevision);
                            Interlocked.Exchange(ref _windowResumeArmSignal, null);
                            Interlocked.Exchange(ref _windowArmSignal, null);
                            Interlocked.Exchange(ref _windowCancelArm, 1);
                            Gate.EmergencyStop("emergency_window_hotkey");
                            Interlocked.Increment(ref _windowEmergencyEvents);
                        }
                        return;
                    }
                }
                bool otherKey = !IsWindowArmKey(args.KeyCode);
                if (otherKey)
                {
                    Interlocked.Exchange(ref _windowResumeArmSignal, null);
                    Interlocked.Exchange(ref _windowArmSignal, null);
                    Interlocked.Exchange(ref _windowCancelArm, 1);
                }
                LeaseSnapshot currentLease = Gate.Snapshot();
                if (otherKey || currentLease.State == ControlState.Agent || currentLease.ArmPermitted)
                {
                    Interlocked.Increment(ref _humanActivityRevision);
                    Gate.ManualTakeover("physical_window_key");
                    if (currentLease.State == ControlState.Agent || currentLease.ArmPermitted)
                        Interlocked.Increment(ref _windowManualEvents);
                }
            }
            catch
            {
                Interlocked.Exchange(ref _windowEventFault, 1);
                Gate.EmergencyStop("window_hotkey_error");
            }
        }

        private static void GameWindowKeyUp(object sender, WinForms.KeyEventArgs args)
        {
            // Only an actual key-up resets these latches. Context cancellation
            // must not turn a still-held key's autorepeat into a fresh key-down.
            if (args.KeyCode == WinForms.Keys.Insert) Interlocked.Exchange(ref _windowInsertHeld, 0);
            if (args.KeyCode == WinForms.Keys.Back) Interlocked.Exchange(ref _windowBackHeld, 0);
            if (args.KeyCode == WinForms.Keys.Home) Interlocked.Exchange(ref _windowHomeHeld, 0);
            if (args.KeyCode == WinForms.Keys.End) Interlocked.Exchange(ref _windowEndHeld, 0);
        }

        private static bool IsWindowArmKey(WinForms.Keys key)
        {
            return key == WinForms.Keys.Insert || key == WinForms.Keys.ControlKey || key == WinForms.Keys.LControlKey ||
                key == WinForms.Keys.RControlKey || key == WinForms.Keys.ShiftKey || key == WinForms.Keys.LShiftKey || key == WinForms.Keys.RShiftKey;
        }

        private static void ClearPendingArm()
        {
            Interlocked.Exchange(ref _windowArmSignal, null);
            _pendingArmChord = false;
            _armChordDiagnosticPending = false;
            _pendingArmAt = 0;
            _pendingArmWorldId = null;
            _pendingArmSessionId = null;
            _pendingArmControlEpoch = 0;
            _pendingArmStopFileStamp = 0;
            _pendingArmHumanActivityRevision = 0;
            _pendingArmFromWindow = false;
            _lastPendingReleaseSummary = null;
        }

        private static void ClearResumeArm()
        {
            Interlocked.Exchange(ref _windowResumeArmSignal, null);
            _resumingArmSignal = null;
            _resumePauseRecoverySignal = null;
        }

        private static void DrainWindowControlEvents()
        {
            if (Interlocked.Exchange(ref _windowCancelArm, 0) != 0)
            {
                if (_pendingArmChord || Volatile.Read(ref _windowArmSignal) != null)
                    Diagnostic("arm_chord_cancelled", "reason=other_window_keyboard_input");
                ClearPendingArm();
            }
            int emergency = Interlocked.Exchange(ref _windowEmergencyEvents, 0);
            int manual = Interlocked.Exchange(ref _windowManualEvents, 0);
            if (emergency != 0 || manual != 0)
            {
                ClearPendingArm();
                _takeovers += emergency + manual;
                Diagnostic("manual_takeover", "windowEmergency=" + (emergency != 0) + ";windowManual=" + (manual != 0) + ";count=" + _takeovers);
            }
            if (Interlocked.Exchange(ref _windowEventFault, 0) != 0)
                FailClosed(new InvalidOperationException("Local game window hotkey callback failed."));
        }

        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(Startup).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void DrainWindowPauseEvents()
        {
            WindowPauseSignal signal = Interlocked.Exchange(ref _windowPauseEvents, null);
            if (signal == null) return;
            ClearPendingArm();
            ClearPreviousInjection();
            long age = MonotonicClock.NowMs - signal.ObservedAtMs;
            Player player = LocalPlayer();
            bool sameWorld = !string.IsNullOrEmpty(signal.WorldId) &&
                string.Equals(signal.WorldId, _worldId, StringComparison.Ordinal) &&
                object.ReferenceEquals(_worldReference, Main.ActiveWorldFileData);
            bool safeWorld = !_bridgeFaulted && Main.netMode == 0 && !Main.gameMenu && player != null &&
                player.active && !player.dead && player.statLife > 0 && !player.ghost && player.spectating < 0 &&
                !player.isOperatingAnotherEntity && !player.isControlledByFilm && !TextInputActive() &&
                FocusHelper.IsSelectedApplication && Main.instance != null && Main.instance.IsActive;
            if (age < 0 || age > 500 || !sameWorld || !safeWorld)
            {
                Diagnostic("operator_pause_ui", "rejected;resume=" + signal.Resume + ";sameWorld=" + sameWorld +
                    ";safeWorld=" + safeWorld + ";expired=" + (age < 0 || age > 500));
                return;
            }
            if (!signal.Resume)
            {
                // Only open from an ordinary gameplay view. Closing our own
                // options must not also close an inventory/map/chest opened
                // earlier by the human operator.
                if (Main.playerInventory || Main.mapFullscreen || player.chest >= 0)
                {
                    Diagnostic("operator_pause_ui", "pause_rejected;priorUi=True;control=stopped_no_arm");
                    return;
                }
                if (Main.ingameOptionsWindow)
                {
                    Diagnostic("operator_pause_ui", "already_options_open;owned=" +
                        string.Equals(_operatorPauseWorldId, _worldId, StringComparison.Ordinal));
                    return;
                }
                // Verified normal single-player options API. Vanilla's own
                // CanPauseGame/DoUpdate sets gamePaused; the bridge never does.
                IngameOptions.Open();
                if (Main.ingameOptionsWindow) _operatorPauseWorldId = _worldId;
                Diagnostic("operator_pause_ui", "normal_options_open;optionsOpen=" + Main.ingameOptionsWindow);
                return;
            }
            if (!Main.ingameOptionsWindow || !string.Equals(_operatorPauseWorldId, _worldId, StringComparison.Ordinal))
            {
                Diagnostic("operator_pause_ui", "resume_rejected;no_owned_options");
                return;
            }
            // Close() normally reopens the inventory, which AutoPause would keep
            // paused. ToggleInv() is vanilla's normal inventory-close path.
            IngameOptions.Close();
            if (!Main.ingameOptionsWindow)
            {
                _operatorPauseWorldId = null;
                if (Main.playerInventory) player.ToggleInv();
            }
            Diagnostic("operator_pause_ui", "normal_options_close;optionsOpen=" + Main.ingameOptionsWindow +
                ";inventoryOpen=" + Main.playerInventory + ";control=stopped_no_arm");
        }

        private static bool ResumeArmMatches(WindowArmSignal signal, LeaseSnapshot lease)
        {
            long age = MonotonicClock.NowMs - signal.ObservedAtMs;
            return age >= 0 && age <= 500 && !_bridgeFaulted && lease.Connected && !lease.ArmPermitted &&
                (lease.State == ControlState.Manual || lease.State == ControlState.LatchedStop) &&
                !string.IsNullOrEmpty(signal.WorldId) && signal.WorldId == _worldId && signal.WorldId == lease.WorldId &&
                signal.SessionId == lease.SessionId && signal.ControlEpoch == lease.ControlEpoch &&
                signal.HumanActivityRevision == Volatile.Read(ref _humanActivityRevision) &&
                signal.StopFileStamp == Interlocked.Read(ref _stopFileStamp) &&
                object.ReferenceEquals(_worldReference, Main.ActiveWorldFileData);
        }

        private static bool ResumeArmWorldSafe(Player player)
        {
            return !_bridgeFaulted && Main.netMode == 0 && !Main.gameMenu && player != null && player.active &&
                !player.dead && player.statLife > 0 && !player.ghost && player.spectating < 0 &&
                !player.isOperatingAnotherEntity && !player.isControlledByFilm && !TextInputActive() &&
                !Main.playerInventory && !Main.mapFullscreen && player.chest < 0 &&
                FocusHelper.IsSelectedApplication && Main.instance != null && Main.instance.IsActive &&
                (!_stageBEnabled || (!PlayerInput.UsingGamepad && !Main.SmartCursorWanted && player.gravDir == 1f));
        }

        private static void DrainResumeArm()
        {
            WindowArmSignal incoming = Interlocked.Exchange(ref _windowResumeArmSignal, null);
            if (incoming != null) _resumingArmSignal = incoming;
            WindowArmSignal signal = _resumingArmSignal;
            if (signal == null) return;
            Player player = LocalPlayer();
            LeaseSnapshot lease = Gate.Snapshot();
            if (!ResumeArmMatches(signal, lease) || !ResumeArmWorldSafe(player))
            {
                _resumingArmSignal = null;
                Diagnostic("operator_resume_arm", "rejected_context_or_stale_gesture;control_remains_stopped=True");
                return;
            }
            KeyboardState keys = Keyboard.GetState();
            Keys[] pressed = keys.GetPressedKeys();
            foreach (Keys key in pressed)
                if (key != Keys.LeftControl && key != Keys.RightControl && key != Keys.LeftShift && key != Keys.RightShift && key != Keys.Insert)
                {
                    _resumingArmSignal = null;
                    Interlocked.Increment(ref _humanActivityRevision);
                    Gate.ManualTakeover("physical_keyboard_input");
                    return;
                }
            if (Main.ingameOptionsWindow)
            {
                if (signal.ResumeStarted || _operatorPauseWorldId != signal.WorldId)
                {
                    _resumingArmSignal = null;
                    Diagnostic("operator_resume_arm", "rejected_no_owned_options;control_remains_stopped=True");
                    return;
                }
                // Keep the player paused until this one complete gesture has
                // been released. Autorepeat cannot refresh its original deadline.
                if (pressed.Length != 0 || Volatile.Read(ref _windowInsertHeld) != 0 ||
                    Volatile.Read(ref _windowBackHeld) != 0 || !Main.gamePaused) return;
                using (FileStream guard = TryAcquireStopFileLock())
                {
                    if (guard == null) { _resumingArmSignal = null; return; }
                    string flag = Path.Combine(_runtimeRoot, "STOP");
                    bool stopChanged = File.Exists(flag) && File.GetLastWriteTimeUtc(flag).Ticks != signal.StopFileStamp;
                    if (stopChanged || !ResumeArmMatches(signal, Gate.Snapshot()) || !ResumeArmWorldSafe(player))
                    {
                        _resumingArmSignal = null;
                        if (stopChanged) Gate.EmergencyStop("stop_file");
                        Diagnostic("operator_resume_arm", "rejected_before_normal_close;control_remains_stopped=True");
                        return;
                    }
                }
                // UI/audio work must not hold STOP.lock: the watchdog uses the
                // same nonblocking lock and would correctly fail closed on busy.
                // Closing a normal menu grants no control; the eventual permit
                // still holds STOP.lock throughout its separate transaction.
                if (!ResumeArmMatches(signal, Gate.Snapshot()) || !ResumeArmWorldSafe(player) ||
                    !Main.ingameOptionsWindow || _operatorPauseWorldId != signal.WorldId)
                { _resumingArmSignal = null; return; }
                ClearPendingArm();
                ClearPreviousInjection();
                IngameOptions.Close();
                if (Main.ingameOptionsWindow)
                {
                    _resumingArmSignal = null;
                    Diagnostic("operator_resume_arm", "normal_close_did_not_close_options;control_remains_stopped=True");
                    return;
                }
                _operatorPauseWorldId = null;
                // Our owned options were opened only from an empty gameplay
                // UI. Close() creates this inventory; close it normally too.
                if (Main.playerInventory) player.ToggleInv();
                _resumingArmSignal = new WindowArmSignal(signal);
                _resumePauseRecoverySignal = _resumingArmSignal;
                Diagnostic("operator_resume_arm", "normal_options_close;waiting_for_safe_context=True;automaticArm=False");
                return; // Let vanilla recalculate gamePaused before permitting.
            }
            if (Main.gamePaused)
            {
                if (!signal.ResumeStarted)
                {
                    _resumingArmSignal = null;
                    Diagnostic("operator_resume_arm", "rejected_unknown_pause;control_remains_stopped=True");
                }
                return;
            }
            // Ordinary unpaused Insert uses the original released/PermitNextArm
            // path, too. No session, gesture timestamp or epoch is renewed here.
            if (!OrdinaryPlayer(player)) { _resumingArmSignal = null; return; }
            _resumingArmSignal = null;
            Interlocked.Exchange(ref _windowArmSignal, signal);
        }

        private static void PauseAfterFailedResumeArm()
        {
            WindowArmSignal signal = _resumePauseRecoverySignal;
            if (signal == null) return;
            LeaseSnapshot lease = Gate.Snapshot();
            Player player = LocalPlayer();
            long age = MonotonicClock.NowMs - signal.ObservedAtMs;
            // This proof comes only from a real, owned options close. Never
            // protect an ordinary unarmed observer by taking over their UI.
            bool sameOwner = signal.SessionId == lease.SessionId && signal.WorldId == lease.WorldId &&
                signal.WorldId == _worldId && object.ReferenceEquals(_worldReference, Main.ActiveWorldFileData);
            if (!sameOwner || signal.HumanActivityRevision != Volatile.Read(ref _humanActivityRevision) ||
                age < 0 || _bridgeFaulted || !ResumeArmWorldSafe(player) || lease.ControlEpoch < signal.ControlEpoch ||
                lease.State == ControlState.Agent || Main.ingameOptionsWindow)
            { _resumePauseRecoverySignal = null; return; }
            // Until the original deadline, wait for the explicit controller arm.
            // A stop/failed grant is visible as a newer epoch without permission.
            bool failed = lease.State == ControlState.LatchedStop && !lease.ArmPermitted &&
                lease.ControlEpoch > signal.ControlEpoch;
            if (!failed && age <= 500) return;
            _resumePauseRecoverySignal = null; // One normal UI attempt; no retry.
            ClearPendingArm();
            Interlocked.Exchange(ref _windowResumeArmSignal, null);
            _resumingArmSignal = null;
            if (lease.ArmPermitted) Gate.EmergencyStop("resume_arm_handoff_timeout");
            ClearPreviousInjection();
            // Vanilla remains responsible for gamePaused. All identity, focus,
            // ordinary-player and empty-UI checks were made on this game thread.
            IngameOptions.Open();
            if (Main.ingameOptionsWindow) _operatorPauseWorldId = _worldId;
            Diagnostic("operator_resume_arm", "normal_options_reopen;optionsOpen=" + Main.ingameOptionsWindow +
                ";reason=" + (failed ? "handoff_failed" : "handoff_timeout") + ";automaticArm=False");
        }

        // Only protocol state and a workspace stop flag are touched by this timer.
        // Player fields are cleared on the next executable game update.
        private static void Watchdog(object unused)
        {
            try
            {
                Gate.PollInputs();
                using (FileStream guard = TryAcquireStopFileLock())
                {
                    if (guard == null) return;
                    string flag = Path.Combine(_runtimeRoot, "STOP");
                    long stamp = File.Exists(flag) ? File.GetLastWriteTimeUtc(flag).Ticks : 0;
                    long previousStamp = Interlocked.Exchange(ref _stopFileStamp, stamp);
                    var lease = Gate.Snapshot();
                    // A standing stop remains enforced without revoking the same
                    // local gesture every 50 ms. A rewritten flag is a new stop.
                    if (stamp != 0 && (stamp != previousStamp || lease.State == ControlState.Agent || lease.ArmPermitted))
                        Gate.EmergencyStop("stop_file");
                }
            }
            catch { Gate.EmergencyStop("watchdog_error"); }
        }

        private static FileStream TryAcquireStopFileLock()
        {
            try
            {
                return new FileStream(Path.Combine(_runtimeRoot, "STOP.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Do not wait on the game thread or grant control while a writer
                // owns the stop file coordination lock.
                Gate.EmergencyStop("stop_file_lock_busy");
                return null;
            }
        }

        // Transport thread: only file coordination and the thread-safe gate are
        // used. Hold the same lock as STOP writers through the one-shot grant.
        private static bool TryInitialOperatorArm(AgentRequest request, out string reason)
        {
            using (FileStream guard = TryAcquireStopFileLock())
            {
                if (guard == null) { reason = "stop_file_lock_busy"; return false; }
                if (File.Exists(Path.Combine(_runtimeRoot, "STOP")))
                {
                    Gate.EmergencyStop("stop_file");
                    reason = "stop_file";
                    return false;
                }
                return Gate.ExplicitOperatorArm(request, out reason);
            }
        }

        private static void GameUpdatePrefix()
        {
            try
            {
                ++_gameTick;
                if (MonotonicClock.NowMs - _lastPathCheckAt >= 1000)
                {
                    _lastPathCheckAt = MonotonicClock.NowMs;
                    try { SaveIsolation.AssertIsolation(); }
                    catch (Exception error) { FatalIsolation(error); return; }
                }
                RefreshContext();
                // Clears last tick's injection BEFORE vanilla rebuilds physical
                // controls. Human controls will then be copied normally by the game.
                ClearPreviousInjection();
                PauseAfterControllerStop();
                DrainWindowPauseEvents();
                DrainResumeArm();
                PauseAfterFailedResumeArm();
            }
            catch (Exception error) { FailClosed(error); }
        }

        private static void CopyIntoPostfix(Player __0)
        {
            try
            {
                if (_bridgeFaulted) return;
                if (__0 == null || Main.player == null || Main.myPlayer < 0 || Main.myPlayer >= Main.player.Length ||
                    !object.ReferenceEquals(__0, Main.player[Main.myPlayer])) return;
                // CopyInto also occurs in dead/chat/menu paths. Recheck immediately
                // here because WritingText is reset during PlayerInput.UpdateInput.
                RefreshContext();
                var lease = Gate.Snapshot();
                // These are vanilla's freshly copied physical controls, before our
                // injection. This also detects gamepad movement/jump without any
                // background polling or logging of physical key/button values.
                if (__0.controlLeft || __0.controlRight || __0.controlJump || __0.controlUp || __0.controlUseItem || __0.controlUseTile)
                {
                    bool left = __0.controlLeft, right = __0.controlRight, jump = __0.controlJump, up = __0.controlUp;
                    bool useItem = __0.controlUseItem, useTile = __0.controlUseTile;
                    Interlocked.Increment(ref _humanActivityRevision);
                    ClearResumeArm();
                    ClearPendingArm();
                    string takeoverReason = _stageBEnabled ? "physical_gameplay_input" : "physical_movement_or_jump";
                    Gate.ManualTakeover(takeoverReason);
                    ClearPreviousInjection();
                    __0.controlLeft = left; __0.controlRight = right; __0.controlJump = jump;
                    __0.controlUp = up;
                    __0.controlUseItem = useItem; __0.controlUseTile = useTile;
                    if (lease.State == ControlState.Agent || lease.ArmPermitted)
                    {
                        ++_takeovers;
                        Diagnostic("manual_takeover", takeoverReason + ";count=" + _takeovers);
                    }
                    return;
                }
                HandleHotkeys();
                lease = Gate.Snapshot();
                if (!OrdinaryPlayer(__0)) return;
                if (lease.State != ControlState.Agent) return;
                // A single atomic snapshot binds input, sequence and lease expiry.
                // Mixing PollInputs with an older snapshot could duplicate crafts.
                InputState input = lease.Inputs;
                if (input.Up)
                {
                    // All reads run on the game thread. A transport observation
                    // or an older controller proof cannot authorize this update.
                    InputState resolved;
                    if (!_stageBEnabled || !NormalPlatformUpPlayer(__0) ||
                        !PlatformStepGuard.TryResolve(input, VisibleEnvironment.Capture(__0), out resolved))
                    {
                        Gate.EmergencyStop("platform_up_route_not_legally_verified");
                        ClearPreviousInjection();
                        return;
                    }
                    // Fresh normal ground after ascent needs only Left/Right.
                    // Keep the original lease/epoch; never extend or rearm it.
                    input = resolved;
                }
                var currentLease = Gate.Snapshot();
                if (currentLease.State != ControlState.Agent || currentLease.LastSequence != lease.LastSequence ||
                    currentLease.ControlEpoch != lease.ControlEpoch) return;
                __0.controlLeft = input.Left;
                __0.controlRight = input.Right;
                __0.controlJump = input.Jump;
                __0.controlUp = input.Up;
                _lastInjectedPlayer = __0;
                _injected = true;
                _lastAppliedLeaseExpiry = lease.ExpiresAtMs;
                _lastAppliedSequence = lease.LastSequence;
                if (_stageBEnabled)
                {
                    if (!_mouseInjected) { _physicalMouseX = Main.mouseX; _physicalMouseY = Main.mouseY; }
                    __0.controlUseItem = false;
                    if (input.CraftWorkBench || input.CraftRecipe != null)
                    {
                        using (FileStream guard = TryAcquireStopFileLock())
                        {
                            if (guard == null) { ClearPreviousInjection(); return; }
                            if (File.Exists(Path.Combine(_runtimeRoot, "STOP")))
                            { Gate.EmergencyStop("stop_file"); ClearPreviousInjection(); return; }
                            string executionReason;
                            Gate.TryExecuteCurrent(lease.SessionId, lease.WorldId, lease.LastSequence,
                                () => GameplayActions.Apply(__0, input, lease, Gate, Diagnostic,
                                    turn => TryCommitNormalTurn(lease, turn)), out executionReason);
                        }
                    }
                    else GameplayActions.Apply(__0, input, lease, Gate, Diagnostic,
                        turn => TryCommitNormalTurn(lease, turn));
                    _mouseInjected = input.UseItem;
                    if (Gate.Snapshot().State != ControlState.Agent) ClearPreviousInjection();
                }
            }
            catch (GameplayActionFailure error)
            {
                Gate.EmergencyStop(error.Message);
                ClearPreviousInjection();
                Diagnostic("gameplay_failure", error.Message);
            }
            catch (Exception error) { FailClosed(error); }
        }

        private static bool TryCommitNormalTurn(LeaseSnapshot lease, Action turn)
        {
            // Capture/aim filtering runs outside this short commit lock. A
            // concurrent STOP or expired/revoked action cannot commit a turn.
            using (FileStream guard = TryAcquireStopFileLock())
            {
                if (guard == null) { Gate.EmergencyStop("stop_file_lock_busy"); return false; }
                if (File.Exists(Path.Combine(_runtimeRoot, "STOP")))
                { Gate.EmergencyStop("stop_file"); return false; }
                string reason;
                return Gate.TryExecuteCurrent(lease.SessionId, lease.WorldId, lease.LastSequence, turn, out reason);
            }
        }

        private static void PauseAfterControllerStop()
        {
            Player player = LocalPlayer();
            LeaseSnapshot before = Gate.Snapshot();
            var context = new AutoPauseContext
            {
                WorldId = _worldId, SinglePlayer = Main.netMode == 0,
                Menu = Main.gameMenu || player == null || !player.active,
                Dead = player != null && player.dead,
                PlayerAlive = player != null && player.active && !player.dead && player.statLife > 0,
                Health = player != null && player.active ? player.statLife : 0,
                OrdinaryPlayer = OrdinaryPlayer(player), TextInput = TextInputActive(), Faulted = _bridgeFaulted,
                Focused = FocusHelper.IsSelectedApplication && Main.instance != null && Main.instance.IsActive,
                OtherUiOpen = Main.playerInventory || Main.mapFullscreen || (player != null && player.chest >= 0),
                GamePaused = Main.gamePaused, OptionsOpen = Main.ingameOptionsWindow,
                HumanActivityRevision = Volatile.Read(ref _humanActivityRevision)
            };
            string reason;
            if (_autoPause == null || !_autoPause.ShouldPause(before, context, MonotonicClock.NowMs, out reason)) return;
            LeaseSnapshot current = Gate.Snapshot();
            bool same = current.State == ControlState.LatchedStop && !current.ArmPermitted &&
                current.ControlEpoch == before.ControlEpoch &&
                current.SessionId == before.SessionId && current.WorldId == before.WorldId &&
                object.ReferenceEquals(_worldReference, Main.ActiveWorldFileData) &&
                context.HumanActivityRevision == Volatile.Read(ref _humanActivityRevision);
            if (!same || !OrdinaryPlayer(player) || Main.instance == null || !Main.instance.IsActive ||
                player.statLife <= 0 || Main.playerInventory || player.chest >= 0)
            { Diagnostic("automatic_pause_ui", "rejected_after_policy;reason=" + reason); return; }
            ClearPendingArm();
            ClearPreviousInjection();
            IngameOptions.Open();
            if (Main.ingameOptionsWindow) _operatorPauseWorldId = _worldId;
            Diagnostic("automatic_pause_ui", "normal_options_open;optionsOpen=" + Main.ingameOptionsWindow +
                ";reason=" + reason + ";control_remains=" + current.State + ";stopReason=" + current.Reason);
        }

        private static void GameUpdatePostfix()
        {
            try
            {
                RefreshContext();
                HandleHotkeys();
                var lease = Gate.Snapshot();
                if (lease.State != ControlState.Agent) ClearPreviousInjection();
                string summary = lease.State + ":" + lease.Reason;
                if (summary != _lastControlSummary)
                {
                    _lastControlSummary = summary;
                    Diagnostic("control_state", summary + ";seq=" + lease.LastSequence + ";expires=" + lease.ExpiresAtMs);
                }
                long now = MonotonicClock.NowMs;
                if (now - _lastPublishAt < 50 || _server == null) return;
                _lastPublishAt = now;
                Player player = LocalPlayer();
                bool menu = Main.gameMenu || player == null || !player.active;
                // Explicit allowlist. Never serialize the player, Main.tile, world
                // file, any other entity or an arbitrary reflected value.
                var observation = new OwnObservation
                {
                    Sequence = ++_observationSequence, WorldId = menu ? null : _worldId,
                    X = menu ? 0 : player.position.X, Y = menu ? 0 : player.position.Y,
                    VelocityX = menu ? 0 : player.velocity.X, VelocityY = menu ? 0 : player.velocity.Y,
                    Health = menu ? 0 : player.statLife, MaxHealth = menu ? 0 : player.statLifeMax2,
                    Dead = !menu && player.dead, Menu = menu, TextInput = TextInputActive(),
                    ControlState = lease.State.ToString(), Reason = lease.Reason,
                    CanArm = lease.ArmPermitted, CanOperatorArm = lease.CanOperatorArm, LeaseInputs = lease.Inputs,
                    Inputs = new InputState { Left = !menu && player.controlLeft, Right = !menu && player.controlRight,
                        Jump = !menu && player.controlJump, Up = !menu && player.controlUp, UseItem = !menu && player.controlUseItem },
                    GameTick = _gameTick, MonotonicMs = now,
                    GamePaused = Main.gamePaused, OptionsOpen = Main.ingameOptionsWindow,
                    Facing = menu ? 0 : player.direction, ItemAnimation = menu ? 0 : player.itemAnimation
                };
                if (_stageBEnabled && !menu && !player.dead)
                {
                    observation.Gameplay = VisibleEnvironment.Capture(player);
                    observation.Gameplay.CanCraftRecipes = GameplayActions.CanCraftRecipes(player);
                    observation.Gameplay.CanCraftWorkBench = Array.IndexOf(observation.Gameplay.CanCraftRecipes, GameplayRecipeIds.WorkBench) >= 0;
                }
                Gate.RecordObservation(observation.Sequence, now);
                _server.Publish(observation);
                // Controllers retain each skill's action/result samples. Keep
                // overnight idle/resource snapshots bounded; critical events
                // (death, input release, craft and control changes) are immediate.
                long logPeriod = menu ? 5000 : observation.GamePaused || _stageBEnabled ? 1000 : 200;
                if (now - _lastLogAt >= logPeriod)
                {
                    _lastLogAt = now;
                    if (!_log.Record("observation", "own_state", observation)) Gate.EmergencyStop("log_backpressure_or_budget");
                }
            }
            catch (Exception error) { FailClosed(error); }
        }

        private static Exception GameUpdateFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                if (__exception.Message.StartsWith("Save isolation rejected:", StringComparison.Ordinal)) FatalIsolation(__exception);
                else FailClosed(__exception);
            }
            return __exception;
        }

        private static void RefreshContext()
        {
            DrainWindowControlEvents();
            Player player = LocalPlayer();
            bool menu = Main.gameMenu || player == null || !player.active || Main.netMode != 0;
            object reference = menu ? null : Main.ActiveWorldFileData;
            bool worldChanged = !object.ReferenceEquals(reference, _worldReference);
            if (worldChanged)
            {
                ClearResumeArm();
                Interlocked.Exchange(ref _windowPauseEvents, null);
                _operatorPauseWorldId = null;
                if (_stageBEnabled) VisibleEnvironment.ClearHistory();
                _worldReference = reference;
                _worldId = reference == null ? null : Guid.NewGuid().ToString("N");
                Diagnostic("world_session", _worldId ?? "menu");
            }
            bool dead = !menu && player.dead;
            if (dead || menu || _bridgeFaulted)
            {
                ClearResumeArm();
                Interlocked.Exchange(ref _windowPauseEvents, null);
                _operatorPauseWorldId = null;
            }
            // A paused own-options view is the only expected unsafe context for
            // the independent resume queue. Genuine focus/input/UI transitions
            // revoke it even when options already make Gate's context unsafe.
            if (!menu && (!FocusHelper.IsSelectedApplication || Main.instance == null || !Main.instance.IsActive ||
                TextInputActive() || Main.playerInventory || Main.mapFullscreen || player.chest >= 0 ||
                player.ghost || player.spectating >= 0 || player.isOperatingAnotherEntity || player.isControlledByFilm ||
                (_stageBEnabled && (PlayerInput.UsingGamepad || Main.SmartCursorWanted || player.gravDir != 1f))))
                ClearResumeArm();
            if (_operatorPauseWorldId != null && !Main.ingameOptionsWindow) _operatorPauseWorldId = null;
            if (dead && !_previousDead) { ++_deaths; Diagnostic("death", "count=" + _deaths); }
            _previousDead = dead;
            bool unsafeInput = TextInputActive() || Main.gamePaused || Main.mapFullscreen || Main.ingameOptionsWindow ||
                !FocusHelper.IsSelectedApplication || (!menu && !OrdinaryPlayer(player)) ||
                (_stageBEnabled && (PlayerInput.UsingGamepad || Main.SmartCursorWanted || (!menu && player.gravDir != 1f)));
            bool contextUnsafe = dead || menu || unsafeInput || _bridgeFaulted;
            bool contextChanged = contextUnsafe != _permitContextUnsafe;
            _permitContextUnsafe = contextUnsafe;
            if (contextChanged && contextUnsafe && !menu && !dead)
                Diagnostic("unsafe_context", "textInput=" + TextInputActive() +
                    ";gamePaused=" + Main.gamePaused + ";map=" + Main.mapFullscreen +
                    ";options=" + Main.ingameOptionsWindow + ";focus=" + FocusHelper.IsSelectedApplication +
                    ";ordinaryPlayer=" + OrdinaryPlayer(player) + ";gamepad=" + PlayerInput.UsingGamepad +
                    ";smartCursor=" + Main.SmartCursorWanted + ";gravity=" + (player == null ? 0 : player.gravDir) +
                    ";bridgeFaulted=" + _bridgeFaulted);
            if (worldChanged || contextChanged || contextUnsafe)
            {
                if (_pendingArmChord)
                    Diagnostic("arm_chord_cancelled", "worldChanged=" + worldChanged + ";dead=" + dead + ";menu=" + menu +
                        ";unsafeInput=" + unsafeInput + ";bridgeFaulted=" + _bridgeFaulted);
                ClearPendingArm();
            }
            Gate.UpdateContext(menu ? null : _worldId, dead, menu, unsafeInput);
            RecordPermitContext(player, menu);
        }

        // Game-thread diagnostics only. Sample at most once a second and record
        // only changed booleans; never log a key list, key text or player object.
        private static void RecordPermitContext(Player player, bool menu)
        {
            long now = MonotonicClock.NowMs;
            if (now - _lastPermitContextAt < 1000) return;
            _lastPermitContextAt = now;
            string summary = "focus=" + FocusHelper.IsSelectedApplication +
                ";xnaActive=" + (Main.instance != null && Main.instance.IsActive) +
                ";gamePaused=" + Main.gamePaused + ";textInput=" + TextInputActive() +
                ";map=" + Main.mapFullscreen + ";options=" + Main.ingameOptionsWindow +
                ";playerPresent=" + (player != null) + ";active=" + (player != null && player.active) +
                ";dead=" + (player != null && player.dead) + ";ghost=" + (player != null && player.ghost) +
                ";spectatingNegative=" + (player != null && player.spectating < 0) +
                ";opEntity=" + (player != null && player.isOperatingAnotherEntity) +
                ";film=" + (player != null && player.isControlledByFilm) +
                ";menu=" + menu + ";singlePlayer=" + (Main.netMode == 0) +
                ";bridgeFaulted=" + _bridgeFaulted + ";ordinaryPlayer=" + OrdinaryPlayer(player);
            if (summary == _lastPermitContextSummary) return;
            _lastPermitContextSummary = summary;
            Diagnostic("permit_context", summary);
        }

        private static Player LocalPlayer()
        {
            return Main.player != null && Main.myPlayer >= 0 && Main.myPlayer < Main.player.Length ? Main.player[Main.myPlayer] : null;
        }

        private static bool OrdinaryPlayer(Player player)
        {
            return player != null && player.active && !player.dead && !player.ghost && player.spectating < 0 &&
                !player.isOperatingAnotherEntity && !player.isControlledByFilm && Main.netMode == 0 && !Main.gameMenu &&
                !TextInputActive() && !Main.gamePaused && !Main.mapFullscreen && !Main.ingameOptionsWindow && FocusHelper.IsSelectedApplication;
        }

        private static bool TextInputActive()
        {
            return PlayerInput.WritingText || Main.drawingPlayerChat || Main.editSign || Main.editChest || Main.blockInput ||
                Main.CurrentInputTextTakerOverride != null;
        }

        private static bool NormalPlatformUpPlayer(Player player)
        {
            // Up also operates mounts, ropes and gravity equipment in vanilla.
            // This narrow route admits only ordinary unconfused ground walking.
            return OrdinaryPlayer(player) && player.gravDir == 1f && !player.gravControl && !player.gravControl2 &&
                !player.confused && !player.shimmering && !player.pulley && player.mount != null && !player.mount.Active &&
                player.grapCount == 0 && player.grappling != null && player.grappling.Length > 0 &&
                player.grappling[0] == -1 && !player.IsRidingTracks;
        }

        private static void HandleHotkeys()
        {
            if (!FocusHelper.IsSelectedApplication) return;
            KeyboardState keys = Keyboard.GetState();
            bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
            bool shift = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift);
            bool insert = keys.IsKeyDown(Keys.Insert);
            bool arm = control && shift && insert;
            bool stop = control && shift && keys.IsKeyDown(Keys.Back);
            WindowArmSignal signal = Interlocked.Exchange(ref _windowArmSignal, null);
            if (signal != null)
            {
                long age = MonotonicClock.NowMs - signal.ObservedAtMs;
                LeaseSnapshot lease = Gate.Snapshot();
                bool sameWorld = !string.IsNullOrEmpty(signal.WorldId) && string.Equals(signal.WorldId, lease.WorldId, StringComparison.Ordinal);
                bool sameOwner = lease.Connected && string.Equals(signal.SessionId, lease.SessionId, StringComparison.Ordinal);
                bool sameEpoch = signal.ControlEpoch == lease.ControlEpoch;
                bool sameHumanRevision = signal.HumanActivityRevision == Volatile.Read(ref _humanActivityRevision);
                bool eligible = age >= 0 && age <= 500 && sameWorld && sameOwner && sameEpoch && sameHumanRevision && !_bridgeFaulted && OrdinaryPlayer(LocalPlayer());
                Diagnostic("arm_chord_seen", "armChordSeen=True;windowEvent=True;eligible=" + eligible + ";sameWorld=" + sameWorld + ";sameOwner=" + sameOwner + ";sameEpoch=" + sameEpoch);
                if (eligible)
                {
                    _pendingArmChord = true;
                    _armChordDiagnosticPending = true;
                    _pendingArmAt = signal.ObservedAtMs;
                    _pendingArmWorldId = signal.WorldId;
                    _pendingArmSessionId = signal.SessionId;
                    _pendingArmControlEpoch = signal.ControlEpoch;
                    _pendingArmStopFileStamp = signal.StopFileStamp;
                    _pendingArmHumanActivityRevision = signal.HumanActivityRevision;
                    _pendingArmFromWindow = true;
                }
            }
            // If the Form saw Insert down, its one-down-until-KeyUp latch owns
            // that gesture. Polling cannot revive expired/rejected autorepeat.
            bool pollDown = arm && !_previousInsert && Volatile.Read(ref _windowInsertHeld) == 0;
            if (stop && !_previousStop && Volatile.Read(ref _windowBackHeld) == 0)
            {
                Interlocked.Increment(ref _humanActivityRevision);
                ClearResumeArm();
                ClearPendingArm();
                ++_takeovers;
                Gate.EmergencyStop("emergency_hotkey");
                ClearPreviousInjection();
                Diagnostic("manual_takeover", "emergency_hotkey;count=" + _takeovers);
            }
            else if (pollDown && !_pendingArmChord && !_bridgeFaulted && OrdinaryPlayer(LocalPlayer()))
            {
                Interlocked.Increment(ref _humanActivityRevision);
                LeaseSnapshot lease = Gate.Snapshot();
                if (lease.Connected)
                {
                    _pendingArmChord = true;
                    _armChordDiagnosticPending = true;
                    _pendingArmAt = MonotonicClock.NowMs;
                    _pendingArmWorldId = lease.WorldId;
                    _pendingArmSessionId = lease.SessionId;
                    _pendingArmControlEpoch = lease.ControlEpoch;
                    _pendingArmStopFileStamp = Interlocked.Read(ref _stopFileStamp);
                    _pendingArmHumanActivityRevision = Volatile.Read(ref _humanActivityRevision);
                    _pendingArmFromWindow = false;
                    Diagnostic("arm_chord_seen", "armChordSeen=True;windowEvent=False;eligible=True");
                }
            }
            if (_pendingArmChord)
            {
                long age = MonotonicClock.NowMs - _pendingArmAt;
                LeaseSnapshot lease = Gate.Snapshot();
                bool sameWorld = string.Equals(_pendingArmWorldId, lease.WorldId, StringComparison.Ordinal);
                bool sameOwner = lease.Connected && string.Equals(_pendingArmSessionId, lease.SessionId, StringComparison.Ordinal);
                bool sameEpoch = _pendingArmControlEpoch == lease.ControlEpoch;
                bool sameHumanRevision = _pendingArmHumanActivityRevision == Volatile.Read(ref _humanActivityRevision);
                if (!sameWorld || !sameOwner || !sameEpoch || !sameHumanRevision || age < 0 || age > 500)
                {
                    Diagnostic("arm_chord_cancelled", "sameWorld=" + sameWorld + ";sameOwner=" + sameOwner + ";sameEpoch=" + sameEpoch + ";expired=" + (age < 0 || age > 500));
                    ClearPendingArm();
                }
            }
            // Grant only after the whole chord is released. Releasing Insert before
            // Ctrl/Shift must not become an immediate accidental manual takeover.
            Keys[] pressed = keys.GetPressedKeys();
            bool released = pressed.Length == 0 && Volatile.Read(ref _windowInsertHeld) == 0 && Volatile.Read(ref _windowBackHeld) == 0;
            if (_pendingArmChord && !released)
            {
                // Release diagnostics contain only counts/booleans, never key
                // identities, typed text, global input or other-window events.
                string releaseSummary = "pressedCount=" + pressed.Length + ";insertMessageHeld=" + (Volatile.Read(ref _windowInsertHeld) != 0) +
                    ";backMessageHeld=" + (Volatile.Read(ref _windowBackHeld) != 0);
                if (releaseSummary != _lastPendingReleaseSummary)
                {
                    _lastPendingReleaseSummary = releaseSummary;
                    Diagnostic("arm_chord_waiting_release", releaseSummary);
                }
            }
            if (_armChordDiagnosticPending && released)
            {
                _armChordDiagnosticPending = false;
                Diagnostic("arm_chord_released", "armChordReleased=True");
            }
            if (_pendingArmChord && released)
            {
                bool fromWindow = _pendingArmFromWindow;
                string sessionId = _pendingArmSessionId, worldId = _pendingArmWorldId;
                long epoch = _pendingArmControlEpoch, observedAtMs = _pendingArmAt, stopStamp = _pendingArmStopFileStamp;
                ClearPendingArm();
                if (!_bridgeFaulted && OrdinaryPlayer(LocalPlayer()))
                {
                    bool allowed = false;
                    using (FileStream guard = TryAcquireStopFileLock())
                    {
                        if (guard != null)
                        {
                            string flag = Path.Combine(_runtimeRoot, "STOP");
                            bool flagExists = File.Exists(flag);
                            if (flagExists && File.GetLastWriteTimeUtc(flag).Ticks != stopStamp)
                                Gate.EmergencyStop("stop_file");
                            else
                            {
                                allowed = Gate.PermitNextArm(sessionId, worldId, epoch, observedAtMs);
                                // Only a validated, live gesture can clear the
                                // already-observed flag. Stop-Agent uses this lock
                                // too; a new writer after release leaves a new STOP.
                                if (allowed && flagExists) File.Delete(flag);
                            }
                        }
                    }
                    if (allowed && _autoPause != null)
                    {
                        LeaseSnapshot permitted = Gate.Snapshot();
                        if (permitted.ArmPermitted && (permitted.State == ControlState.LatchedStop || permitted.State == ControlState.Manual) &&
                            permitted.ControlEpoch == epoch + 1 && permitted.SessionId == sessionId && permitted.WorldId == worldId)
                            _autoPause.NoteHumanArmAttempt(sessionId, worldId, permitted.ControlEpoch,
                                Volatile.Read(ref _humanActivityRevision), MonotonicClock.NowMs);
                    }
                    Diagnostic("human_arm_permission", (allowed ? "granted_once" : "stale_gesture_or_no_ready_session") + ";windowEvent=" + fromWindow);
                }
            }
            else if (_pendingArmChord)
            {
                foreach (Keys key in pressed)
                    if (key != Keys.LeftControl && key != Keys.RightControl && key != Keys.LeftShift && key != Keys.RightShift && key != Keys.Insert)
                    {
                        Diagnostic("arm_chord_cancelled", "reason=other_polled_keyboard_input;pressedCount=" + pressed.Length);
                        ClearResumeArm();
                        ClearPendingArm();
                        break;
                    }
            }
            LeaseSnapshot currentLease = Gate.Snapshot();
            if (!_pendingArmChord && !arm && !stop && (currentLease.State == ControlState.Agent || currentLease.ArmPermitted) && pressed.Length != 0)
            {
                ++_takeovers;
                Interlocked.Increment(ref _humanActivityRevision);
                ClearResumeArm();
                Gate.ManualTakeover("physical_keyboard_input");
                ClearPreviousInjection();
                Diagnostic("manual_takeover", "physical_keyboard_input;count=" + _takeovers);
            }
            _previousInsert = insert;
            _previousStop = stop;
        }

        private static void ClearPreviousInjection()
        {
            if (!_injected || _lastInjectedPlayer == null) return;
            bool releaseForSafety = Gate.Snapshot().State != ControlState.Agent;
            _lastInjectedPlayer.controlLeft = false;
            _lastInjectedPlayer.controlRight = false;
            _lastInjectedPlayer.controlJump = false;
            _lastInjectedPlayer.controlUp = false;
            if (_stageBEnabled)
            {
                _lastInjectedPlayer.controlUseItem = false;
                if (_mouseInjected)
                {
                    Main.mouseX = _physicalMouseX;
                    Main.mouseY = _physicalMouseY;
                    _mouseInjected = false;
                }
            }
            _injected = false;
            if (releaseForSafety) Diagnostic("actual_input_release", "gameTick=" + _gameTick + ";reason=" + Gate.Snapshot().Reason +
                ";lastAppliedSeq=" + _lastAppliedSequence + ";lastAppliedExpiresAtMs=" + _lastAppliedLeaseExpiry);
        }

        private static void Diagnostic(string eventName, string detail)
        {
            try
            {
                if (_log != null && !_log.Record(eventName, detail)) Gate.EmergencyStop("log_backpressure_or_budget");
            }
            catch { Gate.EmergencyStop("diagnostic_exception"); }
        }

        private static void FailClosed(Exception error)
        {
            Gate.EmergencyStop("bridge_exception");
            ClearResumeArm();
            ClearPendingArm();
            ClearPreviousInjection();
            if (!_bridgeFaulted) Diagnostic("failure", error.GetType().Name + ":" + error.Message);
            _bridgeFaulted = true;
        }

        private static void FatalIsolation(Exception error)
        {
            Gate.EmergencyStop("fatal_save_isolation");
            ClearPreviousInjection();
            Diagnostic("fatal_save_isolation", error.Message);
            try { if (_log != null) _log.Dispose(); } catch { }
            RemoveConnectionCredential();
            // A wrong save root must terminate the game process, not merely stop
            // Agent inputs while vanilla continues loading/saving. This touches no
            // existing save and deliberately avoids a game save-on-exit path.
            Environment.Exit(2);
        }

        public static void Shutdown()
        {
            Main.OnEnginePreload -= InstallInputHooks;
            Gate.EmergencyStop("shutdown");
            DetachWindowHotkeys();
            ClearResumeArm();
            ClearPendingArm();
            DrainWindowControlEvents();
            Interlocked.Exchange(ref _windowPauseEvents, null);
            _operatorPauseWorldId = null;
            if (_watchdog != null) _watchdog.Dispose();
            if (_server != null) _server.Dispose();
            RemoveConnectionCredential();
            Diagnostic("run_end", "deaths=" + _deaths + ";manualTakeovers=" + _takeovers);
            if (_log != null) _log.Dispose();
        }

        private static void RemoveConnectionCredential()
        {
            try { if (_connectionPath != null && File.Exists(_connectionPath)) File.Delete(_connectionPath); }
            catch { /* A stale private token cannot authenticate to a stopped server. */ }
        }
    }
}
