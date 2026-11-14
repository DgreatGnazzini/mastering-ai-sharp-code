using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace PriorAuthBot.Eval;

/// <summary>
/// An <see cref="IDistributedCache"/> on disk, one file per entry (chapter 9). Behind
/// <c>AI:CacheResponses</c> and <c>AI:CacheEmbeddings</c>, PriorAuthBot's own pipeline then reuses
/// every answer and embedding from an earlier run whose request was identical: same prompt, same
/// model, same options. A rerun after a change to one prompt calls the model only for what changed,
/// and an unchanged rerun calls it not at all, which is what makes an evaluation repeatable.
/// Expiration is ignored: the key already changes whenever the request does.
/// </summary>
internal sealed class FileDistributedCache : IDistributedCache
{
    private readonly string _directory;

    public FileDistributedCache(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    public byte[]? Get(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            Misses++;
            return null;
        }

        Hits++;
        return File.ReadAllBytes(path);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            Misses++;
            return null;
        }

        Hits++;
        return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => File.WriteAllBytes(PathFor(key), value);

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        File.WriteAllBytesAsync(PathFor(key), value, token);

    public void Refresh(string key)
    {
    }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key) => File.Delete(PathFor(key));

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }

    // The keys MEAI builds are already hashes, but hashing again keeps file names short and safe on every OS.
    private string PathFor(string key) =>
        Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".bin");
}
