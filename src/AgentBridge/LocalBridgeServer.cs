using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TerrariaAgent.Protocol;

namespace TerrariaAgent.Bridge
{
    public sealed class TransportStatistics
    {
        public long AcceptedConnections { get; set; }
        public long AuthenticationFailures { get; set; }
        public long FramesReceived { get; set; }
        public long RepliesSent { get; set; }
        public long ActionsAccepted { get; set; }
        public long ActionsRejected { get; set; }
        public long ProtocolErrors { get; set; }
    }

    // Transport consumes only already-filtered observations and LeaseGate. It has
    // no Terraria/XNA references and never reads live game objects on background threads.
    public sealed class LocalBridgeServer : IDisposable
    {
        private const int MaxConnections = 8;
        private readonly object _sync = new object();
        private readonly object _diagnosticSync = new object();
        private readonly LeaseGate _gate;
        private readonly byte[] _tokenBytes;
        private readonly string _runId;
        private readonly string _logDirectory;
        private readonly Action<string, string> _diagnostic;
        private readonly HashSet<TcpClient> _clients = new HashSet<TcpClient>();
        private readonly Dictionary<string, long> _diagnosticLast = new Dictionary<string, long>();
        private TcpListener _listener;
        private string _ownerSession;
        private OwnObservation _latest;
        private bool _disposed;
        private int _port;
        private long _accepted;
        private long _authenticationFailures;
        private long _framesReceived;
        private long _repliesSent;
        private long _actionsAccepted;
        private long _actionsRejected;
        private long _protocolErrors;

        public LocalBridgeServer(LeaseGate gate, string token, string runId, string logDirectory,
            Action<string, string> diagnostic = null)
        {
            if (gate == null) throw new ArgumentNullException("gate");
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("Authentication token is required.", "token");
            _gate = gate;
            _tokenBytes = Encoding.UTF8.GetBytes(token);
            _runId = runId ?? "";
            _logDirectory = logDirectory ?? "";
            _diagnostic = diagnostic;
        }

        public int Start()
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException("LocalBridgeServer");
                if (_listener != null) return _port;
                // IPv4 loopback only. Never IPAddress.Any, public interfaces or HTTP.
                _listener = new TcpListener(IPAddress.Parse("127.0.0.1"), 0);
                _listener.Server.ExclusiveAddressUse = true;
                _listener.Start(MaxConnections);
                _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                var listener = _listener;
                var thread = new Thread(() => AcceptLoop(listener))
                { IsBackground = true, Name = "TerrariaAgent.LoopbackAccept" };
                thread.Start();
                Emit("transport_started", "loopback_port=" + _port);
                return _port;
            }
        }

        public void Publish(OwnObservation observation)
        {
            if (observation == null) throw new ArgumentNullException("observation");
            // Defensive copy means a caller later mutating its object cannot expand
            // observation scope or change a snapshot while a reply is serialized.
            Interlocked.Exchange(ref _latest, CopyObservation(observation));
        }

        public TransportStatistics GetStatistics()
        {
            return new TransportStatistics
            {
                AcceptedConnections = Interlocked.Read(ref _accepted),
                AuthenticationFailures = Interlocked.Read(ref _authenticationFailures),
                FramesReceived = Interlocked.Read(ref _framesReceived),
                RepliesSent = Interlocked.Read(ref _repliesSent),
                ActionsAccepted = Interlocked.Read(ref _actionsAccepted),
                ActionsRejected = Interlocked.Read(ref _actionsRejected),
                ProtocolErrors = Interlocked.Read(ref _protocolErrors)
            };
        }

        private void AcceptLoop(TcpListener listener)
        {
            try
            {
                while (true)
                {
                    var client = listener.AcceptTcpClient();
                    bool admitted = false;
                    lock (_sync)
                    {
                        if (!_disposed && _clients.Count < MaxConnections)
                        {
                            _clients.Add(client);
                            admitted = true;
                        }
                    }
                    if (!admitted) { client.Close(); Emit("connection_limit", "pending_limit=8"); continue; }
                    Interlocked.Increment(ref _accepted);
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
            }
            catch (Exception error)
            {
                lock (_sync) { if (_disposed) return; }
                _gate.EmergencyStop("transport_accept_failed");
                Emit("transport_accept_failed", error.GetType().Name);
            }
        }

        private void HandleClient(TcpClient client)
        {
            string sessionId = null;
            string closeReason = "disconnected";
            try
            {
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                if (endpoint == null || !IPAddress.IsLoopback(endpoint.Address))
                    throw new InvalidDataException("Only loopback clients are accepted.");
                client.NoDelay = true;
                client.ReceiveTimeout = Wire.IoTimeoutMs;
                client.SendTimeout = Wire.IoTimeoutMs;
                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = Wire.IoTimeoutMs;
                    stream.WriteTimeout = Wire.IoTimeoutMs;
                    var hello = ReadRequest(stream, out _);
                    if (!string.Equals(hello.Type, "hello", StringComparison.Ordinal) || !TokenEquals(hello.Token))
                    {
                        Interlocked.Increment(ref _authenticationFailures);
                        Send(stream, new AgentReply { Type = "hello", Status = "rejected", Reason = "authentication_failed" });
                        Emit("authentication_failed", "credential_rejected");
                        return;
                    }

                    string proposed = Guid.NewGuid().ToString("N");
                    string reason;
                    bool opened;
                    lock (_sync)
                    {
                        if (_disposed) return;
                        opened = _gate.OpenSession(proposed, out reason);
                        if (opened) { sessionId = proposed; _ownerSession = sessionId; }
                    }
                    if (!opened)
                    {
                        Send(stream, new AgentReply { Type = "hello", Status = "rejected", Reason = reason });
                        Emit("session_rejected", reason);
                        return;
                    }
                    Send(stream, Reply("hello", "ok", "authenticated_manual", sessionId, 0));
                    Emit("session_opened", "authenticated_manual");

                    while (true)
                    {
                        long receivedAtMs;
                        var request = ReadRequest(stream, out receivedAtMs);
                        // Packet identity is bound to this authenticated connection.
                        // Old/mismatched identities are rejected without stopping a replacement owner.
                        if (!string.Equals(request.SessionId, sessionId, StringComparison.Ordinal))
                        {
                            Send(stream, Reply(request.Type, "rejected", "wrong_session", sessionId, request.Sequence));
                            Emit("packet_rejected", "wrong_session");
                            continue;
                        }

                        bool accepted;
                        switch (request.Type)
                        {
                            case "observe":
                                Send(stream, Reply("observe", "ok", "observed", sessionId, request.Sequence));
                                break;
                            case "arm":
                                accepted = _gate.ExplicitArm(request, out reason);
                                Send(stream, Reply("arm", accepted ? "ok" : "rejected", reason, sessionId, request.Sequence));
                                Emit("arm_result", reason);
                                break;
                            case "action":
                                accepted = _gate.TryApplyAction(request, receivedAtMs, out reason);
                                if (accepted) Interlocked.Increment(ref _actionsAccepted);
                                else { Interlocked.Increment(ref _actionsRejected); Emit("action_rejected", reason); }
                                Send(stream, Reply("action", accepted ? "ok" : "rejected", reason, sessionId, request.Sequence));
                                break;
                            case "stop":
                                _gate.Stop(sessionId);
                                Send(stream, Reply("stop", "ok", "explicit_stop", sessionId, request.Sequence));
                                Emit("explicit_stop", "inputs_released");
                                break;
                            default:
                                Send(stream, Reply("error", "rejected", "unknown_request_type", sessionId, request.Sequence));
                                throw new InvalidDataException("Unknown request type.");
                        }
                    }
                }
            }
            catch (EndOfStreamException) { closeReason = "disconnected"; }
            catch (IOException)
            {
                closeReason = "transport_io_failed_or_timeout";
                Interlocked.Increment(ref _protocolErrors);
            }
            catch (Exception error)
            {
                closeReason = "transport_protocol_error";
                Interlocked.Increment(ref _protocolErrors);
                // Exception messages from JSON can contain untrusted credential bytes.
                Emit("protocol_error", error.GetType().Name);
            }
            finally
            {
                if (sessionId != null)
                {
                    _gate.Disconnect(sessionId, closeReason);
                    Emit("session_closed", closeReason + "; accepted_actions=" + Interlocked.Read(ref _actionsAccepted) +
                        "; rejected_actions=" + Interlocked.Read(ref _actionsRejected));
                }
                lock (_sync)
                {
                    _clients.Remove(client);
                    if (string.Equals(_ownerSession, sessionId, StringComparison.Ordinal)) _ownerSession = null;
                }
                try { client.Close(); } catch { }
            }
        }

        private AgentRequest ReadRequest(Stream stream, out long receivedAtMs)
        {
            byte[] bytes = Wire.ReadFrame(stream);
            receivedAtMs = MonotonicClock.NowMs;
            Interlocked.Increment(ref _framesReceived);
            return JsonCodec.Deserialize<AgentRequest>(bytes);
        }

        private void Send(Stream stream, AgentReply reply)
        {
            Wire.Write(stream, reply);
            Interlocked.Increment(ref _repliesSent);
        }

        private AgentReply Reply(string type, string status, string reason, string sessionId, long sequence)
        {
            var observation = Volatile.Read(ref _latest);
            var lease = _gate.Snapshot();
            if (observation != null && !string.Equals(observation.WorldId, lease.WorldId, StringComparison.Ordinal))
                observation = null;
            if (observation != null)
            {
                observation = CopyObservation(observation);
                observation.ControlState = lease.State.ToString();
                observation.Reason = lease.Reason;
                observation.LeaseInputs = lease.Inputs.Copy();
                observation.CanArm = lease.ArmPermitted;
            }
            return new AgentReply
            {
                Type = type, Status = status, Reason = reason, SessionId = sessionId,
                WorldId = lease.WorldId, Sequence = sequence, Observation = observation
            };
        }

        private bool TokenEquals(string supplied)
        {
            var candidate = Encoding.UTF8.GetBytes(supplied ?? "");
            int diff = candidate.Length ^ _tokenBytes.Length;
            int length = Math.Max(candidate.Length, _tokenBytes.Length);
            for (int i = 0; i < length; i++)
                diff |= (i < candidate.Length ? candidate[i] : 0) ^ (i < _tokenBytes.Length ? _tokenBytes[i] : 0);
            return diff == 0;
        }

        private static OwnObservation CopyObservation(OwnObservation value)
        {
            return new OwnObservation
            {
                Sequence = value.Sequence, WorldId = value.WorldId, X = value.X, Y = value.Y,
                VelocityX = value.VelocityX, VelocityY = value.VelocityY,
                Health = value.Health, MaxHealth = value.MaxHealth, Dead = value.Dead,
                Menu = value.Menu, TextInput = value.TextInput, ControlState = value.ControlState,
                Reason = value.Reason, Inputs = value.Inputs == null ? new InputState() : value.Inputs.Copy(),
                LeaseInputs = value.LeaseInputs == null ? new InputState() : value.LeaseInputs.Copy(),
                GameTick = value.GameTick, MonotonicMs = value.MonotonicMs, CanArm = value.CanArm
            };
        }

        private void Emit(string eventName, string detail)
        {
            if (_diagnostic == null) return;
            long now = MonotonicClock.NowMs;
            lock (_diagnosticSync)
            {
                long last;
                if (_diagnosticLast.TryGetValue(eventName, out last) && now - last < 1000) return;
                _diagnosticLast[eventName] = now;
            }
            // Event names and details are bounded canonical strings/counters, never packet dumps/token.
            try { _diagnostic(eventName, detail); } catch { }
        }

        public void Dispose()
        {
            List<TcpClient> clients;
            TcpListener listener;
            string sessionId;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                clients = new List<TcpClient>(_clients);
                listener = _listener;
                sessionId = _ownerSession;
            }
            if (sessionId != null) _gate.Disconnect(sessionId, "server_disposed");
            try { if (listener != null) listener.Stop(); } catch { }
            foreach (var client in clients) try { client.Close(); } catch { }
            Emit("transport_stopped", "inputs_released");
        }
    }
}
