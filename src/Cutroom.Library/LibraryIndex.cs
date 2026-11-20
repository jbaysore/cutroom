using System.Text.Json;
using System.Text.RegularExpressions;
using Cutroom.Core;
using Microsoft.Data.Sqlite;

namespace Cutroom.Library;

public enum LibraryRole { Footage, Asset }
public enum AssetCategory { Video, Music, Sfx, Image, Background }

public sealed record LibraryItem(long Id, string Path, MediaKind Kind, LibraryRole Role, AssetCategory Category, double Duration,
    int Width, int Height, double Fps, string Fingerprint, bool Favorite, long LastUsed, IReadOnlyList<string> Tags)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

public sealed record Moment(long Id, long ItemId, double In, double Out, string Note, IReadOnlyList<string> Tags);

public sealed record SearchHit(LibraryItem Item, Moment? Moment);

/// <summary>
/// The footage and asset library. Files are indexed where they are and never moved or renamed.
/// Search is SQLite FTS5 over file names, folder names, tags and moment notes.
/// </summary>
public sealed class LibraryIndex : IDisposable
{
    readonly SqliteConnection db;
    readonly Lock gate = new();
    readonly List<FileSystemWatcher> watchers = [];
    public event Action? Changed;

    public LibraryIndex(string? path = null)
    {
        path ??= Path.Combine(AppSettings.Folder, "index.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection($"Data Source={path}");
        db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS items(
              id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, kind TEXT NOT NULL, role TEXT NOT NULL, category TEXT NOT NULL,
              duration REAL, width INTEGER, height INTEGER, fps REAL, fingerprint TEXT, mtime INTEGER, size INTEGER,
              favorite INTEGER NOT NULL DEFAULT 0, last_used INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS moments(
              id INTEGER PRIMARY KEY, item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
              in_s REAL NOT NULL, out_s REAL NOT NULL, note TEXT NOT NULL DEFAULT '', created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS tags(
              item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE, moment_id INTEGER REFERENCES moments(id) ON DELETE CASCADE, tag TEXT NOT NULL COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS tags_item ON tags(item_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts5(body, item_id UNINDEXED, moment_id UNINDEXED, tokenize='unicode61 remove_diacritics 2');
            """);
    }

    void Exec(string sql, params (string, object?)[] args)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    T Scalar<T>(string sql, params (string, object?)[] args)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            var r = cmd.ExecuteScalar();
            return r is null || r is DBNull ? default! : (T)Convert.ChangeType(r, typeof(T));
        }
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    public static AssetCategory Categorize(string path, MediaKind kind, LibraryRole role, double duration)
    {
        var folders = path.ToLowerInvariant();
        if (kind == MediaKind.Image) return AssetCategory.Image;
        if (kind == MediaKind.Audio)
        {
            if (Regex.IsMatch(folders, @"\b(sfx|sound ?effects?|whoosh|impact|hits?)\b")) return AssetCategory.Sfx;
            if (Regex.IsMatch(folders, @"\b(music|songs?|tracks?|bgm)\b")) return AssetCategory.Music;
            return duration > 20 ? AssetCategory.Music : AssetCategory.Sfx;
        }
        if (role == LibraryRole.Asset && Regex.IsMatch(folders, @"\b(backgrounds?|bg|loops?|textures?|paper|notebook)\b")) return AssetCategory.Background;
        return AssetCategory.Video;
    }

    /// <summary>Adds or refreshes one file. Returns the item id, or null when it is not media.</summary>
    public async Task<long?> AddOrUpdate(string path, LibraryRole role, CancellationToken ct = default)
    {
        if (!MediaProbe.IsMedia(path) || !File.Exists(path)) return null;
        var info = new FileInfo(path);
        var existing = Query("SELECT id, mtime, size FROM items WHERE path=$p", r => (r.GetInt64(0), r.IsDBNull(1) ? 0 : r.GetInt64(1), r.IsDBNull(2) ? 0 : r.GetInt64(2)), ("$p", info.FullName));
        if (existing.Count == 1 && existing[0].Item2 == info.LastWriteTimeUtc.Ticks && existing[0].Item3 == info.Length) return existing[0].Item1;
        MediaRef m;
        try { m = await MediaProbe.Probe(path, ct).ConfigureAwait(false); }
        catch (ToolException) { return null; }
        var category = Categorize(info.FullName, m.Kind, role, Time.ToSeconds(m.Duration));
        Exec("""
            INSERT INTO items(path, kind, role, category, duration, width, height, fps, fingerprint, mtime, size)
            VALUES($p,$k,$r,$c,$d,$w,$h,$f,$fp,$mt,$s)
            ON CONFLICT(path) DO UPDATE SET kind=$k, role=$r, category=$c, duration=$d, width=$w, height=$h, fps=$f, fingerprint=$fp, mtime=$mt, size=$s
            """, ("$p", info.FullName), ("$k", m.Kind.ToString()), ("$r", role.ToString()), ("$c", category.ToString()), ("$d", Time.ToSeconds(m.Duration)),
            ("$w", m.Width), ("$h", m.Height), ("$f", m.Fps), ("$fp", m.Fingerprint), ("$mt", info.LastWriteTimeUtc.Ticks), ("$s", info.Length));
        long id = Scalar<long>("SELECT id FROM items WHERE path=$p", ("$p", info.FullName));
        Reindex(id);
        Changed?.Invoke();
        return id;
    }

    public void Remove(string path)
    {
        var id = Scalar<long>("SELECT id FROM items WHERE path=$p", ("$p", Path.GetFullPath(path)));
        if (id == 0) return;
        Exec("DELETE FROM search WHERE item_id=$i", ("$i", id));
        Exec("DELETE FROM items WHERE id=$i", ("$i", id));
        Changed?.Invoke();
    }

    /// <summary>Scans a folder tree, adding new/changed files and removing ones that no longer exist.</summary>
    public async Task Scan(string root, LibraryRole role, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return;
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(MediaProbe.IsMedia).ToList();
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await AddOrUpdate(files[i], role, ct).ConfigureAwait(false);
            progress?.Report((i + 1) / (double)files.Count);
        }
        var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        foreach (var gone in Query("SELECT path FROM items WHERE path LIKE $p", r => r.GetString(0), ("$p", prefix + "%")).Where(p => !File.Exists(p)))
            Remove(gone);
    }

    /// <summary>Keeps the index current while Cutroom runs.</summary>
    public void Watch(string root, LibraryRole role)
    {
        if (!Directory.Exists(root)) return;
        var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, EnableRaisingEvents = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        void Changed(string path) => _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false); // let writers finish
            try { if (File.Exists(path)) await AddOrUpdate(path, role).ConfigureAwait(false); else Remove(path); }
            catch (IOException) { }
        });
        w.Created += (_, e) => Changed(e.FullPath);
        w.Changed += (_, e) => Changed(e.FullPath);
        w.Deleted += (_, e) => Remove(e.FullPath);
        w.Renamed += (_, e) => { Remove(e.OldFullPath); Changed(e.FullPath); };
        watchers.Add(w);
    }

    void Reindex(long itemId)
    {
        var path = Scalar<string>("SELECT path FROM items WHERE id=$i", ("$i", itemId));
        if (path is null) return;
        var tags = Query("SELECT tag FROM tags WHERE item_id=$i AND moment_id IS NULL", r => r.GetString(0), ("$i", itemId));
        var words = Regex.Replace(Path.GetFileNameWithoutExtension(path) + " " + Path.GetDirectoryName(path), @"[_\-.\\/]+", " ");
        Exec("DELETE FROM search WHERE item_id=$i", ("$i", itemId));
        Exec("INSERT INTO search(body, item_id, moment_id) VALUES($b,$i,NULL)", ("$b", words + " " + string.Join(" ", tags)), ("$i", itemId));
        foreach (var (mid, note) in Query("SELECT id, note FROM moments WHERE item_id=$i", r => (r.GetInt64(0), r.GetString(1)), ("$i", itemId)))
        {
            var mtags = Query("SELECT tag FROM tags WHERE moment_id=$m", r => r.GetString(0), ("$m", mid));
            Exec("INSERT INTO search(body, item_id, moment_id) VALUES($b,$i,$m)", ("$b", $"{words} {note} {string.Join(" ", mtags.Concat(tags))}"), ("$i", itemId), ("$m", mid));
        }
    }

    public long? ItemId(string path) => Scalar<long?>("SELECT id FROM items WHERE path=$p", ("$p", Path.GetFullPath(path)));

    public void Tag(long itemId, string tag, long? momentId = null)
    {
        tag = NormalizeTag(tag);
        if (tag.Length == 0) return;
        Exec("INSERT INTO tags(item_id, moment_id, tag) SELECT $i,$m,$t WHERE NOT EXISTS (SELECT 1 FROM tags WHERE item_id=$i AND moment_id IS $m AND tag=$t)",
            ("$i", itemId), ("$m", momentId), ("$t", tag));
        Reindex(itemId);
        Changed?.Invoke();
    }

    public void Untag(long itemId, string tag, long? momentId = null)
    {
        Exec("DELETE FROM tags WHERE item_id=$i AND moment_id IS $m AND tag=$t", ("$i", itemId), ("$m", momentId), ("$t", NormalizeTag(tag)));
        Reindex(itemId);
        Changed?.Invoke();
    }

    public static string NormalizeTag(string tag) => Regex.Replace(tag.Trim().TrimStart('#').ToLowerInvariant(), @"\s+", "-");

    public long AddMoment(long itemId, double inSeconds, double outSeconds, string note, IEnumerable<string>? tags = null)
    {
        Exec("INSERT INTO moments(item_id, in_s, out_s, note, created) VALUES($i,$a,$b,$n,$c)",
            ("$i", itemId), ("$a", inSeconds), ("$b", Math.Max(inSeconds, outSeconds)), ("$n", note), ("$c", DateTime.UtcNow.Ticks));
        long id = Scalar<long>("SELECT last_insert_rowid()");
        foreach (var t in tags ?? []) Exec("INSERT INTO tags(item_id, moment_id, tag) VALUES($i,$m,$t)", ("$i", itemId), ("$m", id), ("$t", NormalizeTag(t)));
        Reindex(itemId);
        Changed?.Invoke();
        return id;
    }

    public void SetFavorite(long itemId, bool favorite)
    {
        Exec("UPDATE items SET favorite=$f WHERE id=$i", ("$f", favorite ? 1 : 0), ("$i", itemId));
        Changed?.Invoke();
    }

    public void MarkUsed(string path) => Exec("UPDATE items SET last_used=$t WHERE path=$p", ("$t", DateTime.UtcNow.Ticks), ("$p", Path.GetFullPath(path)));

    public IReadOnlyList<string> AllTags() => Query("SELECT tag, COUNT(*) c FROM tags GROUP BY tag ORDER BY c DESC", r => r.GetString(0));

    LibraryItem ReadItem(SqliteDataReader r)
    {
        long id = r.GetInt64(0);
        var tags = Query("SELECT DISTINCT tag FROM tags WHERE item_id=$i AND moment_id IS NULL", x => x.GetString(0), ("$i", id));
        return new LibraryItem(id, r.GetString(1), Enum.Parse<MediaKind>(r.GetString(2)), Enum.Parse<LibraryRole>(r.GetString(3)), Enum.Parse<AssetCategory>(r.GetString(4)),
            r.IsDBNull(5) ? 0 : r.GetDouble(5), r.IsDBNull(6) ? 0 : r.GetInt32(6), r.IsDBNull(7) ? 0 : r.GetInt32(7), r.IsDBNull(8) ? 0 : r.GetDouble(8),
            r.IsDBNull(9) ? "" : r.GetString(9), r.GetInt64(10) != 0, r.GetInt64(11), tags);
    }

    const string ItemColumns = "items.id, items.path, items.kind, items.role, items.category, items.duration, items.width, items.height, items.fps, items.fingerprint, items.favorite, items.last_used";

    public LibraryItem? Get(long id) => Query($"SELECT {ItemColumns} FROM items WHERE id=$i", ReadItem, ("$i", id)).FirstOrDefault();

    public IReadOnlyList<Moment> Moments(long itemId) =>
        Query("SELECT id, item_id, in_s, out_s, note FROM moments WHERE item_id=$i ORDER BY in_s", r => new Moment(r.GetInt64(0), r.GetInt64(1), r.GetDouble(2), r.GetDouble(3), r.GetString(4),
            Query("SELECT tag FROM tags WHERE moment_id=$m", x => x.GetString(0), ("$m", r.GetInt64(0)))), ("$i", itemId));

    /// <summary>
    /// Search-as-you-type. Words are prefix matches; "#tag" words must match a tag exactly. An empty query lists
    /// everything in the role/category (favorites and recents first).
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, LibraryRole? role = null, AssetCategory? category = null, bool favoritesOnly = false, bool recentOnly = false, int limit = 200)
        => Search(query, out _, role, category, favoritesOnly, recentOnly, limit);

    public IReadOnlyList<SearchHit> Search(string query, out bool partialMatches, LibraryRole? role = null, AssetCategory? category = null, bool favoritesOnly = false, bool recentOnly = false, int limit = 200)
    {
        var tags = Regex.Matches(query, @"#([\w\-]+)").Select(m => NormalizeTag(m.Groups[1].Value)).ToList();
        var words = Regex.Matches(Regex.Replace(query, @"#[\w\-]+", " "), @"[\p{L}\p{N}]+")
            .Select(m => m.Value.ToLowerInvariant()).Distinct().Select(w => $"\"{w}\"*").ToList();
        var filters = new List<string>();
        var args = new List<(string, object?)>();
        if (role is not null) { filters.Add("items.role=$role"); args.Add(("$role", role.ToString())); }
        if (category is not null) { filters.Add("items.category=$cat"); args.Add(("$cat", category.ToString())); }
        if (favoritesOnly) filters.Add("items.favorite=1");
        if (recentOnly) filters.Add("items.last_used>0");
        for (int i = 0; i < tags.Count; i++)
        {
            filters.Add($"EXISTS (SELECT 1 FROM tags t WHERE t.item_id=items.id AND (t.moment_id IS NULL OR t.moment_id IS search.moment_id) AND t.tag=$t{i})");
            args.Add(($"$t{i}", tags[i]));
        }
        string where = words.Count > 0 ? "search MATCH $q" : "1=1";
        if (filters.Count > 0) where += " AND " + string.Join(" AND ", filters);
        string order = recentOnly ? "items.last_used DESC" : words.Count > 0 ? "bm25(search), items.favorite DESC" : "items.favorite DESC, items.last_used DESC, items.path";
        string select = $"SELECT {ItemColumns}, search.moment_id FROM search JOIN items ON items.id = search.item_id";
        args.Add(("$limit", limit));
        List<(LibraryItem Item, long? MomentId)> ReadRows(string sql) =>
            Query(sql, r => (ReadItem(r), r.IsDBNull(12) ? (long?)null : r.GetInt64(12)), args.ToArray());

        if (words.Count > 0) args.Add(("$q", string.Join(" ", words)));
        var rows = ReadRows($"{select} WHERE {where} ORDER BY {order} LIMIT $limit");
        partialMatches = false;
        if (rows.Count == 0 && words.Count > 1 && role == LibraryRole.Footage)
        {
            args[^1] = ("$q", string.Join(" OR ", words));
            args.Add(("$terms", JsonSerializer.Serialize(words)));
            rows = ReadRows($"""
                WITH matches AS (
                    SELECT search.rowid AS id, count(*) AS word_count
                    FROM json_each($terms) AS term CROSS JOIN search
                    WHERE search MATCH term.value
                    GROUP BY search.rowid
                )
                {select} JOIN matches ON matches.id = search.rowid
                WHERE {where}
                ORDER BY matches.word_count DESC, {order}, search.rowid LIMIT $limit
                """);
            partialMatches = rows.Count > 0;
        }
        var hits = new List<SearchHit>();
        var seenItems = new HashSet<long>();
        foreach (var (item, momentId) in rows)
        {
            if (momentId is { } mid)
            {
                var moment = Moments(item.Id).FirstOrDefault(m => m.Id == mid);
                if (moment is not null) hits.Add(new SearchHit(item, moment));
            }
            else if (seenItems.Add(item.Id) && (tags.Count == 0 || tags.All(t => item.Tags.Contains(t))))
                hits.Add(new SearchHit(item, null));
        }
        // When the user searched with words, moments rank above whole files; otherwise files first.
        return words.Count > 0 || tags.Count > 0 ? hits : hits.Where(h => h.Moment is null).ToList();
    }

    public void Dispose()
    {
        foreach (var w in watchers) w.Dispose();
        db.Dispose();
    }
}
