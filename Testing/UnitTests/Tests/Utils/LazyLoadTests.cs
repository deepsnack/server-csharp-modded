using NUnit.Framework;
using SPTarkov.Server.Core.Utils.Json;

namespace UnitTests.Tests.Utils;

[TestFixture]
public class LazyLoadTests
{
    [Test]
    public void DeserializeRunsOnlyOnceAcrossRepeatedReads()
    {
        var deserializeCalls = 0;

        var lazy = new LazyLoad<Dictionary<string, string>>(() =>
        {
            deserializeCalls++;
            return new Dictionary<string, string> { ["k"] = "v" };
        });

        for (var i = 0; i < 10; i++)
        {
            Assert.That(lazy.Value!["k"], Is.EqualTo("v"));
        }

        Assert.That(deserializeCalls, Is.EqualTo(1));
    }

    [Test]
    public void TransformersReplayOnEveryRead()
    {
        var deserializeCalls = 0;
        var transformCalls = 0;

        var lazy = new LazyLoad<Dictionary<string, string>>(() =>
        {
            deserializeCalls++;
            return new Dictionary<string, string>();
        });
        lazy.AddTransformer(dict =>
        {
            transformCalls++;
            dict!["transformed"] = "yes";
            return dict;
        });

        for (var i = 0; i < 3; i++)
        {
            Assert.That(lazy.Value!["transformed"], Is.EqualTo("yes"));
        }

        Assert.That(deserializeCalls, Is.EqualTo(1));
        Assert.That(transformCalls, Is.EqualTo(3));
    }

    [Test]
    public void TransformerRegisteredAfterFirstReadAppliesToSubsequentReads()
    {
        var lazy = new LazyLoad<List<string>>(() => ["base"]);

        Assert.That(lazy.Value!, Is.EqualTo(new List<string> { "base" }));

        lazy.AddTransformer(list => list!.Concat(["late"]).ToList());
        Assert.That(lazy.Value!, Is.EqualTo(new List<string> { "base", "late" }));
    }

    [Test]
    public void TransformersApplyInRegistrationOrder()
    {
        var lazy = new LazyLoad<List<string>>(() => []);
        lazy.AddTransformer(list => list!.Append("first").ToList());
        lazy.AddTransformer(list => list!.Append("second").ToList());

        Assert.That(lazy.Value!, Is.EqualTo(new List<string> { "first", "second" }));
    }

    [Test]
    public void FailedDeserializeIsRetriedOnNextRead()
    {
        var attempts = 0;

        var lazy = new LazyLoad<string>(() =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("transient failure");
            }

            return "ok";
        });

        Assert.Throws<InvalidOperationException>(() => _ = lazy.Value);
        Assert.That(lazy.Value, Is.EqualTo("ok"));
        Assert.That(attempts, Is.EqualTo(2));
    }

    [Test]
    public void TransformerUsingDictionaryAddSurvivesReplay()
    {
        // 生态兼容回归：第三方 mod（如 Couturier）的 transformer 用 Dictionary.Add 而非索引器赋值，
        // 假定每次从"原始反序列化状态"开始。缓存后每次访问必须从克隆重放，Add 永不撞键。
        var lazy = new LazyLoad<Dictionary<string, string>>(() => new Dictionary<string, string>());
        lazy.AddTransformer(dict =>
        {
            dict!.Add("66eeef8b2a166b73dc0671e8 FullName", "Couturier");
            return dict;
        });

        for (var i = 0; i < 3; i++)
        {
            Assert.That(lazy.Value!["66eeef8b2a166b73dc0671e8 FullName"], Is.EqualTo("Couturier"));
        }
    }

    [Test]
    public void TransformerWritesAreReappliedOnEveryRead()
    {
        // transformer 每次从缓存克隆开始重放：注入的键每次读取都存在（旧版每次全新字典语义）。
        var lazy = new LazyLoad<Dictionary<string, string>>(() => new Dictionary<string, string> { ["original"] = "1" });
        lazy.AddTransformer(dict =>
        {
            dict!["injected"] = "yes";
            return dict;
        });

        for (var i = 0; i < 3; i++)
        {
            Assert.That(lazy.Value!["injected"], Is.EqualTo("yes"));
            Assert.That(lazy.Value!["original"], Is.EqualTo("1"));
        }
    }
}
