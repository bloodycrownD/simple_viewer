// Responsibility: 图库 SQLite 索引实现——建库（WAL）、行级 Upsert/Remove/UpdateTags/ReplacePath、
//                 标签 OR 筛选（补空格 LIKE）、无标签筛选（untagged-filter-entry）、标签计数聚合、
//                 对账重建（文件系统为准）、损坏自动重建。
// Invariants: 单连接长驻（Dispose 关闭）；全部公共方法经 Task.Run 包裹同步 SQLite 调用（避免 UI 线程阻塞）；
//             SQLiteException 即删库文件（含 -wal/-shm）重建空库后重试一次（T-IX6）；
//             tags 列存 " t1 t2 "（左右补空格，无标签存空串）；sort_key 列以 '\u0001' 连接预分词 token
//             （Windows 文件名不允许控制字符，token 内不可能出现该分隔符）；
//             查询结果按 GalleryItemNaturalComparer 自然序排序（SQLite 字典序与自然序不一致，故在内存排序）。
// Call chain: 见 ILibraryIndexService 文件头。

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <inheritdoc cref="ILibraryIndexService" />
public sealed class LibraryIndexService : ILibraryIndexService
{
    /// <summary>索引 schema 版本（写入 meta 表；结构演进时递增并配迁移）。</summary>
    private const string SchemaVersion = "1";

    /// <summary>对账重建时分块写入/删除的批大小（对齐 <see cref="LibraryScanService.ChunkSize"/> 量级）。</summary>
    private const int RebuildBatchSize = 500;

    /// <summary>sort_key 列的 token 分隔符（文件名非法字符，token 内不会出现）。</summary>
    private const char SortKeySeparator = '\u0001';

    private readonly string _rootPath;
    private readonly ILibraryScanService _scanService;
    private readonly object _sync = new();
    private SqliteConnection? _connection;
    private bool _disposed;

    /// <param name="rootPath">图库根目录（用于派生库文件名哈希；RebuildAsync 未显式传 root 时回落它）。</param>
    /// <param name="indexDirectory">库文件所在目录；null 时用默认 <c>%LocalAppData%\SimpleViewer\index</c>。</param>
    /// <param name="scanService">对账重建用的扫描服务；null 时使用默认 <see cref="LibraryScanService"/>。</param>
    public LibraryIndexService(
        string rootPath,
        string? indexDirectory = null,
        ILibraryScanService? scanService = null)
    {
        _rootPath = rootPath;
        _scanService = scanService ?? new LibraryScanService();
        DatabaseFilePath = System.IO.Path.Combine(
            indexDirectory ?? DefaultIndexDirectory(),
            GetDatabaseFileName(rootPath));
    }

    /// <inheritdoc />
    public string DatabaseFilePath { get; }

    /// <summary>默认索引目录：%LocalAppData%\SimpleViewer\index。</summary>
    public static string DefaultIndexDirectory()
        => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleViewer",
            "index");

    /// <summary>
    /// 依据根路径派生库文件名：根路径规范化（小写、去尾随目录分隔符）的 SHA-256 前 16 个十六进制字符 + ".db"。
    /// 公开静态以便测试与外部诊断定位库文件。
    /// </summary>
    public static string GetDatabaseFileName(string rootPath)
    {
        var normalized = (rootPath ?? string.Empty).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + ".db";
    }

    /// <inheritdoc />
    public Task UpsertChunkAsync(IReadOnlyList<GalleryItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ThrowIfDisposed();
        if (items.Count == 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() => RunCommand(connection => UpsertCore(connection, items)), cancellationToken);
    }

    /// <inheritdoc />
    public Task RemovePathAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        return Task.Run(() => RunCommand(connection => RemovePathCore(connection, path)), cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearAllItemsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // DELETE 不带 WHERE 走全表清空；索引是可重建缓存，语义见接口注释。
        return Task.Run(() => RunCommand(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM items";
            command.ExecuteNonQuery();
        }), cancellationToken);
    }

    /// <inheritdoc />
    public Task UpdateTagsAsync(string path, IReadOnlyList<string> newTags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(newTags);
        ThrowIfDisposed();
        return Task.Run(
            () => RunCommand(connection => UpdateTagsCore(connection, path, ComposeTagsValue(newTags))),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task ReplacePathAsync(string oldPath, GalleryItem newItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newItem);
        ThrowIfDisposed();
        return Task.Run(
            () => RunCommand(connection => ReplacePathCore(connection, oldPath, newItem)),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GalleryItem>> QueryByTagsAsync(
        IReadOnlyList<string>? tags, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // 过滤空白并去重（同标签重复出现只需一个 OR 谓词）；全空 → 全量查询。
        var effective = tags?
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.Run(
            () => RunCommand(connection => QueryByTagsCore(connection, effective)),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GalleryItem>> QueryUntaggedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => RunCommand(QueryUntaggedCore), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, int>> TagCountsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run(() => RunCommand(TagCountsCore), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IndexRebuildResult> RebuildAsync(string root, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var rootPath = string.IsNullOrWhiteSpace(root) ? _rootPath : root;

        // 阶段 1（后台）：读库中现有全部 path 作为孤儿候选集。
        var existing = await Task.Run(() => RunCommand(ReadAllPathsCore), cancellationToken).ConfigureAwait(false);
        var orphans = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        // 阶段 2：流式扫描（文件系统为准），分块写入；命中磁盘项即从孤儿集合排除。
        var scanned = 0;
        var buffer = new List<GalleryItem>(RebuildBatchSize);
        async Task FlushAsync()
        {
            var batch = buffer.ToArray();
            buffer.Clear();
            await Task.Run(() => RunCommand(connection => UpsertCore(connection, batch)), cancellationToken)
                .ConfigureAwait(false);
            scanned += batch.Length;
        }

        await foreach (var item in _scanService.ScanAsync(rootPath, null, cancellationToken).ConfigureAwait(false))
        {
            buffer.Add(item);
            orphans.Remove(item.Path);
            if (buffer.Count >= RebuildBatchSize)
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }

        if (buffer.Count > 0)
        {
            await FlushAsync().ConfigureAwait(false);
        }

        // 阶段 3（后台）：剩余候选即孤儿（库中有、磁盘无），批量删除。
        var orphanList = orphans.ToArray();
        var removed = await Task.Run(() => RunCommand(connection => DeletePathsCore(connection, orphanList)), cancellationToken)
            .ConfigureAwait(false);

        return new IndexRebuildResult(scanned, removed);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
    }

    // ---------- 同步核心（全部在 lock 内执行） ----------

    /// <summary>
    /// 命令执行包装：取/建连接执行；遇 <see cref="SqliteException"/>（库文件损坏）时
    /// 删除库文件（含 -wal/-shm）重建空库，然后重试一次；再失败则异常上抛（真损坏且不可恢复）。
    /// </summary>
    private void RunCommand(Action<SqliteConnection> action)
    {
        lock (_sync)
        {
            try
            {
                action(GetConnectionLocked());
            }
            catch (SqliteException)
            {
                ResetDatabaseLocked();
                action(GetConnectionLocked());
            }
        }
    }

    /// <summary>同 <see cref="RunCommand(Action{SqliteConnection})"/> 的带返回值版本。</summary>
    private T RunCommand<T>(Func<SqliteConnection, T> action)
    {
        lock (_sync)
        {
            try
            {
                return action(GetConnectionLocked());
            }
            catch (SqliteException)
            {
                ResetDatabaseLocked();
                return action(GetConnectionLocked());
            }
        }
    }

    /// <summary>建目录并打开连接 + WAL + 建表 + 写 schema 版本（幂等，连接断开后重建时复用）。</summary>
    private SqliteConnection OpenAndInitialize()
    {
        var directory = System.IO.Path.GetDirectoryName(DatabaseFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFilePath,
                DefaultTimeout = 10,
                // 单连接长驻语义：禁用连接池，确保 Dispose 即真正关闭文件句柄（默认池化会延迟释放、锁住 db 文件）。
                Pooling = false,
            }.ToString());
        try
        {
            connection.Open();

            // WAL 为库文件持久属性，但重复执行无害；打开即执行，兼作库文件有效性的首次探测（损坏则在此抛 SqliteException）。
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }

            using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS items(
                    path TEXT PRIMARY KEY,
                    dir TEXT,
                    base_name TEXT,
                    ext TEXT,
                    tags TEXT,
                    w INTEGER,
                    h INTEGER,
                    sort_key TEXT);
                CREATE TABLE IF NOT EXISTS meta(
                    key TEXT PRIMARY KEY,
                    value TEXT);
                INSERT OR IGNORE INTO meta(key, value) VALUES('schema_version', '##VERSION##');
                """.Replace("##VERSION##", SchemaVersion);
            schema.ExecuteNonQuery();

            return connection;
        }
        catch
        {
            // 半开的连接（如 PRAGMA 探测到坏文件时）必须就地释放，否则文件句柄泄漏会锁住 db 文件、阻碍删除重建。
            connection.Dispose();
            throw;
        }
    }

    /// <summary>损坏恢复：关闭连接并删除库文件三件套（db/-wal/-shm），下次取连接时重建空库。</summary>
    private void ResetDatabaseLocked()
    {
        _connection?.Dispose();
        _connection = null;

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(DatabaseFilePath + suffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删除失败（被占用等）：继续尝试重开；若库文件仍损坏，第二次失败将上抛由调用方感知。
            }
        }
    }

    private SqliteConnection GetConnectionLocked()
        => _connection ??= OpenAndInitialize();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    // ---------- 各命令的同步实现 ----------

    /// <summary>批量 INSERT OR REPLACE（单事务包裹；命令与参数复用以降低批量分配）。</summary>
    private static void UpsertCore(SqliteConnection connection, IReadOnlyList<GalleryItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using (var command = CreateInsertCommand(connection, transaction))
        {
            foreach (var item in items)
            {
                BindItemParameters(command, item);
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static void RemovePathCore(SqliteConnection connection, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM items WHERE path = @path";
        command.Parameters.Add("@path", SqliteType.Text).Value = path;
        command.ExecuteNonQuery();
    }

    /// <summary>就地重写标签列（path 未变场景；path 已变请用 ReplacePathCore）。</summary>
    private static void UpdateTagsCore(SqliteConnection connection, string path, string tagsValue)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET tags = @tags WHERE path = @path";
        command.Parameters.Add("@tags", SqliteType.Text).Value = tagsValue;
        command.Parameters.Add("@path", SqliteType.Text).Value = path;
        command.ExecuteNonQuery();
    }

    /// <summary>行替换：单事务内删旧行 + 整行写入新项（打标即改名后的 path 变化场景）。</summary>
    private static void ReplacePathCore(SqliteConnection connection, string oldPath, GalleryItem newItem)
    {
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM items WHERE path = @old";
            delete.Parameters.Add("@old", SqliteType.Text).Value = oldPath;
            delete.ExecuteNonQuery();
        }

        using (var insert = CreateInsertCommand(connection, transaction))
        {
            BindItemParameters(insert, newItem);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// 标签筛选（OR）：参数化 LIKE + ESCAPE——模式为 "% tag %"（tags 列左右补空格，防子串误命中），
    /// 标签中的 LIKE 通配符（%/_/\）按字面转义。空标签集返回全量。
    /// 读出后按预分词自然排序 key 在内存排序（SQLite TEXT 字典序与自然序不一致）。
    /// </summary>
    private static IReadOnlyList<GalleryItem> QueryByTagsCore(SqliteConnection connection, string[]? effectiveTags)
    {
        var sql = new StringBuilder(
            "SELECT path, dir, base_name, ext, tags, w, h, sort_key FROM items");
        var hasFilter = effectiveTags is { Length: > 0 };
        if (hasFilter)
        {
            sql.Append(" WHERE ");
            for (var i = 0; i < effectiveTags!.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(" OR ");
                }

                sql.Append("tags LIKE @t").Append(i).Append(" ESCAPE '\\'");
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        if (hasFilter)
        {
            for (var i = 0; i < effectiveTags!.Length; i++)
            {
                command.Parameters.Add("@t" + i, SqliteType.Text).Value = "% " + EscapeLikePattern(effectiveTags[i]) + " %";
            }
        }

        var results = new List<GalleryItem>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                results.Add(ReadItem(reader));
            }
        }

        // 稳定排序（OrderBy 语义）保证同 key 项保持行序，对齐 D15 发现顺序口径。
        return results.OrderBy(static i => i, GalleryItemNaturalComparer.Instance).ToList();
    }

    /// <summary>
    /// 无标签筛选核心（untagged-filter-entry）：tags 列空串（正常写入口径）或 NULL（廉价防御）即命中；
    /// 读出后按预分词自然排序 key 在内存排序（对齐 QueryByTagsCore 尾部处理）。
    /// </summary>
    private static IReadOnlyList<GalleryItem> QueryUntaggedCore(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT path, dir, base_name, ext, tags, w, h, sort_key FROM items WHERE tags IS NULL OR tags = ''";

        var results = new List<GalleryItem>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                results.Add(ReadItem(reader));
            }
        }

        // 稳定排序（OrderBy 语义）保证同 key 项保持行序，对齐 D15 发现顺序口径。
        return results.OrderBy(static i => i, GalleryItemNaturalComparer.Instance).ToList();
    }

    /// <summary>每标签计数：全量读 tags 列后内存拆分聚合（几十万行一次性聚合可接受）。</summary>
    private static IReadOnlyDictionary<string, int> TagCountsCore(SqliteConnection connection)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tags FROM items";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            foreach (var tag in reader.GetString(0).Split(' '))
            {
                if (tag.Length > 0)
                {
                    counts[tag] = counts.TryGetValue(tag, out var current) ? current + 1 : 1;
                }
            }
        }

        // 给定展示友好序（计数降序、标签 OrdinalIgnoreCase 升序）；IReadOnlyDictionary 消费方不应依赖此序。
        var ordered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in counts.OrderByDescending(static kv => kv.Value)
                     .ThenBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            ordered[pair.Key] = pair.Value;
        }

        return ordered;
    }

    private static List<string> ReadAllPathsCore(SqliteConnection connection)
    {
        var paths = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM items";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    /// <summary>批量删除孤儿行（DELETE ... IN 分批，批大小不超过 SQLite 变量数安全上限）。</summary>
    private static int DeletePathsCore(SqliteConnection connection, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var batch in paths.Chunk(RebuildBatchSize))
        {
            using var command = connection.CreateCommand();
            var sql = new StringBuilder("DELETE FROM items WHERE path IN (");
            for (var i = 0; i < batch.Length; i++)
            {
                sql.Append(i == 0 ? "@p" : ", @p").Append(i);
            }

            sql.Append(')');
            command.CommandText = sql.ToString();
            for (var i = 0; i < batch.Length; i++)
            {
                command.Parameters.Add("@p" + i, SqliteType.Text).Value = batch[i];
            }

            deleted += command.ExecuteNonQuery();
        }

        return deleted;
    }

    // ---------- 行读写辅助 ----------

    /// <summary>创建 8 参数的 INSERT OR REPLACE 命令（不绑定值，供批量复用）。</summary>
    private static SqliteCommand CreateInsertCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO items(path, dir, base_name, ext, tags, w, h, sort_key)
            VALUES(@path, @dir, @base_name, @ext, @tags, @w, @h, @sort_key)
            """;
        command.Parameters.Add("@path", SqliteType.Text);
        command.Parameters.Add("@dir", SqliteType.Text);
        command.Parameters.Add("@base_name", SqliteType.Text);
        command.Parameters.Add("@ext", SqliteType.Text);
        command.Parameters.Add("@tags", SqliteType.Text);
        command.Parameters.Add("@w", SqliteType.Integer);
        command.Parameters.Add("@h", SqliteType.Integer);
        command.Parameters.Add("@sort_key", SqliteType.Text);
        return command;
    }

    /// <summary>把图库项绑定到 <see cref="CreateInsertCommand"/> 的参数槽位。</summary>
    private static void BindItemParameters(SqliteCommand command, GalleryItem item)
    {
        command.Parameters["@path"].Value = item.Path;
        command.Parameters["@dir"].Value = item.DirectoryName;
        command.Parameters["@base_name"].Value = item.BaseName;
        command.Parameters["@ext"].Value = item.Extension;
        command.Parameters["@tags"].Value = ComposeTagsValue(item.Tags);
        command.Parameters["@w"].Value = (long)item.Width;
        command.Parameters["@h"].Value = (long)item.Height;
        command.Parameters["@sort_key"].Value = string.Join(SortKeySeparator, item.SortKey);
    }

    /// <summary>tags 列值：" t1 t2 "（左右补空格供 LIKE '% tag %' 精确命中；无标签存空串）。</summary>
    private static string ComposeTagsValue(IReadOnlyList<string> tags)
        => tags.Count == 0 ? string.Empty : " " + string.Join(" ", tags) + " ";

    /// <summary>LIKE 模式转义：%/_/\ 前加反斜杠（配合 SQL 中的 ESCAPE '\' 子句按字面匹配）。</summary>
    private static string EscapeLikePattern(string tag)
    {
        var escaped = new StringBuilder(tag.Length + 8);
        foreach (var c in tag)
        {
            if (c is '%' or '_' or '\\')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    private static GalleryItem ReadItem(SqliteDataReader reader)
    {
        var tagsValue = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
        var sortKeyValue = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);

        return new GalleryItem
        {
            Path = reader.GetString(0),
            DirectoryName = reader.GetString(1),
            BaseName = reader.GetString(2),
            Extension = reader.GetString(3),
            Tags = tagsValue.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            Width = (int)reader.GetInt64(5),
            Height = (int)reader.GetInt64(6),
            FileSizeBytes = 0, // 索引表不含大小列；需要真实大小时由调用方按需查询文件系统
            SortKey = sortKeyValue.Length == 0
                ? Array.Empty<string>()
                : sortKeyValue.Split(SortKeySeparator),
        };
    }
}
