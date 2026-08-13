namespace SoftPilot.Application;

public class SoftPilotException : Exception
{
    public SoftPilotException(string message)
        : base(message)
    {
    }

    public SoftPilotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class IntegrityException : SoftPilotException
{
    public IntegrityException(string message)
        : base(message)
    {
    }
}

public sealed class RuntimeNotFoundException : SoftPilotException
{
    public RuntimeNotFoundException(RuntimeKind kind, string version)
        : base($"找不到 {kind.ToString().ToLowerInvariant()}@{version}。")
    {
    }
}
