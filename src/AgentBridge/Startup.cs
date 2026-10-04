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
        private static readonly LeaseGate Gate = new LeaseGate();
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
        private static bool _pendingArmFromWindow;
        private static bool _permitContextUnsafe = true;
        private static WinForms.Form _gameForm;
        private static WindowArmSignal _windowArmSignal;
        private static int _windowInsertHeld;
        private static int _windowBackHeld;
        private static int _windowCancelArm;
        private static int _windowEmergencyEvents;
        private static int _windowManualEvents;
        private static int _windowEventFault;
        private static long _stopFileStamp;
        private static long _lastAppliedLeaseExpiry;
        private static long _lastAppliedSequence;
        private static Harmony _harmony;

        public static void Initialize(string runtimeRoot, string saveRoot, string runDirectory, string runId, string token)
        {
            _runtimeRoot = runtimeRoot;
            _saveRoot = saveRoot;
            _runDirectory = runDirectory;
            _log = new EventLog(runDirectory, runId);
            _harmony = new Harmony("terraria-agent.stage-a");
            SaveIsolation.Install(_harmony, saveRoot, Diagnostic);
            SaveIsolation.AssertIsolation();
            // Preparing DoUpdate before graphics initialization can initialize its
            // BeforeFieldInit dependencies while Main.instance is still null.
            // This verified event runs after CreateDevice/Initialize, before the
            // first DoUpdate. Save guards are already installed above.
            Main.OnEnginePreload += InstallInputHooks;
            _server = new LocalBridgeServer(Gate, token, runId, runDirectory, Diagnostic);
            int port = _server.Start();
            var info = new ConnectionInfo { Port = port, Token = token, RunId = runId, LogDirectory = runDirectory };
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
            Diagnostic("bridge_loaded", "rules; own-state-only; controlHooks=pending_graphics_ready; control=Manual; loopback=" + port);
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
            internal WindowArmSignal(long observedAtMs, LeaseSnapshot lease, long stopFileStamp)
            {
                ObservedAtMs = observedAtMs;
                WorldId = lease.WorldId;
                SessionId = lease.SessionId;
                ControlEpoch = lease.ControlEpoch;
                StopFileStamp = stopFileStamp;
            }
        }

        private static void AttachWindowHotkeys()
        {
            _gameForm = WinForms.Control.FromHandle(Main.instance.Window.Handle) as WinForms.Form;
            if (_gameForm == null) throw new InvalidOperationException("The real XNA game window is not a managed Form.");
            _gameForm.KeyDown += GameWindowKeyDown;
            _gameForm.KeyUp += GameWindowKeyUp;
            Diagnostic("window_hotkeys_installed", "localForm=True;keyDown=True;keyUp=True");
        }

        private static void DetachWindowHotkeys()
        {
            WinForms.Form form = _gameForm;
            _gameForm = null;
            if (form == null) return;
            form.KeyDown -= GameWindowKeyDown;
            form.KeyUp -= GameWindowKeyUp;
        }

        // Event callbacks only touch local immutable signals and the thread-safe
        // protocol gate. They never read/write Terraria objects or consume keys.
        private static void GameWindowKeyDown(object sender, WinForms.KeyEventArgs args)
        {
            try
            {
                if (args.KeyCode == WinForms.Keys.Insert)
                {
                    bool firstDown = Interlocked.Exchange(ref _windowInsertHeld, 1) == 0;
                    if (args.Control && args.Shift)
                    {
                        if (firstDown)
                        {
                            LeaseSnapshot lease = Gate.Snapshot();
                            if (lease.Connected)
                                Interlocked.Exchange(ref _windowArmSignal, new WindowArmSignal(MonotonicClock.NowMs, lease, Interlocked.Read(ref _stopFileStamp)));
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
                    Interlocked.Exchange(ref _windowArmSignal, null);
                    Interlocked.Exchange(ref _windowCancelArm, 1);
                }
                LeaseSnapshot currentLease = Gate.Snapshot();
                if (otherKey || currentLease.State == ControlState.Agent || currentLease.ArmPermitted)
                {
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
            _pendingArmFromWindow = false;
        }

        private static void DrainWindowControlEvents()
        {
            if (Interlocked.Exchange(ref _windowCancelArm, 0) != 0) ClearPendingArm();
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
                HandleHotkeys();
                var lease = Gate.Snapshot();
                if (!OrdinaryPlayer(__0)) return;
                // These are vanilla's freshly copied physical controls, before our
                // injection. This also detects gamepad movement/jump without any
                // background polling or logging of physical key/button values.
                if ((lease.State == ControlState.Agent || lease.ArmPermitted) && (__0.controlLeft || __0.controlRight || __0.controlJump))
                {
                    bool left = __0.controlLeft, right = __0.controlRight, jump = __0.controlJump;
                    ++_takeovers;
                    Gate.ManualTakeover("physical_movement_or_jump");
                    ClearPreviousInjection();
                    __0.controlLeft = left; __0.controlRight = right; __0.controlJump = jump;
                    Diagnostic("manual_takeover", "physical_movement_or_jump;count=" + _takeovers);
                    return;
                }
                if (lease.State != ControlState.Agent) return;
                InputState input = Gate.PollInputs();
                if (Gate.Snapshot().State != ControlState.Agent) return;
                __0.controlLeft = input.Left;
                __0.controlRight = input.Right;
                __0.controlJump = input.Jump;
                _lastInjectedPlayer = __0;
                _injected = true;
                _lastAppliedLeaseExpiry = lease.ExpiresAtMs;
                _lastAppliedSequence = lease.LastSequence;
            }
            catch (Exception error) { FailClosed(error); }
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
                    CanArm = lease.ArmPermitted, LeaseInputs = lease.Inputs,
                    Inputs = new InputState { Left = !menu && player.controlLeft, Right = !menu && player.controlRight,
                        Jump = !menu && player.controlJump }, GameTick = _gameTick, MonotonicMs = now
                };
                Gate.RecordObservation(observation.Sequence, now);
                _server.Publish(observation);
                if (now - _lastLogAt >= 200)
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
                _worldReference = reference;
                _worldId = reference == null ? null : Guid.NewGuid().ToString("N");
                Diagnostic("world_session", _worldId ?? "menu");
            }
            bool dead = !menu && player.dead;
            if (dead && !_previousDead) { ++_deaths; Diagnostic("death", "count=" + _deaths); }
            _previousDead = dead;
            bool unsafeInput = TextInputActive() || Main.gamePaused || Main.mapFullscreen || Main.ingameOptionsWindow ||
                !FocusHelper.IsSelectedApplication || (!menu && !OrdinaryPlayer(player));
            bool contextUnsafe = dead || menu || unsafeInput || _bridgeFaulted;
            bool contextChanged = contextUnsafe != _permitContextUnsafe;
            _permitContextUnsafe = contextUnsafe;
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
                bool eligible = age >= 0 && age <= 500 && sameWorld && sameOwner && sameEpoch && !_bridgeFaulted && OrdinaryPlayer(LocalPlayer());
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
                    _pendingArmFromWindow = true;
                }
            }
            // If the Form saw Insert down, its one-down-until-KeyUp latch owns
            // that gesture. Polling cannot revive expired/rejected autorepeat.
            bool pollDown = arm && !_previousInsert && Volatile.Read(ref _windowInsertHeld) == 0;
            if (stop && !_previousStop && Volatile.Read(ref _windowBackHeld) == 0)
            {
                ClearPendingArm();
                ++_takeovers;
                Gate.EmergencyStop("emergency_hotkey");
                ClearPreviousInjection();
                Diagnostic("manual_takeover", "emergency_hotkey;count=" + _takeovers);
            }
            else if (pollDown && !_pendingArmChord && !_bridgeFaulted && OrdinaryPlayer(LocalPlayer()))
            {
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
                if (!sameWorld || !sameOwner || !sameEpoch || age < 0 || age > 500)
                {
                    Diagnostic("arm_chord_cancelled", "sameWorld=" + sameWorld + ";sameOwner=" + sameOwner + ";sameEpoch=" + sameEpoch + ";expired=" + (age < 0 || age > 500));
                    ClearPendingArm();
                }
            }
            // Grant only after the whole chord is released. Releasing Insert before
            // Ctrl/Shift must not become an immediate accidental manual takeover.
            Keys[] pressed = keys.GetPressedKeys();
            bool released = pressed.Length == 0 && Volatile.Read(ref _windowInsertHeld) == 0 && Volatile.Read(ref _windowBackHeld) == 0;
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
                    Diagnostic("human_arm_permission", (allowed ? "granted_once" : "stale_gesture_or_no_ready_session") + ";windowEvent=" + fromWindow);
                }
            }
            else if (_pendingArmChord)
            {
                foreach (Keys key in pressed)
                    if (key != Keys.LeftControl && key != Keys.RightControl && key != Keys.LeftShift && key != Keys.RightShift && key != Keys.Insert)
                        ClearPendingArm();
            }
            LeaseSnapshot currentLease = Gate.Snapshot();
            if (!_pendingArmChord && !arm && !stop && (currentLease.State == ControlState.Agent || currentLease.ArmPermitted) && pressed.Length != 0)
            {
                ++_takeovers;
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
            ClearPendingArm();
            DrainWindowControlEvents();
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
