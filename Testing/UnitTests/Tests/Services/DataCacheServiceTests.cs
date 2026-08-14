using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace UnitTests.Tests.Services;

[TestFixture]
public class DataCacheServiceTests
{
    [Test]
    public void GetOrCompute_CachesPerKey()
    {
        var service = new DataCacheService();
        var computeCalls = 0;

        var first = service.GetOrCompute("data:items", () => $"payload-{++computeCalls}");
        var second = service.GetOrCompute("data:items", () => $"payload-{++computeCalls}");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo("payload-1"));
            Assert.That(second, Is.EqualTo("payload-1"));
            Assert.That(computeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void GetOrComputeZlib_ReusesCompressedBytesForSameStringInstance()
    {
        var service = new DataCacheService();
        var output = new string('x', 1000) + "items-json";

        var first = service.GetOrComputeZlib(output);
        var second = service.GetOrComputeZlib(output);

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void GetOrComputeZlib_ReusesCompressedBytesForDifferentInstanceSameContent()
    {
        var service = new DataCacheService();
        var output = new string('x', 1000) + "items-json";
        var rebuilt = new string(output.ToCharArray());

        Assert.That(rebuilt, Is.Not.SameAs(output));
        var first = service.GetOrComputeZlib(output);
        var second = service.GetOrComputeZlib(rebuilt);

        // 内容寻址：实例重建/变化不影响缓存命中（修复引用键在运行时失效导致的每请求重压缩）
        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void GetOrComputeZlib_CompressedBytesRoundTrip()
    {
        var service = new DataCacheService();
        var output = "{\"err\":0,\"data\":[\"a\",\"b\",\"c\"]}";

        var compressed = service.GetOrComputeZlib(output);

        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(zlib, Encoding.UTF8);
        Assert.That(reader.ReadToEnd(), Is.EqualTo(output));
    }

    [Test]
    public void GetOrComputeUtf8_ReusesBytesForSameStringInstance()
    {
        var service = new DataCacheService();
        var output = new string('x', 1000) + "items-json";

        var first = service.GetOrComputeUtf8(output);
        var second = service.GetOrComputeUtf8(output);

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void GetOrComputeUtf8_ReusesBytesForDifferentInstanceSameContent()
    {
        var service = new DataCacheService();
        var output = new string('x', 1000) + "items-json";
        var rebuilt = new string(output.ToCharArray());

        Assert.That(rebuilt, Is.Not.SameAs(output));
        var first = service.GetOrComputeUtf8(output);
        var second = service.GetOrComputeUtf8(rebuilt);

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void GetOrCompute_InstanceRebuild_SameContentKeepsByteCache()
    {
        var service = new DataCacheService();
        // 主缓存 TTL 不可注入，反射替换为短 TTL 以触发实例重建
        var shortTtlCache = new RequestCoalescingCache(TimeSpan.FromMilliseconds(100));
        typeof(DataCacheService)
            .GetField("cache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, shortTtlCache);

        // 非 const：避免编译器字符串驻留导致两次 lambda 返回同一实例
        var payload = new string("items-v1-0123456789".ToCharArray());
        var first = service.GetOrCompute("data:items", () => new string(payload.ToCharArray()));
        var firstUtf8 = service.GetOrComputeUtf8(first);
        var firstZlib = service.GetOrComputeZlib(first);

        Thread.Sleep(150); // 主缓存过期，重建新实例（同内容）

        var second = service.GetOrCompute("data:items", () => payload);
        Assert.That(second, Is.Not.SameAs(first));

        // 内容寻址：新实例同内容 → 字节级缓存直接命中，返回同一数组
        Assert.Multiple(() =>
        {
            Assert.That(service.GetOrComputeUtf8(second), Is.SameAs(firstUtf8));
            Assert.That(service.GetOrComputeZlib(second), Is.SameAs(firstZlib));
        });
    }

    [Test]
    public void GetOrCompute_ReferenceFastPath_PopulatesRefCache()
    {
        var service = new DataCacheService();
        var output = new string('y', 500) + "payload";
        service.GetOrComputeZlib(output);
        service.GetOrComputeUtf8(output);

        var utf8Ref = (System.Collections.ICollection)typeof(DataCacheService)
            .GetField("_utf8RefCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        var zlibRef = (System.Collections.ICollection)typeof(DataCacheService)
            .GetField("_zlibRefCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        var zlibContent = (System.Collections.ICollection)typeof(DataCacheService)
            .GetField("_zlibCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;

        // 引用快速路径装配：同实例调用后引用表与内容表各 1 条，内容不重复缓存
        Assert.Multiple(() =>
        {
            Assert.That(utf8Ref.Count, Is.EqualTo(1));
            Assert.That(zlibRef.Count, Is.EqualTo(1));
            Assert.That(zlibContent.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void GetOrCompute_ContentChange_CreatesDistinctByteEntry()
    {
        var service = new DataCacheService();
        var oldContent = new string("items-v1".ToCharArray());
        var newContent = new string("items-v2".ToCharArray());

        var oldUtf8 = service.GetOrComputeUtf8(oldContent);
        var oldZlib = service.GetOrComputeZlib(oldContent);

        // 内容变化 → 新条目；旧条目由 TTL/清扫回收（此处仅断言不同内容有各自缓存条目）
        Assert.Multiple(() =>
        {
            Assert.That(service.GetOrComputeUtf8(newContent), Is.Not.SameAs(oldUtf8));
            Assert.That(service.GetOrComputeZlib(newContent), Is.Not.SameAs(oldZlib));
        });
    }
}
