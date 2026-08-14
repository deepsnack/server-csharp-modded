using System.Collections;

namespace SPTarkov.Server.Core.Utils.Json;

public class LazyLoad<T>(Func<T> deserialize)
{
    private readonly object _lock = new();
    private readonly List<Func<T?, T?>> _lazyLoadTransformers = [];
    private T? _cached;
    private bool _hasCached;

    /// <summary>
    /// Adds a transformer to modify the value during lazy loading. Transformers execute
    /// in registration order on every access, reading the underlying store live at call time
    /// (e.g. mod admin configs), so no invalidation is needed after registration.
    /// </summary>
    /// <param name="transformer">Function that transforms the value</param>
    public void AddTransformer(Func<T?, T?> transformer)
    {
        lock (_lock)
        {
            _lazyLoadTransformers.Add(transformer);
        }
    }

    public T? Value
    {
        get
        {
            lock (_lock)
            {
                // 只反序列化一次并缓存；抛异常时不置位 _hasCached，下次访问重试。
                // 之前每次访问都全量重跑 deserialize（locale 表级 3MB 文件 → 每次 1.6GB 分配），
                // 是服务端 GC 风暴 / CPU 尖峰的直接来源。
                if (!_hasCached)
                {
                    _cached = deserialize();
                    _hasCached = true;
                }

                if (_lazyLoadTransformers.Count == 0)
                {
                    // 无 transformer：直接返回缓存引用，零分配（绝大多数场景）。
                    return _cached;
                }

                // 有 transformer：浅克隆缓存再依次重放——transformer 假定每次从"原始反序列化状态"开始
                // （第三方 mod 如 Couturier 用 Dictionary.Add 而非索引器赋值；旧实现每次新建字典所以
                // Add 永不撞键）。克隆后重放与旧版语义完全等价：Add 不撞键、后注册覆盖先注册（同键时
                // 后注册 Add 会抛，与旧版行为一致）。transformer 每次实时读 store，配置改动即时生效。
                var result = CloneForTransformers(_cached);
                foreach (var transform in _lazyLoadTransformers)
                {
                    result = transform(result);
                }

                return result;
            }
        }
    }

    /// <summary>浅克隆字典/列表供 transformer 重放；不可克隆类型（类实例等）直接返回缓存引用。</summary>
    private static T? CloneForTransformers(T? source)
    {
        if (source is IDictionary dict)
        {
            try
            {
                var clone = (IDictionary)Activator.CreateInstance(dict.GetType())!;
                foreach (DictionaryEntry entry in dict)
                {
                    clone.Add(entry.Key, entry.Value);
                }

                return (T)clone;
            }
            catch
            {
                // 克隆失败（无无参构造等）：退化为共享引用，宁可少克隆也不崩服务端。
                return source;
            }
        }

        if (source is IList list)
        {
            try
            {
                var clone = (IList)Activator.CreateInstance(list.GetType())!;
                foreach (var item in list)
                {
                    clone.Add(item);
                }

                return (T)clone;
            }
            catch
            {
                return source;
            }
        }

        return source;
    }
}
