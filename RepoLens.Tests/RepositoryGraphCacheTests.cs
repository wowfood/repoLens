using DevContext.Configuration;
using DevContext.Infrastructure;
using DevContext.Services;

namespace DevContext.Tests;

[TestClass]
public sealed class RepositoryGraphCacheTests
{
    [TestMethod]
    public async Task Cache_HitsForIdenticalInputsAndInvalidatesForSourceChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dev-context-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var source = Path.Combine(root, "Sample.cs");
        await File.WriteAllTextAsync(source, "namespace Sample; public class First { }");

        try
        {
            var runner = new ProcessRunner();
            var service = new RepositoryGraphService(runner, new ProjectIndexer(runner));
            var config = new DevContextConfig { Cache = new CacheConfig { Enabled = true } };

            var first = await service.BuildAsync(root, config, CancellationToken.None);
            var second = await service.BuildAsync(root, config, CancellationToken.None);
            await File.WriteAllTextAsync(source, "namespace Sample; public class Second { }");
            var changed = await service.BuildAsync(root, config, CancellationToken.None);

            Assert.IsFalse(first.CacheHit);
            Assert.IsTrue(second.CacheHit);
            Assert.AreEqual(first.InputHash, second.InputHash);
            Assert.IsFalse(changed.CacheHit);
            Assert.AreNotEqual(first.InputHash, changed.InputHash);
            Assert.IsTrue(changed.Symbols.Symbols.Any(symbol => symbol.Name == "Second"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
