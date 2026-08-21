namespace SoftPilot.Application;

public enum EnvironmentVariableScope
{
    User,
    Machine,
}

public sealed record EnvironmentVariableEntry(
    string Name,
    string Value,
    EnvironmentVariableScope Scope);
