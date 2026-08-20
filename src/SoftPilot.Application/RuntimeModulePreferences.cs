namespace SoftPilot.Application;

public sealed record RuntimeModulePreferences(
    bool NodeEnabled,
    bool JavaEnabled,
    bool PythonEnabled,
    string Language = "en-US",
    IReadOnlyList<ModuleKind>? ModuleOrder = null,
    bool RedisEnabled = true,
    bool MySqlEnabled = true,
    bool GitEnabled = true)
{
    private static readonly ModuleKind[] DefaultOrder =
    [
        ModuleKind.Node,
        ModuleKind.Java,
        ModuleKind.Python,
        ModuleKind.Redis,
        ModuleKind.MySql,
        ModuleKind.Git,
    ];

    public static RuntimeModulePreferences Default { get; } = new(
        true,
        true,
        true,
        "en-US",
        DefaultOrder);

    public bool IsEnabled(ModuleKind kind) => kind switch
    {
        ModuleKind.Node => NodeEnabled,
        ModuleKind.Java => JavaEnabled,
        ModuleKind.Python => PythonEnabled,
        ModuleKind.Redis => RedisEnabled,
        ModuleKind.MySql => MySqlEnabled,
        ModuleKind.Git => GitEnabled,
        _ => false,
    };

    public IReadOnlyList<ModuleKind> GetModuleOrder()
    {
        var order = (ModuleOrder ?? [])
            .Where(kind => DefaultOrder.Contains(kind))
            .Distinct()
            .ToList();
        order.AddRange(DefaultOrder.Where(kind => !order.Contains(kind)));
        return order;
    }
}

public enum ModuleKind
{
    Node,
    Java,
    Python,
    Redis,
    MySql,
    Git,
}
