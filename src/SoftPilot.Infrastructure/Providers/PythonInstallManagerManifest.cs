using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace SoftPilot.Infrastructure.Providers;

internal sealed record PythonInstallManagerManifest(
    string PackageName,
    string Publisher,
    Version Version,
    Uri PackageUri)
{
    private const string ExpectedPackageName = "PythonSoftwareFoundation.PythonManager";
    private const string ExpectedPublisher =
        "CN=Python Software Foundation, O=Python Software Foundation, L=Beaverton, S=Oregon, C=US";
    private static readonly Uri OfficialDirectory = new("https://www.python.org/ftp/python/pymanager/");
    private static readonly Uri OfficialAppInstallerUri = new(OfficialDirectory, "pymanager.appinstaller");

    public static PythonInstallManagerManifest ParseAppInstaller(string path)
    {
        var document = LoadXml(path);
        var root = document.Root
            ?? throw new IntegrityException("Python Install Manager AppInstaller 缺少根元素。");
        if (!string.Equals(root.Name.LocalName, "AppInstaller", StringComparison.Ordinal))
        {
            throw new IntegrityException("Python Install Manager AppInstaller 根元素无效。");
        }

        var appInstallerUri = ParseRequiredUri(root, "Uri");
        ValidateOfficialUri(appInstallerUri, OfficialAppInstallerUri.AbsolutePath);

        var appInstallerVersion = ParseRequiredVersion(root, "Version");
        var packages = root.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "MainPackage", StringComparison.Ordinal))
            .ToArray();
        if (packages.Length != 1)
        {
            throw new IntegrityException("Python Install Manager AppInstaller 必须包含一个 MainPackage。");
        }

        var package = packages[0];
        var name = ReadRequiredAttribute(package, "Name");
        var publisher = ReadRequiredAttribute(package, "Publisher");
        var architecture = ReadRequiredAttribute(package, "ProcessorArchitecture");
        var version = ParseRequiredVersion(package, "Version");
        var packageUri = ParseRequiredUri(package, "Uri");
        if (!string.Equals(name, ExpectedPackageName, StringComparison.Ordinal)
            || !string.Equals(publisher, ExpectedPublisher, StringComparison.Ordinal)
            || !string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase)
            || version != appInstallerVersion)
        {
            throw new IntegrityException("Python Install Manager AppInstaller 的包身份无效。");
        }

        ValidateOfficialUri(packageUri);
        var fileName = Path.GetFileName(packageUri.AbsolutePath);
        var expectedPrefix = $"python-manager-{version.Major}.{version.Minor}";
        if (!string.Equals(fileName, expectedPrefix + ".msix", StringComparison.Ordinal))
        {
            throw new IntegrityException("Python Install Manager AppInstaller 的包文件名与版本不一致。");
        }

        return new PythonInstallManagerManifest(name, publisher, version, packageUri);
    }

    public void ValidatePackageArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var manifestEntry = archive.Entries.SingleOrDefault(entry =>
            string.Equals(entry.FullName, "AppxManifest.xml", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null || manifestEntry.Length > 1024 * 1024)
        {
            throw new IntegrityException("Python Install Manager MSIX 缺少有效的 AppxManifest.xml。");
        }

        XDocument document;
        using (var stream = manifestEntry.Open())
        using (var reader = XmlReader.Create(stream, SecureXmlSettings()))
        {
            document = XDocument.Load(reader, LoadOptions.None);
        }

        var identity = document.Descendants().SingleOrDefault(element =>
            string.Equals(element.Name.LocalName, "Identity", StringComparison.Ordinal))
            ?? throw new IntegrityException("Python Install Manager MSIX 缺少包身份。");
        var name = ReadRequiredAttribute(identity, "Name");
        var publisher = ReadRequiredAttribute(identity, "Publisher");
        var architecture = ReadRequiredAttribute(identity, "ProcessorArchitecture");
        var version = ParseRequiredVersion(identity, "Version");
        if (!string.Equals(name, PackageName, StringComparison.Ordinal)
            || !string.Equals(publisher, Publisher, StringComparison.Ordinal)
            || !string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase)
            || version != Version)
        {
            throw new IntegrityException("Python Install Manager MSIX 的包身份与 AppInstaller 不一致。");
        }
    }

    private static XDocument LoadXml(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 128 * 1024)
        {
            throw new IntegrityException("Python Install Manager AppInstaller 文件无效。");
        }

        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, SecureXmlSettings());
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static XmlReaderSettings SecureXmlSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 1024 * 1024,
    };

    private static string ReadRequiredAttribute(XElement element, string name) =>
        element.Attribute(name)?.Value is { Length: > 0 } value
            ? value
            : throw new IntegrityException($"Python Install Manager 元数据缺少 {name}。");

    private static Version ParseRequiredVersion(XElement element, string name) =>
        System.Version.TryParse(ReadRequiredAttribute(element, name), out var version)
            ? version
            : throw new IntegrityException($"Python Install Manager 元数据的 {name} 无效。");

    private static Uri ParseRequiredUri(XElement element, string name) =>
        Uri.TryCreate(ReadRequiredAttribute(element, name), UriKind.Absolute, out var uri)
            ? uri
            : throw new IntegrityException($"Python Install Manager 元数据的 {name} 无效。");

    private static void ValidateOfficialUri(Uri uri, string? exactPath = null)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, OfficialDirectory.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != OfficialDirectory.Port
            || !uri.AbsolutePath.StartsWith(OfficialDirectory.AbsolutePath, StringComparison.Ordinal)
            || exactPath is not null && !string.Equals(uri.AbsolutePath, exactPath, StringComparison.Ordinal))
        {
            throw new IntegrityException($"拒绝非 python.org 官方地址：{uri.GetLeftPart(UriPartial.Path)}");
        }
    }
}
