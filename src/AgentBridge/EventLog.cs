using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using TerrariaAgent.Protocol;

namespace TerrariaAgent.Bridge
{
    internal sealed class EventLog : IDisposable
    {
        private readonly BlockingCollection<string> _lines = new BlockingCollection<string>(2048);
        private readonly string _directory;
        private readonly string _runId;
        private readonly Thread _writer;
        private volatile bool _failed;

        public EventLog(string directory, string runId)
        {
            _directory = directory;
            _runId = runId;
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "TerrariaAgent.Log" };
            _writer.Start();
        }

        public bool Record(string eventName, string detail, OwnObservation observation = null)
        {
            if (_failed || _lines.IsAddingCompleted) return false;
            var record = new LogRecord { Utc = DateTime.UtcNow.ToString("o"), MonotonicMs = MonotonicClock.NowMs,
                RunId = _runId, Event = eventName, Detail = detail, Observation = observation };
            string line = Encoding.UTF8.GetString(JsonCodec.Serialize(record));
            try { return _lines.TryAdd(line); }
            catch (InvalidOperationException) { return false; }
        }

        private void WriteLoop()
        {
            try
            {
                // 8 x 32 MiB per run, bounded queue, 5 Hz own-state logs. Never
                // snapshot terrain, inventories or unfiltered game objects here.
                int segment = 0;
                StreamWriter writer = null;
                try
                {
                    foreach (string line in _lines.GetConsumingEnumerable())
                    {
                        if (writer == null || writer.BaseStream.Length >= 32L * 1024 * 1024)
                        {
                            if (writer != null) writer.Dispose();
                            if (segment >= 8) { _failed = true; return; }
                            writer = new StreamWriter(Path.Combine(_directory, "bridge-" + (segment++).ToString("D2") + ".jsonl"), false, new UTF8Encoding(false));
                        }
                        writer.WriteLine(line);
                        writer.Flush();
                    }
                }
                finally { if (writer != null) writer.Dispose(); }
            }
            catch (Exception error)
            {
                _failed = true;
                Console.Error.WriteLine("Agent logging stopped: " + error.Message);
            }
        }

        public void Dispose()
        {
            _lines.CompleteAdding();
            _writer.Join(2000);
        }

        [DataContract]
        private sealed class LogRecord
        {
            [DataMember(Name = "utc", Order = 0)] public string Utc { get; set; }
            [DataMember(Name = "monotonicMs", Order = 1)] public long MonotonicMs { get; set; }
            [DataMember(Name = "runId", Order = 2)] public string RunId { get; set; }
            [DataMember(Name = "decisionMode", Order = 3)] public string DecisionMode = "rules";
            [DataMember(Name = "event", Order = 4)] public string Event { get; set; }
            [DataMember(Name = "detail", Order = 5)] public string Detail { get; set; }
            [DataMember(Name = "observation", Order = 6, EmitDefaultValue = false)] public OwnObservation Observation { get; set; }
        }
    }
}
