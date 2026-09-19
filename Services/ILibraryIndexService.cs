// Responsibility: 图库 SQLite 索引服务契约——行级增删改、标签 OR 筛选、无标签筛选（untagged-filter-entry）、标签计数、对账重建。
// Invariants: 标签事实源永远是文件名，索引为可随时删除重建的缓存（D2）；tags 列空格分隔存储、
//             查询采用左右补空格的 LIKE 写法（防子串误命中）；空标签列表查询返回全量；
//             结果按预分词自然排序 key 排序（D15）；库文件损坏自动删除重建空库；单连接长驻（WAL）。
// Call chain: MainViewModel（Step 7 打开图库）→ UpsertChunkAsync 渐进写入；TagService 打标后 ReplacePathAsync 行更新；
//             Step 9 标签栏 → TagCountsAsync 计数；Step 11 筛选条 → QueryByTagsAsync / QueryUntaggedAsync 驱动瀑布流。

using SimpleViewer.Models;

namespace SimpleViewer.Services;

/// <summary>
/// 图库本地索引（SQLite，WAL 模式；D2：纯缓存，事实源永远是文件名，可随时删除重建）。
/// 库文件默认落位 <c>%LocalAppData%\SimpleViewer\index\</c>，
/// 文件名 = 根路径规范化（小写、去尾随目录分隔符）的 SHA-256 前 16 个十六进制字符 + <c>".db"</c>；
/// 目录可通过构造参数注入（便于测试）。
/// 所有方法均为异步：内部以 <c>Task.Run</c> 包裹同步 SQLite 调用，避免阻塞 UI 线程。
/// </summary>
public interface ILibraryIndexService : IDisposable
{
    /// <summary>库文件完整路径（含注入目录；测试可据此定位/损坏 db 文件）。</summary>
    string DatabaseFilePath { get; }

    /// <summary>
    /// 批量插入或替换行（以 path 为主键，INSERT OR REPLACE，单事务包裹）。
    /// 扫描管线（<see cref="ILibraryScanService"/>）的分块写入入口。
    /// </summary>
    /// <param name="items">待写入的图库项分块（空列表为无操作）。</param>
    /// <param name="cancellationToken">取消令牌（仅在任务调度前生效；进行中的 SQLite 写入不可中断）。</param>
    Task UpsertChunkAsync(IReadOnlyList<GalleryItem> items, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空 items 全表（2026-09-17 走查修复：重开图库时索引缓存全量重建）。
    /// 索引是可丢弃缓存、事实源是文件名——打开图库即重扫全量，
    /// 若不清表，上一轮的孤儿行（改名/删除前的旧 path）会污染候选集与计数
    /// （曾表现为删除回执"成功 1 失败 1"且侧栏计数残留）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ClearAllItemsAsync(CancellationToken cancellationToken = default);

    /// <summary>删除指定 path 的行（文件被删除/移出图库时调用）。</summary>
    /// <param name="path">要删除的文件全路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RemovePathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 就地重写指定行的标签列（path 未变化的场景，如外部修正文件名后重新解析）。
    /// 注意：打标即改名——文件名变化后 path 已变，应改用 <see cref="ReplacePathAsync"/>。
    /// </summary>
    /// <param name="path">目标行路径。</param>
    /// <param name="newTags">新的标签全集（整列替换，非增量）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task UpdateTagsAsync(string path, IReadOnlyList<string> newTags, CancellationToken cancellationToken = default);

    /// <summary>
    /// 打标/重命名后的行替换：单事务内删除旧行并以新项整行写入（path 变化场景的正解，
    /// 保证旧行消失、新行的 tags/base_name 等字段以 <paramref name="newItem"/> 为准）。
    /// </summary>
    /// <param name="oldPath">改名前的旧路径（该行将被删除；不存在也无妨）。</param>
    /// <param name="newItem">改名后的新图库项（整行以它为准写入）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ReplacePathAsync(string oldPath, GalleryItem newItem, CancellationToken cancellationToken = default);

    /// <summary>
    /// 标签筛选（OR 语义）：命中任一标签即返回；标签列表为 null 或空（或全为空白）时返回全量。
    /// 匹配采用 tags 列左右补空格的 <c>LIKE '% tag %'</c> 填充写法——防子串误命中（如"风"不命中"风景"）；
    /// 参数化构造并转义 LIKE 通配符，防注入（标签含 <c>_</c>/<c>%</c> 时按字面匹配）。
    /// 结果按预分词自然排序 key 排序返回（D15；文件大小不在索引表中，返回项的 <c>FileSizeBytes</c> 为 0）。
    /// </summary>
    /// <param name="tags">筛选标签集（OR）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<GalleryItem>> QueryByTagsAsync(IReadOnlyList<string>? tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// 无标签筛选（untagged-filter-entry，2026-09-19）：返回 tags 列为空（无任何标签）的全部图库项。
    /// SQL 谓词 <c>tags IS NULL OR tags = ''</c>（IS NULL 为廉价防御——正常写入无标签恒为空串）。
    /// 结果按预分词自然排序 key 排序返回（对齐 <see cref="QueryByTagsAsync"/> 口径）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<GalleryItem>> QueryUntaggedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 每标签计数：从 tags 列全量读取后内存拆分聚合（几十万行一次性聚合可接受）。
    /// 无标签的行不产生任何计数项。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyDictionary<string, int>> TagCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 对账重建：以文件系统为准（经注入的 <see cref="ILibraryScanService"/> 递归扫描），
    /// 磁盘存在的项逐块写入（插入或刷新，宽高/标签过期数据一并纠正），
    /// 库中存在而磁盘不存在的孤儿行删除。
    /// </summary>
    /// <param name="root">扫描根目录（空/null 时回落构造时的根路径）。</param>
    /// <param name="cancellationToken">取消令牌（贯穿扫描枚举全程）。</param>
    /// <returns>重建统计：磁盘扫描写入行数与删除的孤儿行数。</returns>
    Task<IndexRebuildResult> RebuildAsync(string root, CancellationToken cancellationToken = default);
}

/// <summary>对账重建（<see cref="ILibraryIndexService.RebuildAsync"/>）的结果统计。</summary>
/// <param name="Scanned">磁盘扫描发现并写入索引的行数（含插入与刷新）。</param>
/// <param name="Removed">库中存在而磁盘不存在的孤儿行删除数。</param>
public readonly record struct IndexRebuildResult(int Scanned, int Removed);
