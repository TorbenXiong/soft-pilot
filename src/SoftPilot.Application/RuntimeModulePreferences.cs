namespace SoftPilot.Application;

public sealed record RuntimeModulePreferences(
    bool NodeEnabled,
    bool JavaEnabled,
    bool PythonEnabled,
    string Language = "en-US",
    IReadOnlyList<RuntimeKind>? ModuleOrder = null,
    bool RedisEnabled = true)
{
    private static readonly RuntimeKind[] DefaultOrder =
    [
        RuntimeKind.Node,
        RuntimeKind.Java,
        RuntimeKind.Python,
        RuntimeKind.Redis,
    ];

    public static RuntimeModulePreferences Default { get; } = new(
        true,
        true,
        true,
        "en-US",
        DefaultOrder);

    public bool IsEnabled(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Node => NodeEnabled,
        RuntimeKind.Java => JavaEnabled,
        RuntimeKind.Python => PythonEnabled,
        RuntimeKind.Redis => RedisEnabled,
        _ => false,
    };

    public IReadOnlyList<RuntimeKind> GetModuleOrder()
    {
        var order = (ModuleOrder ?? [])
            .Where(kind => DefaultOrder.Contains(kind))
            .Distinct()
            .ToList();
        order.AddRange(DefaultOrder.Where(kind => !order.Contains(kind)));
        return order;
    }
}
