using Microsoft.EntityFrameworkCore;
using Studio.Data;

namespace Studio.Services;

public sealed class StudioPaths
{
    public string Root { get; }
    public string Media { get; }
    public string Temp { get; }
    public StudioPaths(IConfiguration cfg)
    {
        Root = Path.GetFullPath(cfg["Studio:DataPath"] ?? ".studio-data");
        Media = Path.Combine(Root, "media"); Temp = Path.Combine(Root, "temp");
        Directory.CreateDirectory(Media); Directory.CreateDirectory(Temp);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
public sealed class AssetStore(StudioPaths paths, IDbContextFactory<StudioDb> factory)
{
    public string WorkDir(Guid jobId) { var p = Path.Combine(paths.Temp, jobId.ToString("N")); Directory.CreateDirectory(p); return p; }
    public async Task<AssetRow> Add(Guid projectId, string source, string name, string type, CancellationToken ct, string? attribution = null)
    {
        var asset = new AssetRow { ProjectId = projectId, FileName = Path.GetFileName(name), ContentType = type, Attribution = attribution };
        var directory = Path.Combine(paths.Media, projectId.ToString("N")); Directory.CreateDirectory(directory);
        asset.Path = Path.Combine(directory, asset.Id + Path.GetExtension(name).ToLowerInvariant());
        await using (var input = File.OpenRead(source))
        await using (var output = File.Create(asset.Path)) await input.CopyToAsync(output, ct);
        asset.Bytes = new FileInfo(asset.Path).Length;
        await using var db = await factory.CreateDbContextAsync(ct); db.Assets.Add(asset); await db.SaveChangesAsync(ct);
        return asset;
    }
    public async Task<AssetRow> Get(string id, Guid? projectId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var asset = await db.Assets.FindAsync(id) ?? throw new InvalidOperationException("Media không tồn tại.");
        if (projectId is not null && projectId != asset.ProjectId) throw new InvalidOperationException("Media thuộc dự án khác.");
        var full = Path.GetFullPath(asset.Path);
        if (!full.StartsWith(paths.Media + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) throw new InvalidOperationException("Media chưa có trên máy này.");
        return asset;
    }
    public async Task<List<AssetRow>> List(Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Assets.Where(a => a.ProjectId == projectId).ToListAsync();
    }
}
