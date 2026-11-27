using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using CxShell.Models;

namespace CxShell.Services;

public enum MRemoteNgNodeKind
{
    Container,
    Connection
}

/// <summary>One entry of an mRemoteNG connection tree, mapped onto CxShell types.</summary>
public sealed class MRemoteNgNode
{
    public string Name { get; init; } = string.Empty;
    public MRemoteNgNodeKind Kind { get; init; }
    public IReadOnlyList<MRemoteNgNode> Children { get; init; } = [];
    public SessionInfo? Session { get; init; }
}

/// <summary>Result of reading a confCons.xml file, including what was dropped.</summary>
public sealed class MRemoteNgImportDocument
{
    public IReadOnlyList<MRemoteNgNode> Nodes { get; init; } = [];
    public int SkippedConnections { get; init; }
    public IReadOnlyList<string> SkippedProtocols { get; init; } = [];
    public int PasswordFailures { get; init; }
    public bool WasFullyEncrypted { get; init; }

    public int ConnectionCount => Count(Nodes);

    private static int Count(IReadOnlyList<MRemoteNgNode> nodes)
    {
        var total = 0;
        foreach (var node in nodes)
        {
            if (node.Kind == MRemoteNgNodeKind.Connection)
                total++;
            total += Count(node.Children);
        }

        return total;
    }
}

/// <summary>The file carries a master password and the caller has not supplied it yet.</summary>
public sealed class MRemoteNgPasswordRequiredException(string message) : Exception(message);

/// <summary>A master password was supplied but it does not open the file.</summary>
public sealed class MRemoteNgPasswordInvalidException(string message) : Exception(message);

/// <summary>The file is not a readable mRemoteNG configuration.</summary>
public sealed class MRemoteNgFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Reads mRemoteNG's confCons.xml (the file behind <c>%APPDATA%\mRemoteNG</c>,
/// also produced by its "Export to XML" action) into a tree of sessions and
/// groups.
///
/// The file is XML with a <c>&lt;Connections&gt;</c> root whose direct children
/// are <c>&lt;Node&gt;</c> elements of <c>Type="Container"</c> or
/// <c>Type="Connection"</c>. Credentials live on the nodes: the username and
/// domain in clear text, the password as a base64 blob encrypted with the file
/// password (the built-in "mR3m" when no master password was set). A node may
/// inherit password/username/domain/port/protocol from its container instead of
/// carrying its own value, so the walk carries the resolved parent values down.
///
/// Only the protocols CxShell implements are imported; HTTP, external tools,
/// PowerShell/WSL/Terminal, AnyDesk and RAW entries are counted as skipped so
/// the caller can say so instead of silently dropping them.
/// </summary>
public static class MRemoteNgConfigParser
{
    public static bool LooksLikeConfig(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.Length == 0 || trimmed[0] != '<')
            return false;

        return trimmed.Contains("ConfVersion", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("mremoteng.org", StringComparison.OrdinalIgnoreCase);
    }

    public static MRemoteNgImportDocument Parse(string text, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var root = LoadRoot(text);
        if (!root.Name.LocalName.Equals("Connections", StringComparison.OrdinalIgnoreCase))
        {
            throw new MRemoteNgFormatException(
                "The XML root is not an mRemoteNG <Connections> element.");
        }

        var engine = Attribute(root, "EncryptionEngine");
        var mode = Attribute(root, "BlockCipherMode");
        var iterations = IntAttribute(root, "KdfIterations", 1000);
        if (!MRemoteNgCrypto.IsSupportedEngine(engine) || !MRemoteNgCrypto.IsSupportedMode(mode))
        {
            throw new MRemoteNgFormatException(
                $"The file is encrypted with {engine}/{mode}, which only mRemoteNG itself can read.");
        }

        // Newer builds can hand the key to Windows DPAPI; that ciphertext is
        // bound to the Windows account that wrote it, so there is nothing to
        // derive here even on Windows.
        if (Attribute(root, "KeyProtection").Equals("Dpapi", StringComparison.OrdinalIgnoreCase))
        {
            throw new MRemoteNgFormatException(
                "The file is protected with Windows DPAPI. Re-save it in mRemoteNG with a password instead of DPAPI.");
        }

        var effectivePassword = ResolvePassword(root, password, engine, mode, iterations);
        var wasFullyEncrypted = BoolAttribute(root, "FullFileEncryption");
        var body = wasFullyEncrypted
            ? DecryptBody(root, password, effectivePassword, engine, mode, iterations)
            : root;

        var context = new ParseContext(engine, mode, iterations, effectivePassword);
        var nodes = new List<MRemoteNgNode>();
        foreach (var element in body.Elements())
        {
            if (!element.Name.LocalName.Equals("Node", StringComparison.OrdinalIgnoreCase))
                continue;

            var node = ParseNode(element, null, context);
            if (node != null)
                nodes.Add(node);
        }

        // Nothing opened with the built-in password: the file very likely has a
        // master password that the user has not typed yet.
        if (context.PasswordFailures > 0 && string.IsNullOrEmpty(password) && nodes.Count > 0)
            throw new MRemoteNgPasswordRequiredException(
                "The mRemoteNG file is protected and its passwords could not be read without the master password.");

        return new MRemoteNgImportDocument
        {
            Nodes = nodes,
            SkippedConnections = context.SkippedConnections,
            SkippedProtocols = context.SkippedProtocols.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            PasswordFailures = context.PasswordFailures,
            WasFullyEncrypted = wasFullyEncrypted
        };
    }

    private static XElement LoadRoot(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var document = XDocument.Load(reader);
            return document.Root ?? throw new MRemoteNgFormatException("The XML document is empty.");
        }
        catch (XmlException exception)
        {
            throw new MRemoteNgFormatException("The file is not valid XML.", exception);
        }
    }

    private static string ResolvePassword(
        XElement root,
        string? suppliedPassword,
        string engine,
        string mode,
        int iterations)
    {
        var protectedValue = Attribute(root, "Protected");
        if (string.IsNullOrEmpty(protectedValue))
            return string.IsNullOrEmpty(suppliedPassword) ? MRemoteNgCrypto.DefaultPassword : suppliedPassword;

        if (!string.IsNullOrEmpty(suppliedPassword))
        {
            if (TryReadProtectionMarker(protectedValue, suppliedPassword, engine, mode, iterations) != null)
                return suppliedPassword;

            // A file saved without a master password still accepts the built-in
            // key, so only report a bad password once that fails as well.
            if (TryReadProtectionMarker(protectedValue, MRemoteNgCrypto.DefaultPassword, engine, mode, iterations)
                == "ThisIsNotProtected")
            {
                return MRemoteNgCrypto.DefaultPassword;
            }

            throw new MRemoteNgPasswordInvalidException("The mRemoteNG master password is not correct.");
        }

        var marker = TryReadProtectionMarker(protectedValue, MRemoteNgCrypto.DefaultPassword, engine, mode, iterations);
        if (marker == null || marker.Equals("ThisIsProtected", StringComparison.Ordinal))
        {
            throw new MRemoteNgPasswordRequiredException(
                "The mRemoteNG file is protected with a master password.");
        }

        return MRemoteNgCrypto.DefaultPassword;
    }

    private static string? TryReadProtectionMarker(
        string protectedValue,
        string password,
        string engine,
        string mode,
        int iterations)
    {
        try
        {
            return MRemoteNgCrypto.Decrypt(protectedValue, password, engine, mode, iterations);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static XElement DecryptBody(
        XElement root,
        string? suppliedPassword,
        string effectivePassword,
        string engine,
        string mode,
        int iterations)
    {
        var encryptedBody = root.Value.Trim();
        string innerXml;
        try
        {
            innerXml = MRemoteNgCrypto.Decrypt(encryptedBody, effectivePassword, engine, mode, iterations);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw string.IsNullOrEmpty(suppliedPassword)
                ? new MRemoteNgPasswordRequiredException(
                    "The mRemoteNG file is fully encrypted; the master password is required.")
                : new MRemoteNgPasswordInvalidException("The mRemoteNG master password is not correct.");
        }

        try
        {
            return XElement.Parse($"<mRemoteNgBody>{innerXml}</mRemoteNgBody>", LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new MRemoteNgFormatException("The decrypted mRemoteNG content is not valid XML.", exception);
        }
    }

    private static MRemoteNgNode? ParseNode(XElement element, NodeValues? parent, ParseContext context)
    {
        var name = Attribute(element, "Name").Trim();
        var values = ResolveValues(element, parent, context);
        var isContainer = Attribute(element, "Type").Equals("Container", StringComparison.OrdinalIgnoreCase);

        if (isContainer)
        {
            var children = new List<MRemoteNgNode>();
            foreach (var child in element.Elements())
            {
                if (!child.Name.LocalName.Equals("Node", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parsed = ParseNode(child, values, context);
                if (parsed != null)
                    children.Add(parsed);
            }

            return new MRemoteNgNode
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Container" : name,
                Kind = MRemoteNgNodeKind.Container,
                Children = children
            };
        }

        if (!TryMapProtocol(values.Protocol, out var protocol))
        {
            context.Skip(values.Protocol);
            return null;
        }

        var host = Attribute(element, "Hostname").Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            context.Skip(string.IsNullOrEmpty(values.Protocol) ? "no host" : $"{values.Protocol} (no host)");
            return null;
        }

        var session = new SessionInfo
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? host : name,
            Host = host,
            Protocol = protocol,
            Port = values.Port > 0 ? values.Port : DefaultPort(protocol),
            Username = CombineDomain(values.Domain, values.Username),
            Password = values.Password,
            CreatedAt = DateTime.Now
        };

        if (protocol == SessionProtocol.RDP)
            ApplyRdpOptions(element, session);

        return new MRemoteNgNode
        {
            Name = session.Name,
            Kind = MRemoteNgNodeKind.Connection,
            Session = session
        };
    }

    /// <summary>
    /// Resolves the values a connection may take from its container. mRemoteNG
    /// writes the container's values on the container and marks the children
    /// with <c>InheritX="true"</c>; the child's own attributes then hold
    /// defaults that must not win.
    /// </summary>
    private static NodeValues ResolveValues(XElement element, NodeValues? parent, ParseContext context)
    {
        var protocol = Attribute(element, "Protocol");
        if (BoolAttribute(element, "InheritProtocol") && parent != null)
            protocol = parent.Protocol;
        if (string.IsNullOrEmpty(protocol) && parent != null)
            protocol = parent.Protocol;

        var port = IntAttribute(element, "Port", 0);
        if ((BoolAttribute(element, "InheritPort") || port <= 0) && parent != null)
            port = parent.Port;
        if (port <= 0)
            port = DefaultPort(protocol);

        var username = Attribute(element, "Username");
        if (BoolAttribute(element, "InheritUsername") && parent != null)
            username = parent.Username;

        var domain = Attribute(element, "Domain");
        if (BoolAttribute(element, "InheritDomain") && parent != null)
            domain = parent.Domain;

        var password = BoolAttribute(element, "InheritPassword") && parent != null
            ? parent.Password
            : DecryptPassword(element, context);

        return new NodeValues(protocol, port, username, domain, password);
    }

    private static string DecryptPassword(XElement element, ParseContext context)
    {
        var encrypted = Attribute(element, "Password");
        if (string.IsNullOrEmpty(encrypted))
            return string.Empty;

        try
        {
            return MRemoteNgCrypto.Decrypt(
                encrypted,
                context.Password,
                context.Engine,
                context.Mode,
                context.Iterations);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            context.PasswordFailures++;
            return string.Empty;
        }
    }

    private static void ApplyRdpOptions(XElement element, SessionInfo session)
    {
        var colors = Attribute(element, "Colors");
        if (colors.StartsWith("Colors", StringComparison.OrdinalIgnoreCase))
        {
            var bits = colors[6..];
            if (bits.EndsWith("Bit", StringComparison.OrdinalIgnoreCase))
                bits = bits[..^3];

            session.RdpColorQuality = bits.Equals("256", StringComparison.OrdinalIgnoreCase) ? "8" : bits;
        }

        if (element.Attribute("DisplayWallpaper") != null)
            session.RdpDisableWallpaper = !BoolAttribute(element, "DisplayWallpaper");

        if (BoolAttribute(element, "RedirectDiskDrives"))
            session.RdpRedirectDrives = true;

        session.RdpAudioMode = Attribute(element, "RedirectSound") switch
        {
            "BringToThisComputer" => "PlayLocal",
            "LeaveAtRemoteComputer" => "PlayRemote",
            _ => "DoNotPlay"
        };

        var resolution = Attribute(element, "Resolution");
        if (resolution.Equals("Fullscreen", StringComparison.OrdinalIgnoreCase))
        {
            session.RdpWindowSize = "FullScreen";
        }
        else if (resolution.Equals("FitToWindow", StringComparison.OrdinalIgnoreCase) ||
                 resolution.Equals("SmartSize", StringComparison.OrdinalIgnoreCase))
        {
            session.RdpWindowSize = "WorkSpace";
        }
        else if (TryParseResolution(resolution, out var width, out var height))
        {
            session.RdpWindowSize = $"{width}x{height}";
            session.RdpDesktopWidth = width;
            session.RdpDesktopHeight = height;
        }
    }

    private static bool TryParseResolution(string value, out int width, out int height)
    {
        width = 0;
        height = 0;
        var separator = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (separator <= 0)
            return false;

        return int.TryParse(value.AsSpan(0, separator), out width) &&
               int.TryParse(value.AsSpan(separator + 1), out height) &&
               width >= 200 &&
               height >= 200;
    }

    private static bool TryMapProtocol(string? value, out SessionProtocol protocol)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "RDP":
                protocol = SessionProtocol.RDP;
                return true;
            case "VNC":
            case "ARD":
                protocol = SessionProtocol.VNC;
                return true;
            case "SSH1":
            case "SSH2":
                protocol = SessionProtocol.SSH;
                return true;
            case "TELNET":
                protocol = SessionProtocol.TELNET;
                return true;
            case "RLOGIN":
                protocol = SessionProtocol.RLOGIN;
                return true;
            default:
                protocol = SessionProtocol.SSH;
                return false;
        }
    }

    private static int DefaultPort(SessionProtocol protocol) => protocol switch
    {
        SessionProtocol.RDP => 3389,
        SessionProtocol.VNC => 5900,
        SessionProtocol.TELNET => 23,
        SessionProtocol.RLOGIN => 513,
        _ => 22
    };

    private static int DefaultPort(string? protocolValue) =>
        TryMapProtocol(protocolValue, out var protocol) ? DefaultPort(protocol) : 0;

    private static string CombineDomain(string domain, string username)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(username))
            return username;

        if (username.Contains('\\') || username.Contains('@'))
            return username;

        return $"{domain}\\{username}";
    }

    private static string Attribute(XElement element, string name) =>
        (string?)element.Attribute(name) ?? string.Empty;

    private static bool BoolAttribute(XElement element, string name) =>
        bool.TryParse((string?)element.Attribute(name), out var value) && value;

    private static int IntAttribute(XElement element, string name, int fallback) =>
        int.TryParse((string?)element.Attribute(name), out var value) ? value : fallback;

    private sealed record NodeValues(
        string Protocol,
        int Port,
        string Username,
        string Domain,
        string Password);

    private sealed class ParseContext(string engine, string mode, int iterations, string password)
    {
        private readonly HashSet<string> _skippedProtocols = new(StringComparer.OrdinalIgnoreCase);

        public string Engine { get; } = engine;
        public string Mode { get; } = mode;
        public int Iterations { get; } = iterations;
        public string Password { get; } = password;
        public int SkippedConnections { get; private set; }
        public int PasswordFailures { get; set; }
        public IReadOnlyCollection<string> SkippedProtocols => _skippedProtocols;

        public void Skip(string? protocol)
        {
            SkippedConnections++;
            if (!string.IsNullOrWhiteSpace(protocol))
                _skippedProtocols.Add(protocol.Trim());
        }
    }
}
