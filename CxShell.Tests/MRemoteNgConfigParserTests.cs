using System.Security.Cryptography;
using System.Text;
using CxShell.Models;
using CxShell.Services;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace CxShell.Tests;

public sealed class MRemoteNgConfigParserTests
{
    private const int Iterations = 1000;
    private const string DefaultPassword = "mR3m";

    [Fact]
    public void Parse_MapsContainersProtocolsAndInheritedCredentials()
    {
        var xml = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections Name="Connections" Export="false" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="{{Iterations}}" FullFileEncryption="false" Protected="{{Encrypt("ThisIsNotProtected", DefaultPassword)}}" ConfVersion="2.6">
              <Node Name="Lab" Type="Container" Descr="" Panel="General" Id="c1" Username="admin" Domain="CORP" Password="{{Encrypt("rootpw", DefaultPassword)}}" Port="3389" Protocol="RDP" InheritUsername="false" InheritPassword="false" InheritDomain="false" InheritPort="false" InheritProtocol="false">
                <Node Name="rdp-1" Type="Connection" Hostname="10.0.0.5" Port="3389" Protocol="RDP" Username="bob" InheritDomain="true" Password="{{Encrypt("bobpw", DefaultPassword)}}" Resolution="1920x1080" Colors="Colors32Bit" DisplayWallpaper="false" RedirectDiskDrives="true" RedirectSound="DoNotPlay" />
                <Node Name="ssh-inherited" Type="Connection" Hostname="10.0.0.6" Port="22" Protocol="SSH2" InheritUsername="true" InheritPassword="true" InheritDomain="true" />
                <Node Name="portal" Type="Connection" Hostname="portal.example.com" Port="443" Protocol="HTTPS" />
              </Node>
              <Node Name="vnc-1" Type="Connection" Hostname="10.0.0.9" Port="5900" Protocol="VNC" />
            </Connections>
            """;

        var document = MRemoteNgConfigParser.Parse(xml);

        Assert.Equal(2, document.Nodes.Count);

        var container = document.Nodes[0];
        Assert.Equal(MRemoteNgNodeKind.Container, container.Kind);
        Assert.Equal("Lab", container.Name);
        Assert.Equal(2, container.Children.Count);

        var rdp = container.Children[0].Session;
        Assert.NotNull(rdp);
        Assert.Equal(SessionProtocol.RDP, rdp.Protocol);
        Assert.Equal("10.0.0.5", rdp.Host);
        Assert.Equal(3389, rdp.Port);
        Assert.Equal(@"CORP\bob", rdp.Username);
        Assert.Equal("bobpw", rdp.Password);
        Assert.Equal("1920x1080", rdp.RdpWindowSize);
        Assert.Equal("32", rdp.RdpColorQuality);
        Assert.True(rdp.RdpDisableWallpaper);
        Assert.True(rdp.RdpRedirectDrives);
        Assert.Equal("DoNotPlay", rdp.RdpAudioMode);

        var ssh = container.Children[1].Session;
        Assert.NotNull(ssh);
        Assert.Equal(SessionProtocol.SSH, ssh.Protocol);
        Assert.Equal(@"CORP\admin", ssh.Username);
        Assert.Equal("rootpw", ssh.Password);

        var vnc = document.Nodes[1].Session;
        Assert.NotNull(vnc);
        Assert.Equal(SessionProtocol.VNC, vnc.Protocol);
        Assert.Equal(5900, vnc.Port);

        Assert.Equal(1, document.SkippedConnections);
        Assert.Equal(["HTTPS"], document.SkippedProtocols);
    }

    [Fact]
    public void Parse_AsksForMasterPasswordWhenOnlyTheUserPasswordOpensTheFile()
    {
        var protectedValue = Encrypt("ThisIsProtected", "s3cret");
        var xml = $$"""
            <Connections Name="Connections" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="{{Iterations}}" FullFileEncryption="false" Protected="{{protectedValue}}" ConfVersion="2.6">
              <Node Name="host" Type="Connection" Hostname="10.0.0.7" Port="22" Protocol="SSH2" Username="root" Password="{{Encrypt("hunter2", "s3cret")}}" />
            </Connections>
            """;

        Assert.Throws<MRemoteNgPasswordRequiredException>(() => MRemoteNgConfigParser.Parse(xml));
        Assert.Throws<MRemoteNgPasswordInvalidException>(() => MRemoteNgConfigParser.Parse(xml, "wrong"));

        var document = MRemoteNgConfigParser.Parse(xml, "s3cret");
        var session = Assert.Single(document.Nodes).Session;
        Assert.NotNull(session);
        Assert.Equal("hunter2", session.Password);
        Assert.Equal(0, document.PasswordFailures);
    }

    [Fact]
    public void Parse_ReadsFullyEncryptedBody()
    {
        var body = $"""
            <Node Name="db" Type="Connection" Hostname="10.0.0.8" Port="22" Protocol="SSH2" Username="dba" Password="{Encrypt("pg", DefaultPassword)}" />
            """;
        var encryptedBody = Encrypt(body, DefaultPassword);
        var xml = $$"""
            <Connections Name="Connections" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="{{Iterations}}" FullFileEncryption="true" Protected="{{Encrypt("ThisIsNotProtected", DefaultPassword)}}" ConfVersion="2.6">{{encryptedBody}}</Connections>
            """;

        var document = MRemoteNgConfigParser.Parse(xml);

        Assert.True(document.WasFullyEncrypted);
        var session = Assert.Single(document.Nodes).Session;
        Assert.NotNull(session);
        Assert.Equal("10.0.0.8", session.Host);
        Assert.Equal("pg", session.Password);
    }

    [Fact]
    public void Parse_ReportsUnsupportedProtocolsAndMissingHosts()
    {
        var xml = $$"""
            <Connections Name="Connections" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="{{Iterations}}" Protected="{{Encrypt("ThisIsNotProtected", DefaultPassword)}}" ConfVersion="2.6">
              <Node Name="app" Type="Connection" Protocol="IntApp" ExtApp="notepad.exe" />
              <Node Name="rdp-blank" Type="Connection" Hostname="" Port="3389" Protocol="RDP" />
            </Connections>
            """;

        var document = MRemoteNgConfigParser.Parse(xml);

        Assert.Empty(document.Nodes);
        Assert.Equal(2, document.SkippedConnections);
        Assert.Contains("IntApp", document.SkippedProtocols);
    }

    [Fact]
    public void Parse_AcceptsNamespacePrefixedRoot()
    {
        var xml = $$"""
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="{{Iterations}}" Protected="{{Encrypt("ThisIsNotProtected", DefaultPassword)}}" ConfVersion="2.9">
              <mrng:Node Name="nas" Type="Connection" Hostname="nas.lan" Port="22" Protocol="SSH2" Username="root" />
            </mrng:Connections>
            """;

        var document = MRemoteNgConfigParser.Parse(xml);

        var session = Assert.Single(document.Nodes).Session;
        Assert.NotNull(session);
        Assert.Equal("nas.lan", session.Host);
        Assert.Equal(SessionProtocol.SSH, session.Protocol);
    }

    [Fact]
    public void LooksLikeConfig_DistinguishesExportFormats()
    {
        Assert.True(MRemoteNgConfigParser.LooksLikeConfig(
            "<Connections Name=\"Connections\" ConfVersion=\"2.6\"></Connections>"));
        Assert.True(MRemoteNgConfigParser.LooksLikeConfig(
            "<mrng:Connections xmlns:mrng=\"http://mremoteng.org\"></mrng:Connections>"));
        Assert.False(MRemoteNgConfigParser.LooksLikeConfig("{\"Format\":\"CxShell.SessionExport\"}"));
        Assert.False(MRemoteNgConfigParser.LooksLikeConfig("Host server\n  HostName example.com"));
    }

    /// <summary>
    /// Mirrors mRemoteNG's AeadCryptographyProvider: PBKDF2/HMAC-SHA1 over the
    /// low byte of each password character, then AES-GCM with a random salt and
    /// nonce prepended, the salt also bound as associated data.
    /// </summary>
    private static string Encrypt(string plainText, string password)
    {
        var salt = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var nonce = Enumerable.Range(101, 16).Select(value => (byte)value).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password.Select(character => (byte)character).ToArray(),
            salt,
            Iterations,
            HashAlgorithmName.SHA1,
            32);

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce, salt));

        var plain = Encoding.UTF8.GetBytes(plainText);
        var output = new byte[cipher.GetOutputSize(plain.Length)];
        var length = cipher.ProcessBytes(plain, 0, plain.Length, output, 0);
        length += cipher.DoFinal(output, length);

        return Convert.ToBase64String([.. salt, .. nonce, .. output[..length]]);
    }
}
