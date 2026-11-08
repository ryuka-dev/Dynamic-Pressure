using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;

namespace Ryuka.Sulfur.DynamicPressure
{
    /// <summary>
    /// Writes a play session's combat events to a local file a player can send to the author.
    /// </summary>
    /// <remarks>
    /// Observation only: nothing here feeds a spawn decision, and a recorder that cannot write
    /// goes quiet instead of affecting the game. Everything it touches sits under one directory
    /// owned by this mod, and only files carrying its own prefixes are ever deleted. Callers must
    /// not pass file paths, player names or account identifiers as values.
    /// </remarks>
    internal sealed class CombatRecorder
    {
        private const string SessionPrefix = "session-";
        private const string ExportPrefix = "dynamicpressure-feedback-";
        private const string Extension = ".jsonl";

        private const int FlushAtLines = 2000;
        private const int FlushRetryLines = 500;
        private const int KeepSessions = 20;
        private const int KeepExports = 3;
        private const int ExportSessions = 5;
        private const int MaxFlushFailures = 3;
        private const int StampLength = 15;
        private const long MaxSessionBytes = 5L * 1024L * 1024L;
        private const long MaxTotalBytes = 20L * 1024L * 1024L;

        private readonly string _directory;
        private readonly ManualLogSource _log;
        private readonly Func<DateTime> _now;
        private readonly List<string> _buffer = new List<string>();

        private string _sessionPath;
        private long _sessionBytes;
        private bool _stopped;
        private bool _unavailable;
        private int _flushFailures;
        private int _flushAtLines = FlushAtLines;

        public CombatRecorder(string directory, ManualLogSource log, Func<DateTime> now = null)
        {
            _directory = directory;
            _log = log;
            _now = now ?? (() => DateTime.Now);
        }

        public bool IsRecording
        {
            get { return _sessionPath != null && !_stopped; }
        }

        /// <summary>True when <see cref="Stop"/> has something to clear before recording can begin again.</summary>
        public bool NeedsStop
        {
            get { return _sessionPath != null || _unavailable; }
        }

        /// <summary>True when at least one recorded session is on disk.</summary>
        public bool HasRecordedData
        {
            get
            {
                try
                {
                    return Directory.Exists(_directory) && Files(SessionPrefix).Any(f => f.Length > 0L);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Starts a new session file. Does nothing if one is already open, and after a failure
        /// stays quiet until <see cref="Stop"/>, so a broken disk costs one warning and no retries.
        /// </summary>
        public void Start()
        {
            if (_sessionPath != null || _unavailable)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_directory);
                _sessionPath = UniquePath(SessionPrefix);
                _sessionBytes = 0L;
                _flushFailures = 0;
                _stopped = false;
            }
            catch (Exception e)
            {
                _sessionPath = null;
                _unavailable = true;
                _log?.LogWarning("Combat data recording is unavailable: " + e.GetType().Name);
                return;
            }

            PruneQuietly(SessionPrefix, KeepSessions - 1, null);
        }

        /// <summary>Flushes and closes the current session.</summary>
        public void Stop()
        {
            Flush();
            _buffer.Clear();
            _sessionPath = null;
            _sessionBytes = 0L;
            _stopped = false;
            _unavailable = false;
            _flushFailures = 0;
        }

        /// <summary>
        /// Queues one event. <paramref name="fields"/> alternates field names and values; values
        /// may be numbers, booleans, strings, null, or nested arrays and dictionaries of those.
        /// </summary>
        public void Record(string kind, float time, params object[] fields)
        {
            if (!IsRecording)
            {
                return;
            }

            if (fields == null)
            {
                fields = new object[0];
            }

            StringBuilder line = new StringBuilder(128);
            line.Append("{\"e\":");
            AppendValue(line, kind);
            line.Append(",\"t\":");
            AppendValue(line, time);

            for (int i = 0; i + 1 < fields.Length; i += 2)
            {
                line.Append(',');
                AppendValue(line, Convert.ToString(fields[i], CultureInfo.InvariantCulture));
                line.Append(':');
                AppendValue(line, fields[i + 1]);
            }

            line.Append('}');
            _buffer.Add(line.ToString());

            if (_buffer.Count >= _flushAtLines)
            {
                Flush();
            }
        }

        /// <summary>Writes queued events to disk. Meant for quiet moments such as level changes.</summary>
        public void Flush()
        {
            if (_buffer.Count == 0 || _sessionPath == null || _stopped)
            {
                return;
            }

            StringBuilder text = new StringBuilder();
            long size = 0L;
            int lines = 0;
            bool full = false;

            foreach (string line in _buffer)
            {
                long lineSize = Encoding.UTF8.GetByteCount(line) + 1L;
                if (_sessionBytes + size + lineSize > MaxSessionBytes)
                {
                    full = true;
                    break;
                }

                text.Append(line).Append('\n');
                size += lineSize;
                lines++;
            }

            try
            {
                if (lines > 0)
                {
                    File.AppendAllText(_sessionPath, text.ToString(), new UTF8Encoding(false));
                }
            }
            catch (Exception e)
            {
                // Often a brief lock by an antivirus or backup tool, so wait for more lines before
                // retrying instead of burning the failures on the very next event.
                _flushAtLines = _buffer.Count + FlushRetryLines;

                if (++_flushFailures >= MaxFlushFailures)
                {
                    Silence("Combat data recording stopped: " + e.GetType().Name);
                }

                return;
            }

            _flushFailures = 0;
            _flushAtLines = FlushAtLines;
            _sessionBytes += size;
            _buffer.RemoveRange(0, lines);

            if (full)
            {
                Silence("Combat data recording stopped: this session reached its size limit.");
                return;
            }

            EnforceTotalLimit();
        }

        /// <summary>
        /// Gathers the newest sessions into one file to send, and returns its full path.
        /// </summary>
        /// <returns>False when there is nothing recorded or the file could not be written.</returns>
        public bool TryExport(out string exportPath)
        {
            exportPath = null;
            Flush();

            string temporary = null;

            try
            {
                if (!Directory.Exists(_directory))
                {
                    return false;
                }

                List<FileInfo> chosen = new List<FileInfo>();
                long remaining = MaxTotalBytes;

                foreach (FileInfo session in Files(SessionPrefix).Take(ExportSessions))
                {
                    if (session.Length > 0L && session.Length <= remaining)
                    {
                        chosen.Add(session);
                        remaining -= session.Length;
                    }
                }

                if (chosen.Count == 0)
                {
                    return false;
                }

                chosen.Reverse();

                string path = UniquePath(ExportPrefix);
                temporary = path + ".tmp";

                using (FileStream output = new FileStream(temporary, FileMode.Create, FileAccess.Write))
                {
                    foreach (FileInfo session in chosen)
                    {
                        using (FileStream input = new FileStream(session.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            input.CopyTo(output);
                        }
                    }
                }

                File.Move(temporary, path);
                temporary = null;
                exportPath = path;
            }
            catch (Exception e)
            {
                _log?.LogWarning("Combat data could not be exported: " + e.GetType().Name);
                DeleteQuietly(temporary);
                return false;
            }

            PruneQuietly(ExportPrefix, KeepExports, exportPath);
            return true;
        }

        private void Silence(string reason)
        {
            _buffer.Clear();
            _stopped = true;
            _log?.LogWarning(reason);
        }

        private string UniquePath(string prefix)
        {
            string stamp = _now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(_directory, prefix + stamp + Extension);

            for (int i = 2; File.Exists(path); i++)
            {
                path = Path.Combine(_directory, prefix + stamp + "-" + i.ToString(CultureInfo.InvariantCulture) + Extension);
            }

            return path;
        }

        private FileInfo[] Files(string prefix)
        {
            return new DirectoryInfo(_directory)
                .GetFiles(prefix + "*" + Extension)
                .OrderByDescending(f => Stamp(f.Name, prefix), StringComparer.Ordinal)
                .ThenByDescending(f => Suffix(f.Name, prefix))
                .ToArray();
        }

        private static string Stamp(string fileName, string prefix)
        {
            string body = Path.GetFileNameWithoutExtension(fileName).Substring(prefix.Length);
            return body.Length > StampLength ? body.Substring(0, StampLength) : body;
        }

        // A later file in the same second carries -2, -3 and so on; ordering those as text would
        // put -10 before -2.
        private static int Suffix(string fileName, string prefix)
        {
            string body = Path.GetFileNameWithoutExtension(fileName).Substring(prefix.Length);
            int number;
            return body.Length > StampLength + 1
                && int.TryParse(body.Substring(StampLength + 1), NumberStyles.None, CultureInfo.InvariantCulture, out number)
                ? number
                : 1;
        }

        private void PruneQuietly(string prefix, int keep, string alwaysKeep)
        {
            try
            {
                IEnumerable<FileInfo> candidates = Files(prefix);
                if (alwaysKeep != null)
                {
                    keep--;
                    candidates = candidates.Where(f => !SamePath(f.FullName, alwaysKeep));
                }

                foreach (FileInfo old in candidates.Skip(Math.Max(0, keep)).ToArray())
                {
                    DeleteQuietly(old.FullName);
                }
            }
            catch (Exception)
            {
                // Clean-up is best effort; failing it must not cost the player an export.
            }
        }

        private void EnforceTotalLimit()
        {
            try
            {
                long total = 0L;
                foreach (FileInfo session in Files(SessionPrefix))
                {
                    total += session.Length;

                    if (total > MaxTotalBytes && !SamePath(session.FullName, _sessionPath))
                    {
                        DeleteQuietly(session.FullName);
                    }
                }
            }
            catch (Exception)
            {
                // Best effort, as above.
            }
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (path != null)
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best effort, as above.
            }
        }

        internal static void AppendValue(StringBuilder line, object value)
        {
            if (value == null)
            {
                line.Append("null");
            }
            else if (value is string)
            {
                AppendString(line, (string)value);
            }
            else if (value is bool)
            {
                line.Append((bool)value ? "true" : "false");
            }
            else if (value is float || value is double || value is decimal)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    line.Append("null");
                }
                else
                {
                    line.Append(number.ToString("0.###", CultureInfo.InvariantCulture));
                }
            }
            else if (value is System.Collections.IDictionary)
            {
                line.Append('{');
                bool first = true;
                foreach (System.Collections.DictionaryEntry pair in (System.Collections.IDictionary)value)
                {
                    if (!first)
                    {
                        line.Append(',');
                    }

                    first = false;
                    AppendString(line, Convert.ToString(pair.Key, CultureInfo.InvariantCulture));
                    line.Append(':');
                    AppendValue(line, pair.Value);
                }

                line.Append('}');
            }
            else if (value is System.Collections.IEnumerable)
            {
                line.Append('[');
                bool first = true;
                foreach (object item in (System.Collections.IEnumerable)value)
                {
                    if (!first)
                    {
                        line.Append(',');
                    }

                    first = false;
                    AppendValue(line, item);
                }

                line.Append(']');
            }
            else if (IsWholeNumber(value))
            {
                line.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            else
            {
                AppendString(line, Convert.ToString(value, CultureInfo.InvariantCulture));
            }
        }

        private static bool IsWholeNumber(object value)
        {
            return value is int || value is long || value is short || value is byte
                || value is uint || value is ulong || value is ushort || value is sbyte;
        }

        private static void AppendString(StringBuilder line, string text)
        {
            line.Append('"');

            foreach (char c in text)
            {
                switch (c)
                {
                    case '"':
                        line.Append("\\\"");
                        break;
                    case '\\':
                        line.Append("\\\\");
                        break;
                    case '\n':
                        line.Append("\\n");
                        break;
                    case '\r':
                        line.Append("\\r");
                        break;
                    case '\t':
                        line.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            line.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            line.Append(c);
                        }

                        break;
                }
            }

            line.Append('"');
        }
    }
}
