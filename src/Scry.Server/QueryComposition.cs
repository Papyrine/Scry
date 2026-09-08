/// <summary>
/// How an operator is composed onto a query: the <see cref="Queryable"/> method closed over its type
/// arguments, and the provider asked for the query the call describes. Each is answered from a cache
/// after the first time. A request composes several operators, and closing a generic method or asking
/// a provider's untyped <c>CreateQuery</c> each reflect per call — a cloned argument array and a probe
/// of the runtime's instantiation table for the one, and for the other a walk of the expression's type
/// for its element, the same closing again, and a reflective invoke.
/// </summary>
static class QueryComposition
{
    /// <summary>
    /// Builds a call to a generic <see cref="Queryable"/> method. Each method name is used with exactly
    /// one overload shape here, so the first call lets the framework's name-based binder pick the right
    /// overload, then caches its open generic definition. Later calls close that definition over
    /// <paramref name="typeArgs"/> — once per closing — and bind directly, skipping the metadata scan
    /// and overload resolution the name-based
    /// <see cref="Expression.Call(Type, string, Type[], Expression[])"/> does on every invocation.
    /// </summary>
    public static MethodCallExpression Call(string method, Type[] typeArgs, params Expression[] arguments)
    {
        if (queryableMethods.TryGetValue(method, out var open))
        {
            return Expression.Call(Close(open, typeArgs), arguments);
        }

        var call = Expression.Call(typeof(Queryable), method, typeArgs, arguments);
        queryableMethods.TryAdd(method, call.Method.GetGenericMethodDefinition());
        return call;
    }

    static readonly ConcurrentDictionary<string, MethodInfo> queryableMethods = new();

    /// <summary>
    /// A call that folds a sequence to one value. A generic fold — Min, Max — closes over the element
    /// type; the others — Sum, Average — have one overload per numeric type, which the element type
    /// picks. The name-based binder resolves each once per method and element, and the closed method
    /// it found is kept.
    /// </summary>
    public static MethodCallExpression Fold(string method, bool generic, IQueryable values)
    {
        var key = (method, values.ElementType);
        if (folds.TryGetValue(key, out var closed))
        {
            return Expression.Call(closed, values.Expression);
        }

        var call = Expression.Call(typeof(Queryable), method, generic ? [values.ElementType] : null, values.Expression);
        folds.TryAdd(key, call.Method);
        return call;
    }

    static readonly ConcurrentDictionary<(string Method, Type Element), MethodInfo> folds = new();

    /// <summary>
    /// Asks a query's provider for the query <paramref name="call"/> describes, through the provider's
    /// generic <c>CreateQuery&lt;T&gt;</c>. The untyped overload every provider also offers is a
    /// convenience over that one: it finds the element type, closes the generic method over it, and
    /// invokes it reflectively — on every operator of every request. The closed method is held here
    /// as a delegate instead, once per element type.
    /// </summary>
    public static IQueryable Compose(IQueryable query, MethodCallExpression call)
    {
        var element = Schema.CollectionElement(call.Type) ??
                      throw new InvalidOperationException($"'{call.Method.Name}' does not return a sequence.");
        return Cached(creators, element, Creator)(query.Provider, call);
    }

    static readonly ConcurrentDictionary<Type, Func<IQueryProvider, Expression, IQueryable>> creators = new();

    // Reached through a static of this class's own rather than bound directly: CreateQuery<T> is a
    // generic interface method, and the runtime refuses an open-instance delegate over one of those.
    // The provider is the delegate's first argument, so one delegate per element type serves every
    // provider — EF's and an in-memory source's alike.
    static IQueryable Create<T>(IQueryProvider provider, Expression expression) =>
        provider.CreateQuery<T>(expression);

    static readonly MethodInfo create = typeof(QueryComposition).GetMethod(nameof(Create), BindingFlags.NonPublic | BindingFlags.Static)!;

    static Func<IQueryProvider, Expression, IQueryable> Creator(Type element) =>
        create.MakeGenericMethod(element).CreateDelegate<Func<IQueryProvider, Expression, IQueryable>>();

    /// <summary>Closes a generic method definition over its type arguments, once per closing.</summary>
    public static MethodInfo Close(MethodInfo open, Type arg) =>
        Cached(closedMethods, new(open, arg), InstantiateMethod);

    public static MethodInfo Close(MethodInfo open, Type arg0, Type arg1) =>
        Cached(closedMethods, new(open, arg0, arg1), InstantiateMethod);

    static MethodInfo Close(MethodInfo open, Type[] args) =>
        args.Length switch
        {
            1 => Close(open, args[0]),
            2 => Close(open, args[0], args[1]),
            4 => Cached(closedMethods, new(open, args[0], args[1], args[2], args[3]), InstantiateMethod),
            _ => open.MakeGenericMethod(args)
        };

    /// <summary>
    /// Closes a generic type definition — <c>Nullable&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c>,
    /// <c>IGrouping&lt;TKey, TElement&gt;</c> — over its type arguments, once per closing.
    /// </summary>
    public static Type Close(Type open, Type arg) =>
        Cached(closedTypes, new(open, arg), InstantiateType);

    public static Type Close(Type open, Type arg0, Type arg1) =>
        Cached(closedTypes, new(open, arg0, arg1), InstantiateType);

    static MethodInfo InstantiateMethod(Closing key) =>
        ((MethodInfo) key.Definition).MakeGenericMethod(key.Arguments);

    static Type InstantiateType(Closing key) =>
        ((Type) key.Definition).MakeGenericType(key.Arguments);

    static readonly ConcurrentDictionary<Closing, MethodInfo> closedMethods = new();
    static readonly ConcurrentDictionary<Closing, Type> closedTypes = new();

    // A closing is its definition and up to four arguments, held as fields rather than as an array so
    // the key compares by value and a lookup allocates nothing. The definitions are the fixed set the
    // builder emits; the arguments are source types, member types, object[], and the row types a
    // deduplicated projection is closed over.
    readonly record struct Closing(MemberInfo Definition, Type Arg0, Type? Arg1 = null, Type? Arg2 = null, Type? Arg3 = null)
    {
        public Type[] Arguments =>
            Arg1 is null ? [Arg0] :
            Arg2 is null ? [Arg0, Arg1] :
            [Arg0, Arg1, Arg2, Arg3!];
    }

    // A row type is closed over whatever member types a client projected, in the order it projected
    // them, so the closings a caller can produce are many. Growth stops here, and a closing arriving
    // past the limit is made per request — which is what every closing was before these caches, so a
    // caller who fills them degrades the service to its previous behavior rather than to a new failure.
    const int limit = 4096;

    static TValue Cached<TKey, TValue>(ConcurrentDictionary<TKey, TValue> cache, TKey key, Func<TKey, TValue> make)
        where TKey : notnull
    {
        if (cache.TryGetValue(key, out var found))
        {
            return found;
        }

        var made = make(key);
        if (cache.Count < limit)
        {
            cache[key] = made;
        }

        return made;
    }
}
