namespace SoftPilot.Application;

public sealed record RuntimeModulePreferences(
    bool NodeEnabled,
    bool JavaEnabled,
    bool PythonEnabled)
{
    public static RuntimeModulePreferences Default { get; } = new(true, true, true);

    public bool IsEnabled(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Node => NodeEnabled,
        RuntimeKind.Java => JavaEnabled,
        RuntimeKind.Python => PythonEnabled,
        _ => false,
    };
}
