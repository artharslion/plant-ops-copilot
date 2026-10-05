using Microsoft.Extensions.Configuration;
using PlantOps.Application.Knowledge;
using Microsoft.Extensions.Hosting;
namespace PlantOps.Infrastructure.Knowledge;
public sealed class LocalDocumentFileStore(IConfiguration config, IHostEnvironment environment) : IDocumentFileStore
{
    private string PathFor(string key)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new InvalidOperationException("Invalid storage key.");
        var root = config["Documents:StorageRoot"];
        if (string.IsNullOrWhiteSpace(root))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Configure Documents:StorageRoot.");
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlantOpsCopilot", "documents");
        }
        root = System.IO.Path.GetFullPath(root); Directory.CreateDirectory(root); return System.IO.Path.Combine(root, key);
    }
    public async Task<string> PutAsync(byte[] content)
    {
        var key = Guid.NewGuid().ToString("N"); var path = PathFor(key);
        try { await using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await f.WriteAsync(content); await f.FlushAsync(); return key; }
        catch { File.Delete(path); throw; }
    }
    public Task<byte[]> ReadAsync(string key) => File.ReadAllBytesAsync(PathFor(key));
    public Task RemoveAsync(string key) { File.Delete(PathFor(key)); return Task.CompletedTask; }
}
