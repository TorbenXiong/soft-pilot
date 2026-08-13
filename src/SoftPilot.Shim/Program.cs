using System.Diagnostics;
using Microsoft.Win32;

var invokedName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").ToLowerInvariant();
using var key = Registry.CurrentUser.OpenSubKey(@"Software\SoftPilot");
var root = key?.GetValue("Root") as string;
if (string.IsNullOrWhiteSpace(root))
{
    Console.Error.WriteLine("SoftPilot Root 未在 HKCU\\Software\\SoftPilot 中注册。");
    return 1;
}

var invocation = Resolve(invokedName, root);
if (!File.Exists(invocation.FileName))
{
    Console.Error.WriteLine($"{invokedName}: 尚未选择对应的 SoftPilot 全局版本。");
    return 1;
}

var startInfo = new ProcessStartInfo
{
    FileName = invocation.FileName,
    UseShellExecute = false,
};
foreach (var argument in invocation.PrefixArguments.Concat(args))
{
    startInfo.ArgumentList.Add(argument);
}

if (invokedName is "python" or "python3" or "pip")
{
    startInfo.Environment["PYTHONHOME"] = null;
}

using var process = Process.Start(startInfo);
if (process is null)
{
    Console.Error.WriteLine($"无法启动 {invocation.FileName}。");
    return 1;
}

await process.WaitForExitAsync();
return process.ExitCode;

static ShimInvocation Resolve(string name, string root) => name switch
{
    "node" => new(Path.Combine(root, "current", "node", "node.exe"), []),
    "npm" => new(Path.Combine(root, "current", "node", "node.exe"),
        [Path.Combine(root, "current", "node", "node_modules", "npm", "bin", "npm-cli.js")]),
    "npx" => new(Path.Combine(root, "current", "node", "node.exe"),
        [Path.Combine(root, "current", "node", "node_modules", "npm", "bin", "npx-cli.js")]),
    "java" => new(Path.Combine(root, "current", "java", "bin", "java.exe"), []),
    "javac" => new(Path.Combine(root, "current", "java", "bin", "javac.exe"), []),
    "python" or "python3" => new(Path.Combine(root, "current", "python", "python.exe"), []),
    "pip" => new(Path.Combine(root, "current", "python", "python.exe"), ["-m", "pip"]),
    _ => throw new InvalidOperationException($"未知 SoftPilot shim 名称：{name}"),
};

internal sealed record ShimInvocation(string FileName, IReadOnlyList<string> PrefixArguments);
