using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PeakModeTray
{
    // The JSON profile (config\peakmode.json) as the UI sees it. Reads with
    // comments and trailing commas allowed (like the core), writes back only
    // the keys that change, keeping every other key and the key order. Each
    // write goes to a temp file next to the config and then replaces it, so
    // a crash never leaves half a file. The core still validates the file on
    // Activate. Comments in the file are not kept when it is written.
    internal sealed class ProfileStore
    {
        private static readonly JsonDocumentOptions ReadOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        };
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        private JsonObject? _root;

        public ProfileStore(string? path) { Path = path; Reload(); }

        public string? Path  { get; }
        public string  Error { get; private set; } = "";
        public bool    CanEdit => _root != null && Path != null;

        public event Action? Changed;

        public void Reload()
        {
            _root = null;
            Error = "";
            if (Path == null) { Error = "config\\peakmode.json not found next to PeakModeTray.exe"; return; }
            try
            {
                _root = JsonNode.Parse(File.ReadAllText(Path), null, ReadOptions) as JsonObject;
                if (_root == null) Error = "the config is not a JSON object";
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Error = ex.Message;
            }
            Changed?.Invoke();
        }

        public bool GetBool(string? section, string key, bool fallback) =>
            Node(section, key) is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

        public string GetString(string? section, string key) =>
            Node(section, key) is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

        public bool SetBool(string? section, string key, bool value) => Set(section, key, JsonValue.Create(value));

        public bool SetString(string? section, string key, string value) => Set(section, key, JsonValue.Create(value));

        // Several keys in one write: (section, key, value)
        public bool SetMany(params (string? section, string key, bool value)[] changes)
        {
            if (_root == null) return false;
            foreach (var (s, k, v) in changes) Put(s, k, JsonValue.Create(v));
            return Save();
        }

        private bool Set(string? section, string key, JsonNode value)
        {
            if (_root == null) return false;
            Put(section, key, value);
            return Save();
        }

        private void Put(string? section, string key, JsonNode value)
        {
            JsonObject target = _root!;
            if (!string.IsNullOrEmpty(section))   // null or "" = top level
            {
                if (_root![section] is not JsonObject obj) { obj = new JsonObject(); _root[section] = obj; }
                target = obj;
            }
            target[key] = value;
        }

        private JsonNode? Node(string? section, string key) =>
            string.IsNullOrEmpty(section) ? _root?[key] : _root?[section]?[key];

        private bool Save()
        {
            if (_root == null || Path == null) return false;
            string tmp = Path + ".tmp";
            try
            {
                File.WriteAllText(tmp, _root.ToJsonString(WriteOptions) + Environment.NewLine);
                File.Move(tmp, Path, overwrite: true);
                Error = "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = "cannot save the config: " + ex.Message;
                try { File.Delete(tmp); } catch { }
                Changed?.Invoke();
                return false;
            }
            Changed?.Invoke();
            return true;
        }
    }
}
