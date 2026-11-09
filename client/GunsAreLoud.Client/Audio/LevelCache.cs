using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// What was measured for one recording (or one stock shot event: a loop
    /// beat with its tail). Every level here is the recording's own, without
    /// the volume of the bank it was measured in; that volume is kept beside it.
    /// </summary>
    internal sealed class LevelCacheEntry
    {
        /// <summary>K-weighted level of the event's attack window (first 50 ms), dB.</summary>
        internal float AttackWindowDb = float.NaN;
        /// <summary>Peak of the event, dBFS.</summary>
        internal float PeakDb = float.NaN;
        /// <summary>K-weighted level of the event's first 240 ms, dB: the window an explosion's level is held on. NaN when not measured.</summary>
        internal float BodyWindowDb = float.NaN;
        internal float BaseVolume;
        // For a clip decoded by playing it (no content hash in its key): the
        // hash of the decoded prefix and the rate it was decoded at, which a
        // later decode is checked against.
        internal string PrefixHash;
        internal int PrefixRate;

        internal bool HasLayer;
        internal float Level, BassLevel, DecayLevel;
        internal bool DecayReady;
        internal float NativeBodySeconds;
        internal int Group;
        internal string BodySource;
        internal int Rate;
        internal float BandPitch, BandHigh, BandLow;
        // The copy as the sum meter saw it in this band: its peak and attack
        // level at ratio 1 and the aligned sum's peak at the measured ratios.
        internal float CopyPeak;
        internal float CopyAttackDb = float.NaN;
        internal float[] SumPeaks;
        // The program limiter's attack loss table of the event (recording alone,
        // then each ratio; each at the measured peaks over the ceiling).
        internal float[] LimitLoss;

        internal bool HasDirect => !float.IsNaN(AttackWindowDb) && !float.IsNaN(PeakDb);
        internal bool HasBody => HasDirect && !float.IsNaN(BodyWindowDb);
        internal bool HasSum => HasLayer && SumPeaks != null && SumPeaks.Length == Runtime.ShotSumRatios.Values.Length &&
            !float.IsNaN(CopyAttackDb);
        internal bool HasLoss => HasSum && LimitLoss != null && LimitLoss.Length == Runtime.ShotSumRatios.LossLength;

        internal bool BandMatches(float pitch, float high, float low) =>
            HasLayer && Math.Abs(BandPitch - pitch) < 1e-4f && Math.Abs(BandHigh - high) < 1e-3f &&
            Math.Abs(BandLow - low) < 1e-3f;

        internal LevelCacheEntry Clone()
        {
            var clone = (LevelCacheEntry)MemberwiseClone();
            if (SumPeaks != null) clone.SumPeaks = (float[])SumPeaks.Clone();
            if (LimitLoss != null) clone.LimitLoss = (float[])LimitLoss.Clone();
            return clone;
        }
    }

    /// <summary>
    /// The persistent measurement cache: one JSON file next to the mod. Loaded
    /// once, written back on the main thread a moment after new measurements
    /// arrive (several measurements become one write), through a temporary
    /// file that replaces the old one. A damaged or foreign file is replaced.
    /// </summary>
    internal sealed class LevelCacheStore
    {
        /// <summary>
        /// Must be raised whenever a measurement algorithm changes (the meter, the
        /// event definition, the layer analysis or the key): a file of another
        /// format is replaced as a whole. The mod version written into the file
        /// is informational only and never invalidates entries.
        /// </summary>
        internal const int FormatVersion = 3;
        internal const double FlushDelaySeconds = 2.0;
        internal const double RetryDelaySeconds = 30.0;
        internal const int MaximumWriteFailures = 5;

        private readonly Dictionary<string, LevelCacheEntry> _entries =
            new Dictionary<string, LevelCacheEntry>(StringComparer.Ordinal);
        private readonly string _path;
        private readonly string _modVersion;
        private readonly Func<double> _clock;
        private bool _dirty;
        private double _due;
        private int _writeFailures;

        internal LevelCacheStore(string path, string modVersion, Func<double> clock)
        {
            _path = path;
            _modVersion = modVersion ?? "";
            _clock = clock ?? (() => 0.0);
        }

        internal int Count => _entries.Count;
        internal int WriteCount { get; private set; }
        internal bool Dirty => _dirty;
        internal string Path => _path;
        internal string LastWriteError { get; private set; }
        /// <summary>Writing failed too often this session; later measurements stay in memory only.</summary>
        internal bool WritingAbandoned => _writeFailures >= MaximumWriteFailures;

        /// <summary>Reads the file. Returns a one-line description of what was found.</summary>
        internal string Load()
        {
            _entries.Clear();
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return "none yet";
            string text;
            try { text = File.ReadAllText(_path, Encoding.UTF8); }
            catch (Exception error)
            {
                _dirty = true;
                _due = _clock();
                return "unreadable (" + error.GetType().Name + "), replaced";
            }
            if (!TryDeserialize(text, _entries, out string reason))
            {
                _entries.Clear();
                _dirty = true;
                _due = _clock();
                return reason + ", replaced";
            }
            return "loaded";
        }

        internal bool TryGet(string key, out LevelCacheEntry entry)
        {
            entry = null;
            return key != null && _entries.TryGetValue(key, out entry);
        }

        /// <summary>
        /// Forgets every measurement. The file is rewritten empty at the next
        /// tick, so recordings are measured again as weapons are equipped.
        /// Returns how many entries were dropped.
        /// </summary>
        internal int Reset()
        {
            int count = _entries.Count;
            _entries.Clear();
            _writeFailures = 0;
            _dirty = true;
            _due = _clock();
            return count;
        }

        /// <summary>
        /// Records the direct measures of a recording. A NaN
        /// <paramref name="bodyWindowDb"/> leaves the body window as it was:
        /// the weapons do not measure it, the explosions do.
        /// </summary>
        internal void PutDirect(string key, float attackWindowDb, float peakDb, float baseVolume,
            float bodyWindowDb = float.NaN)
        {
            if (key == null || float.IsNaN(attackWindowDb) || float.IsNaN(peakDb)) return;
            LevelCacheEntry entry = Entry(key, out bool created);
            bool body = !float.IsNaN(bodyWindowDb);
            bool changed = created || !Same(entry.AttackWindowDb, attackWindowDb) ||
                !Same(entry.PeakDb, peakDb) || !Same(entry.BaseVolume, baseVolume) ||
                body && !Same(entry.BodyWindowDb, bodyWindowDb);
            entry.AttackWindowDb = attackWindowDb;
            entry.PeakDb = peakDb;
            entry.BaseVolume = baseVolume;
            if (body) entry.BodyWindowDb = bodyWindowDb;
            if (changed) MarkDirty();
        }

        private static bool SameSums(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int index = 0; index < a.Length; index++) if (!Same(a[index], b[index])) return false;
            return true;
        }

        internal void PutPrefix(string key, string hash, int rate)
        {
            if (key == null || hash == null) return;
            LevelCacheEntry entry = Entry(key, out bool created);
            bool changed = created || entry.PrefixHash != hash || entry.PrefixRate != rate;
            entry.PrefixHash = hash;
            entry.PrefixRate = rate;
            if (changed) MarkDirty();
        }

        internal void PutLayer(string key, LevelCacheEntry layer)
        {
            if (key == null || layer == null || !layer.HasLayer) return;
            LevelCacheEntry entry = Entry(key, out bool created);
            bool changed = created || !entry.HasLayer || !Same(entry.Level, layer.Level) ||
                !Same(entry.BassLevel, layer.BassLevel) || !Same(entry.DecayLevel, layer.DecayLevel) ||
                entry.DecayReady != layer.DecayReady || !Same(entry.NativeBodySeconds, layer.NativeBodySeconds) ||
                entry.Group != layer.Group || entry.BodySource != layer.BodySource || entry.Rate != layer.Rate ||
                !entry.BandMatches(layer.BandPitch, layer.BandHigh, layer.BandLow) ||
                entry.HasSum != layer.HasSum || layer.HasSum && (!Same(entry.CopyPeak, layer.CopyPeak) ||
                !Same(entry.CopyAttackDb, layer.CopyAttackDb) || !SameSums(entry.SumPeaks, layer.SumPeaks)) ||
                entry.HasLoss != layer.HasLoss || layer.HasLoss && !SameSums(entry.LimitLoss, layer.LimitLoss);
            entry.HasLayer = true;
            entry.CopyPeak = layer.CopyPeak;
            entry.CopyAttackDb = layer.CopyAttackDb;
            entry.SumPeaks = layer.SumPeaks == null ? null : (float[])layer.SumPeaks.Clone();
            entry.LimitLoss = layer.LimitLoss == null ? null : (float[])layer.LimitLoss.Clone();
            entry.Level = layer.Level;
            entry.BassLevel = layer.BassLevel;
            entry.DecayLevel = layer.DecayLevel;
            entry.DecayReady = layer.DecayReady;
            entry.NativeBodySeconds = layer.NativeBodySeconds;
            entry.Group = layer.Group;
            entry.BodySource = layer.BodySource;
            entry.Rate = layer.Rate;
            entry.BandPitch = layer.BandPitch;
            entry.BandHigh = layer.BandHigh;
            entry.BandLow = layer.BandLow;
            if (layer.BaseVolume > 0f) entry.BaseVolume = layer.BaseVolume;
            if (changed) MarkDirty();
        }

        private LevelCacheEntry Entry(string key, out bool created)
        {
            created = !_entries.TryGetValue(key, out LevelCacheEntry entry);
            if (created) _entries[key] = entry = new LevelCacheEntry();
            return entry;
        }

        private void MarkDirty()
        {
            if (_dirty) return;
            _dirty = true;
            _due = _clock() + FlushDelaySeconds;
        }

        /// <summary>Writes the file when a coalesced write is due. Main thread.</summary>
        internal bool Tick()
        {
            if (!_dirty || _clock() < _due) return false;
            return Flush();
        }

        internal bool Flush()
        {
            if (!_dirty || WritingAbandoned) return false;
            _dirty = false;
            if (string.IsNullOrEmpty(_path)) return false;
            string temporary = _path + ".tmp";
            try
            {
                File.WriteAllText(temporary, Serialize(_entries, _modVersion), new UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
                WriteCount++;
                _writeFailures = 0;
                LastWriteError = null;
                return true;
            }
            catch (Exception error)
            {
                LastWriteError = error.GetType().Name + ": " + error.Message;
                // A locked or read-only file may come free: try again later, a
                // bounded number of times.
                _writeFailures++;
                if (!WritingAbandoned)
                {
                    _dirty = true;
                    _due = _clock() + RetryDelaySeconds;
                }
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                return false;
            }
        }

        private static bool Same(float a, float b) =>
            float.IsNaN(a) ? float.IsNaN(b) : !float.IsNaN(b) && Math.Abs(a - b) <= 1e-5f * Math.Max(1f, Math.Abs(a));

        // ----- format -----

        internal static string Serialize(Dictionary<string, LevelCacheEntry> entries, string modVersion)
        {
            var text = new StringBuilder(256 + entries.Count * 256);
            text.Append("{\n  \"format\": ").Append(FormatVersion.ToString(CultureInfo.InvariantCulture))
                .Append(",\n  \"mod\": ");
            AppendString(text, modVersion ?? "");
            text.Append(",\n  \"entries\": {");
            var keys = new List<string>(entries.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int index = 0; index < keys.Count; index++)
            {
                LevelCacheEntry entry = entries[keys[index]];
                text.Append(index == 0 ? "\n    " : ",\n    ");
                AppendString(text, keys[index]);
                text.Append(": {\"k50\": ");
                AppendNumber(text, entry.AttackWindowDb);
                text.Append(", \"peak\": ");
                AppendNumber(text, entry.PeakDb);
                text.Append(", \"baseVolume\": ");
                AppendNumber(text, entry.BaseVolume);
                if (!float.IsNaN(entry.BodyWindowDb))
                {
                    text.Append(", \"k240\": ");
                    AppendNumber(text, entry.BodyWindowDb);
                }
                if (entry.PrefixHash != null)
                {
                    text.Append(", \"prefixHash\": ");
                    AppendString(text, entry.PrefixHash);
                    text.Append(", \"prefixRate\": ").Append(entry.PrefixRate.ToString(CultureInfo.InvariantCulture));
                }
                if (entry.HasLayer)
                {
                    text.Append(", \"layer\": {\"level\": ");
                    AppendNumber(text, entry.Level);
                    text.Append(", \"bass\": ");
                    AppendNumber(text, entry.BassLevel);
                    text.Append(", \"decay\": ");
                    AppendNumber(text, entry.DecayLevel);
                    text.Append(", \"decayReady\": ").Append(entry.DecayReady ? "true" : "false");
                    text.Append(", \"bodySeconds\": ");
                    AppendNumber(text, entry.NativeBodySeconds);
                    text.Append(", \"group\": ").Append(entry.Group.ToString(CultureInfo.InvariantCulture));
                    text.Append(", \"bodySource\": ");
                    AppendString(text, entry.BodySource ?? "default");
                    text.Append(", \"rate\": ").Append(entry.Rate.ToString(CultureInfo.InvariantCulture));
                    text.Append(", \"pitch\": ");
                    AppendNumber(text, entry.BandPitch);
                    text.Append(", \"high\": ");
                    AppendNumber(text, entry.BandHigh);
                    text.Append(", \"low\": ");
                    AppendNumber(text, entry.BandLow);
                    if (entry.HasSum)
                    {
                        text.Append(", \"copyPeak\": ");
                        AppendNumber(text, entry.CopyPeak);
                        text.Append(", \"copyK50\": ");
                        AppendNumber(text, entry.CopyAttackDb);
                        text.Append(", \"sum\": [");
                        for (int point = 0; point < entry.SumPeaks.Length; point++)
                        {
                            if (point > 0) text.Append(", ");
                            AppendNumber(text, entry.SumPeaks[point]);
                        }
                        text.Append(']');
                        if (entry.HasLoss)
                        {
                            text.Append(", \"limitLoss\": [");
                            for (int point = 0; point < entry.LimitLoss.Length; point++)
                            {
                                if (point > 0) text.Append(", ");
                                AppendNumber(text, entry.LimitLoss[point]);
                            }
                            text.Append(']');
                        }
                    }
                    text.Append('}');
                }
                text.Append('}');
            }
            text.Append(keys.Count == 0 ? "}\n}\n" : "\n  }\n}\n");
            return text.ToString();
        }

        internal static bool TryDeserialize(string text, Dictionary<string, LevelCacheEntry> into, out string reason)
        {
            reason = null;
            object root;
            try { root = new JsonReader(text).ReadDocument(); }
            catch (FormatException error)
            {
                reason = "damaged (" + error.Message + ")";
                return false;
            }
            if (!(root is Dictionary<string, object> document))
            {
                reason = "damaged (not an object)";
                return false;
            }
            if (!document.TryGetValue("format", out object format) || !(format is double version) ||
                (int)version != FormatVersion)
            {
                reason = "format " + (format is double other ? other.ToString(CultureInfo.InvariantCulture) : "unknown") +
                    " instead of " + FormatVersion.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (!document.TryGetValue("entries", out object list) || !(list is Dictionary<string, object> entries))
            {
                reason = "damaged (no entries)";
                return false;
            }
            foreach (KeyValuePair<string, object> pair in entries)
            {
                if (!(pair.Value is Dictionary<string, object> value)) continue;
                var entry = new LevelCacheEntry
                {
                    AttackWindowDb = Number(value, "k50"),
                    PeakDb = Number(value, "peak"),
                    BodyWindowDb = Number(value, "k240"),
                    BaseVolume = Number(value, "baseVolume", 0f)
                };
                if (value.TryGetValue("prefixHash", out object prefix) && prefix is string prefixHash)
                {
                    entry.PrefixHash = prefixHash;
                    entry.PrefixRate = (int)Number(value, "prefixRate", 0f);
                }
                if (value.TryGetValue("layer", out object layerValue) && layerValue is Dictionary<string, object> layer)
                {
                    entry.HasLayer = true;
                    entry.Level = Number(layer, "level", 0f);
                    entry.BassLevel = Number(layer, "bass", 0f);
                    entry.DecayLevel = Number(layer, "decay", 0f);
                    entry.DecayReady = layer.TryGetValue("decayReady", out object ready) && ready is bool flag && flag;
                    entry.NativeBodySeconds = Number(layer, "bodySeconds", 0f);
                    entry.Group = (int)Number(layer, "group", 0f);
                    entry.BodySource = layer.TryGetValue("bodySource", out object source) && source is string name
                        ? name : "default";
                    entry.Rate = (int)Number(layer, "rate", 0f);
                    entry.BandPitch = Number(layer, "pitch", 0f);
                    entry.BandHigh = Number(layer, "high", 0f);
                    entry.BandLow = Number(layer, "low", 0f);
                    if (layer.TryGetValue("sum", out object sumValue) && sumValue is List<object> points &&
                        points.Count == Runtime.ShotSumRatios.Values.Length)
                    {
                        var sums = new float[points.Count];
                        bool numeric = true;
                        for (int point = 0; point < points.Count; point++)
                        {
                            if (points[point] is double d) sums[point] = (float)d;
                            else numeric = false;
                        }
                        if (numeric)
                        {
                            entry.SumPeaks = sums;
                            entry.CopyPeak = Number(layer, "copyPeak", 0f);
                            entry.CopyAttackDb = Number(layer, "copyK50");
                            if (layer.TryGetValue("limitLoss", out object lossValue) && lossValue is List<object> cells &&
                                cells.Count == Runtime.ShotSumRatios.LossLength)
                            {
                                var loss = new float[cells.Count];
                                bool lossNumeric = true;
                                for (int cell = 0; cell < cells.Count; cell++)
                                {
                                    if (cells[cell] is double d) loss[cell] = (float)d;
                                    else lossNumeric = false;
                                }
                                if (lossNumeric) entry.LimitLoss = loss;
                            }
                        }
                    }
                }
                into[pair.Key] = entry;
            }
            return true;
        }

        private static float Number(Dictionary<string, object> value, string key, float fallback = float.NaN) =>
            value.TryGetValue(key, out object number) && number is double d ? (float)d : fallback;

        private static void AppendNumber(StringBuilder text, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) text.Append("null");
            else text.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void AppendString(StringBuilder text, string value)
        {
            text.Append('"');
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (ch < 0x20) text.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(ch);
                        break;
                }
            }
            text.Append('"');
        }

        /// <summary>Just enough JSON for this file: objects, arrays, strings, numbers, booleans and null.</summary>
        private sealed class JsonReader
        {
            private readonly string _text;
            private int _position;

            internal JsonReader(string text) { _text = text ?? ""; }

            internal object ReadDocument()
            {
                object value = ReadValue(0);
                SkipSpace();
                if (_position != _text.Length) throw new FormatException("trailing text");
                return value;
            }

            private object ReadValue(int depth)
            {
                if (depth > 16) throw new FormatException("nested too deep");
                SkipSpace();
                if (_position >= _text.Length) throw new FormatException("unexpected end");
                char ch = _text[_position];
                if (ch == '{') return ReadObject(depth);
                if (ch == '[') return ReadArray(depth);
                if (ch == '"') return ReadString();
                if (Match("true")) return true;
                if (Match("false")) return false;
                if (Match("null")) return null;
                return ReadNumber();
            }

            private Dictionary<string, object> ReadObject(int depth)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                _position++;
                SkipSpace();
                if (Peek() == '}') { _position++; return result; }
                while (true)
                {
                    SkipSpace();
                    if (Peek() != '"') throw new FormatException("expected a name");
                    string key = ReadString();
                    SkipSpace();
                    if (Peek() != ':') throw new FormatException("expected ':'");
                    _position++;
                    result[key] = ReadValue(depth + 1);
                    SkipSpace();
                    char next = Peek();
                    _position++;
                    if (next == '}') return result;
                    if (next != ',') throw new FormatException("expected ',' or '}'");
                }
            }

            private List<object> ReadArray(int depth)
            {
                var result = new List<object>();
                _position++;
                SkipSpace();
                if (Peek() == ']') { _position++; return result; }
                while (true)
                {
                    result.Add(ReadValue(depth + 1));
                    SkipSpace();
                    char next = Peek();
                    _position++;
                    if (next == ']') return result;
                    if (next != ',') throw new FormatException("expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                var text = new StringBuilder();
                _position++;
                while (true)
                {
                    if (_position >= _text.Length) throw new FormatException("unterminated string");
                    char ch = _text[_position++];
                    if (ch == '"') return text.ToString();
                    if (ch != '\\') { text.Append(ch); continue; }
                    if (_position >= _text.Length) throw new FormatException("unterminated escape");
                    char escape = _text[_position++];
                    switch (escape)
                    {
                        case '"': text.Append('"'); break;
                        case '\\': text.Append('\\'); break;
                        case '/': text.Append('/'); break;
                        case 'b': text.Append('\b'); break;
                        case 'f': text.Append('\f'); break;
                        case 'n': text.Append('\n'); break;
                        case 'r': text.Append('\r'); break;
                        case 't': text.Append('\t'); break;
                        case 'u':
                            if (_position + 4 > _text.Length) throw new FormatException("bad escape");
                            text.Append((char)int.Parse(_text.Substring(_position, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture));
                            _position += 4;
                            break;
                        default: throw new FormatException("bad escape");
                    }
                }
            }

            private double ReadNumber()
            {
                int start = _position;
                while (_position < _text.Length && "+-0123456789.eE".IndexOf(_text[_position]) >= 0) _position++;
                if (start == _position) throw new FormatException("unexpected character");
                if (!double.TryParse(_text.Substring(start, _position - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value))
                    throw new FormatException("bad number");
                return value;
            }

            private bool Match(string word)
            {
                if (string.CompareOrdinal(_text, _position, word, 0, word.Length) != 0) return false;
                _position += word.Length;
                return true;
            }

            private char Peek() => _position < _text.Length ? _text[_position] : '\0';

            private void SkipSpace()
            {
                while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
            }
        }
    }

    /// <summary>
    /// The cache of this run, and the keys recordings are filed under. A key
    /// names the clip, its length, rate and channel count, and either a hash
    /// of the decoded samples (a clip read directly) or "stream" (a clip that
    /// is decoded by playing it).
    /// </summary>
    internal static class LevelCache
    {
        internal const string FileName = "GunsAreLoud.LevelCache.json";
        internal const string StreamTag = "stream";

        internal static LevelCacheStore Store { get; private set; }

        internal static void Initialize(LevelCacheStore store) => Store = store;

        internal static void Shutdown()
        {
            Store?.Flush();
            Store = null;
        }

        /// <summary>Drops every stored measurement; 0 without a store.</summary>
        internal static int Reset() => Store?.Reset() ?? 0;

        internal static bool TryGet(string key, out LevelCacheEntry entry)
        {
            entry = null;
            return Store != null && Store.TryGet(key, out entry);
        }

        internal static void PutDirect(string key, float attackWindowDb, float peakP99Db, float baseVolume,
            float bodyWindowDb = float.NaN) =>
            Store?.PutDirect(key, attackWindowDb, peakP99Db, baseVolume, bodyWindowDb);

        internal static void PutLayer(string key, LevelCacheEntry layer) => Store?.PutLayer(key, layer);

        internal static void PutPrefix(string key, string hash, int rate) => Store?.PutPrefix(key, hash, rate);

        internal static bool Tick() => Store != null && Store.Tick();

        internal static string ClipKey(string name, int samples, int frequency, int channels, string tag) =>
            (name ?? "") + "|" + samples.ToString(CultureInfo.InvariantCulture) + "|" +
            frequency.ToString(CultureInfo.InvariantCulture) + "|" +
            channels.ToString(CultureInfo.InvariantCulture) + "|" + tag;

        internal static string HashTag(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

        /// <summary>
        /// A stock shot event: the loop body's key, the tail's key (or none)
        /// and the tail's gain relative to the body.
        /// </summary>
        internal static string EventKey(string bodyKey, string tailKey, float tailGain) =>
            bodyKey + ">" + (tailKey ?? "none") + "@" + tailGain.ToString("0.0000", CultureInfo.InvariantCulture);

        /// <summary>64-bit FNV-1a over the bit patterns of the first <paramref name="count"/> samples.</summary>
        internal static ulong Fnv1a(float[] samples, int count)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            if (samples == null) return hash;
            count = Math.Min(count, samples.Length);
            for (int index = 0; index < count; index++)
            {
                uint bits = new FloatBits { Float = samples[index] }.Bits;
                for (int shift = 0; shift < 32; shift += 8)
                {
                    hash ^= (bits >> shift) & 0xFF;
                    hash *= prime;
                }
            }
            return hash;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] internal float Float;
            [System.Runtime.InteropServices.FieldOffset(0)] internal uint Bits;
        }
    }
}
